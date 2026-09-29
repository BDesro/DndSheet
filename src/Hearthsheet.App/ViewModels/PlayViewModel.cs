using System.Collections.ObjectModel;
using System.Windows.Input;
using Hearthsheet.App.Mvvm;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Rules;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.App.ViewModels;

/// <summary>
/// Gameplay dashboard actions. Each command calls a domain operation (Character.TakeDamage,
/// RestService.Rest, …) and records the outcome; nothing here edits UI state directly.
/// </summary>
public sealed class PlayViewModel : Observable
{
    private const int MaxLogEntries = 200;
    private readonly CharacterViewModel _owner;
    private readonly RestService _rest;
    private readonly Dialogs _dialogs;
    private readonly ILogger _log;

    public PlayViewModel(CharacterViewModel owner, RestService rest, Dialogs dialogs, ILogger log)
    {
        _owner = owner;
        _rest = rest;
        _dialogs = dialogs;
        _log = log;

        Damage = new RelayCommand(p => Act($"Damage {Amount(p)}", c => c.TakeDamage(Amount(p))), p => Amount(p) > 0);
        Heal = new RelayCommand(p => Act($"Heal {Amount(p)}", c => c.Heal(Amount(p))), p => Amount(p) > 0);
        SetHp = new RelayCommand(() => Act($"Set HP {HpInput}", c =>
        {
            c.SetHitPoints(HpInput);
            return $"HP set to {c.Combat.CurrentHp}.";
        }));
        AddTempHp = new RelayCommand(() => Act($"Temp HP {HpInput}", c =>
        {
            c.GrantTemporaryHitPoints(HpInput);
            return $"Temporary HP now {c.Combat.TemporaryHp}.";
        }), () => HpInput > 0);
        DeathSave = new RelayCommand<DeathSaveResult>(r => Act($"Death save {r}", c => c.RecordDeathSave(r)));
        ShortRest = new RelayCommand(DoShortRest);
        LongRest = new RelayCommand(DoLongRest);
        UseResource = new RelayCommand<Resource>(r => Act($"Use {r.Name}", c =>
            c.UseResource(r) ? $"Used {r.Name} ({r.Current}/{r.Maximum} left)." : $"{r.Name} has no uses left."));
        RestoreResource = new RelayCommand<Resource>(r => Act($"Restore {r.Name}", c =>
        {
            c.RestoreResource(r);
            return $"{r.Name}: {r.Current}/{r.Maximum}.";
        }));
        ToggleSlot = new RelayCommand<SlotPip>(p => Act($"Slot level {p.Level}", c =>
        {
            if (p.IsExpended) c.RestoreSpellSlot(p.Level);
            else c.ExpendSpellSlot(p.Level);
            return $"Level {p.Level} slots: {c.Spellcasting.SlotLevel(p.Level).Available}/{c.Spellcasting.SlotLevel(p.Level).Maximum} available.";
        }));
        CastSpell = new RelayCommand<Spell>(s => Act($"Cast {s.Name}", c => c.CastSpell(s)));
        CastRitual = new RelayCommand<Spell>(s => Act($"Ritual {s.Name}", c => c.CastSpell(s, asRitual: true)), s => s.Ritual);
        DropConcentration = new RelayCommand(() => Act("Drop concentration", c =>
        {
            var was = c.Spellcasting.Concentration;
            c.Spellcasting.Concentration = "";
            return $"Stopped concentrating on {was}.";
        }), () => Character.Spellcasting.Concentration.Length > 0);
        ChangeExhaustion = new RelayCommand(p => Act("Exhaustion", c =>
        {
            c.ExhaustionLevel += p as string == "-1" ? -1 : 1;
            return $"Exhaustion level {c.ExhaustionLevel}.";
        }));
        RemoveEffect = new RelayCommand<Effect>(e => Act($"Remove effect {e.Name}", c =>
        {
            c.Effects.Remove(e);
            return $"Effect ended: {e.Name}.";
        }));
        ToggleInspiration = new RelayCommand(() => Act("Inspiration", c =>
        {
            c.Identity.Inspiration = !c.Identity.Inspiration;
            return c.Identity.Inspiration ? "Gained inspiration." : "Used inspiration.";
        }));

        QuickAddItem = new RelayCommand(() => QuickAdd("Add item", null, (c, r) =>
        {
            c.Items.Add(new Item { Name = r.Name, Description = r.Description });
            return $"Added item: {r.Name}.";
        }));
        QuickAddFeature = new RelayCommand(p => QuickAdd(p as string == "Trait" ? "Add trait" : "Add feature",
            Lookups.FeatureSources.Cast<object>().ToList(), (c, r) =>
            {
                c.Features.Add(new Feature { Name = r.Name, Source = (FeatureSource)r.Kind!, Description = r.Description });
                return $"Added {r.Kind}: {r.Name}.";
            }, p as string == "Trait" ? FeatureSource.Trait : FeatureSource.Class));
        QuickAddProficiency = new RelayCommand(() => QuickAdd("Add proficiency", Lookups.ProficiencyCategories.Cast<object>().ToList(), (c, r) =>
        {
            c.Proficiencies.Add(new Proficiency { Name = r.Name, Category = (ProficiencyCategory)r.Kind! });
            return $"Added {r.Kind} proficiency: {r.Name}.";
        }, ProficiencyCategory.Weapon, withDescription: false));
        QuickAddSpell = new RelayCommand(() => QuickAdd("Add spell (level)", Lookups.SpellLevels.Cast<object>().ToList(), (c, r) =>
        {
            c.Spellcasting.Spells.Add(new Spell { Name = r.Name, Level = (int)r.Kind!, Prepared = true, Description = r.Description });
            return $"Added spell: {r.Name}.";
        }, 1));
        QuickAddEffect = new RelayCommand(() => QuickAdd("Add effect", Lookups.EffectExpiries.Cast<object>().ToList(), (c, r) =>
        {
            c.Effects.Add(new Effect { Name = r.Name, Expiry = (EffectExpiry)r.Kind!, Description = r.Description });
            return $"Effect added: {r.Name}.";
        }, EffectExpiry.Manual));
        QuickAddResource = new RelayCommand(() => QuickAdd("Add resource (recovers on)", Lookups.RecoveryTimings.Cast<object>().ToList(), (c, r) =>
        {
            c.Resources.Add(new Resource { Name = r.Name, Recovery = (RecoveryTiming)r.Kind!, Maximum = 1, Current = 1, Notes = r.Description });
            return $"Resource added: {r.Name} (edit maximum in Details or on the dashboard).";
        }, RecoveryTiming.LongRest));
    }

    private Character Character => _owner.Character;

    public ObservableCollection<string> ActivityLog { get; } = [];

    /// <summary>Amount typed into the HP box, used by Set HP / Temp HP and the custom Damage/Heal buttons.</summary>
    public int HpInput { get; set => Set(ref field, Math.Clamp(value, 0, 9999)); } = 5;

    public IEnumerable<Spell> PreparedSpells => Character.Spellcasting.Spells
        .Where(s => s.Prepared || s.Level == 0).OrderBy(s => s.Level).ThenBy(s => s.Name);

    public ICommand Damage { get; }
    public ICommand Heal { get; }
    public ICommand SetHp { get; }
    public ICommand AddTempHp { get; }
    public ICommand DeathSave { get; }
    public ICommand ShortRest { get; }
    public ICommand LongRest { get; }
    public ICommand UseResource { get; }
    public ICommand RestoreResource { get; }
    public ICommand ToggleSlot { get; }
    public ICommand CastSpell { get; }
    public ICommand CastRitual { get; }
    public ICommand DropConcentration { get; }
    public ICommand ChangeExhaustion { get; }
    public ICommand RemoveEffect { get; }
    public ICommand ToggleInspiration { get; }
    public ICommand QuickAddItem { get; }
    public ICommand QuickAddFeature { get; }
    public ICommand QuickAddProficiency { get; }
    public ICommand QuickAddSpell { get; }
    public ICommand QuickAddEffect { get; }
    public ICommand QuickAddResource { get; }

    internal void Refresh() => Raise(nameof(PreparedSpells));

    /// <summary>A button's CommandParameter ("5") or, with no parameter, the typed amount.</summary>
    private int Amount(object? parameter) =>
        parameter is string s && int.TryParse(s, out var n) ? n : parameter is int i ? i : HpInput;

    private void Act(string action, Func<Character, string> operation)
    {
        _log.LogDebug("Gameplay action: {Action}", action);
        Record(operation(Character));
    }

    public void Record(string message)
    {
        ActivityLog.Insert(0, $"{DateTime.Now:HH:mm}  {message}");
        while (ActivityLog.Count > MaxLogEntries) ActivityLog.RemoveAt(ActivityLog.Count - 1);
    }

    private void QuickAdd(string title, IReadOnlyList<object>? kinds, Func<Character, QuickAddResult, string> add,
        object? defaultKind = null, bool withDescription = true)
    {
        var result = _dialogs.QuickAdd(title, kinds, defaultKind, withDescription);
        if (result is null) return;
        Act(title, c => add(c, result));
    }

    private void DoShortRest()
    {
        if (!_dialogs.ShortRest(this, Character)) return;
        Act("Short Rest", c => Describe(_rest.Rest(c, RestKind.Short)));
    }

    private void DoLongRest()
    {
        if (!_dialogs.Confirm("Take a long rest? HP, hit dice, spell slots and long-rest resources will be restored.", "Long Rest"))
            return;
        Act("Long Rest", c => Describe(_rest.Rest(c, RestKind.Long)));
    }

    /// <summary>Spends one hit die during a short rest. Used by the short rest dialog.</summary>
    public void SpendHitDie(int roll) => Act($"Spend hit die ({roll})", c =>
    {
        var healed = c.SpendHitDie(roll);
        return $"Spent a hit die (rolled {roll}): healed {healed}.";
    });

    private static string Describe(RestReport report)
    {
        var title = report.Kind == RestKind.Long ? "Long rest" : "Short rest";
        if (!report.Completed) return $"{title} not completed: {string.Join(" ", report.NeedsAttention)}";
        var text = $"{title}: {string.Join(" ", report.Changes)}";
        return report.NeedsAttention.Count > 0 ? $"{text} Check: {string.Join(" ", report.NeedsAttention)}" : text;
    }
}

public sealed record QuickAddResult(string Name, object? Kind, string Description);
