using System.Collections.ObjectModel;

namespace Hearthsheet.Core.Domain;

/// <summary>
/// Aggregate root and single source of truth for one character. Stored state lives here;
/// gameplay operations are in Character.Play.cs; derived numbers come from CharacterRules.
/// </summary>
public sealed partial class Character : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Identity Identity { get; init; } = new();
    public Biography Biography { get; init; } = new();
    public AbilityScores Abilities { get; init; } = new();
    public Combat Combat { get; init; } = new();
    public Currency Currency { get; init; } = new();
    public Spellcasting Spellcasting { get; init; } = new();

    public ObservableCollection<SavingThrow> SavingThrows { get; init; } =
        [.. AbilityInfo.All.Select(a => new SavingThrow { Ability = a })];
    public ObservableCollection<SkillEntry> Skills { get; init; } =
        [.. SkillInfo.All.Select(s => new SkillEntry { Skill = s })];

    public ObservableCollection<Attack> Attacks { get; init; } = [];
    public ObservableCollection<Proficiency> Proficiencies { get; init; } = [];
    public ObservableCollection<Feature> Features { get; init; } = [];
    public ObservableCollection<Item> Items { get; init; } = [];
    public ObservableCollection<Resource> Resources { get; init; } = [];
    public ObservableCollection<Effect> Effects { get; init; } = [];
    public ObservableCollection<Condition> Conditions { get; init; } = [];

    /// <summary>0–6. Rule effects of each level differ between editions, so only the level is tracked.</summary>
    public int ExhaustionLevel { get; set => SetClamped(ref field, value, 0, 6); }

    public SavingThrow Save(Ability a) => SavingThrows.First(s => s.Ability == a);
    public SkillEntry SkillEntry(Skill s) => Skills.First(e => e.Skill == s);

    /// <summary>
    /// Repairs structural invariants after deserialization or import: one entry per ability/skill,
    /// all nine slot levels present. Values are already range-clamped by the property setters.
    /// </summary>
    public void Normalize()
    {
        foreach (var a in AbilityInfo.All)
        {
            var matches = SavingThrows.Where(s => s.Ability == a).ToList();
            if (matches.Count == 0) SavingThrows.Add(new SavingThrow { Ability = a });
            foreach (var extra in matches.Skip(1)) SavingThrows.Remove(extra);
        }
        foreach (var s in SkillInfo.All)
        {
            var matches = Skills.Where(e => e.Skill == s).ToList();
            if (matches.Count == 0) Skills.Add(new SkillEntry { Skill = s });
            foreach (var extra in matches.Skip(1)) Skills.Remove(extra);
        }
        for (var level = 1; level <= 9; level++)
        {
            var matches = Spellcasting.Slots.Where(s => s.Level == level).ToList();
            if (matches.Count == 0) Spellcasting.Slots.Add(new SpellSlotLevel { Level = level });
            foreach (var extra in matches.Skip(1)) Spellcasting.Slots.Remove(extra);
        }
        SortInPlace(SavingThrows, s => (int)s.Ability);
        SortInPlace(Skills, s => (int)s.Skill);
        SortInPlace(Spellcasting.Slots, s => s.Level);

        var distinctConditions = Conditions.Distinct().ToList();
        if (distinctConditions.Count != Conditions.Count)
        {
            Conditions.Clear();
            foreach (var c in distinctConditions) Conditions.Add(c);
        }
        if (Combat.HitDiceRemaining > Identity.Level) Combat.HitDiceRemaining = Identity.Level;
    }

    private static void SortInPlace<T>(ObservableCollection<T> items, Func<T, int> key)
    {
        var sorted = items.OrderBy(key).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = items.IndexOf(sorted[i]);
            if (current != i) items.Move(current, i);
        }
    }
}
