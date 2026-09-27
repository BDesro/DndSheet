using System.Collections.ObjectModel;

namespace DndSheet.Core.Domain;

// Stored (authoritative) character state. Everything derivable — modifiers, skill totals, AC with
// effects, spell DC — lives in CharacterRules and is never persisted.

public sealed class Identity : Observable
{
    public string Name { get; set => Set(ref field, value ?? ""); } = "";
    public string PlayerName { get; set => Set(ref field, value ?? ""); } = "";
    public string ClassName { get; set => Set(ref field, value ?? ""); } = "";
    public string Subclass { get; set => Set(ref field, value ?? ""); } = "";
    public int Level { get; set => SetClamped(ref field, value, 1, 20); } = 1;
    public string Background { get; set => Set(ref field, value ?? ""); } = "";
    public string Species { get; set => Set(ref field, value ?? ""); } = "";
    public string Alignment { get; set => Set(ref field, value ?? ""); } = "";
    public int Experience { get; set => SetClamped(ref field, value, 0, 10_000_000); }
    public bool Inspiration { get; set => Set(ref field, value); }
}

public sealed class Biography : Observable
{
    public string Age { get; set => Set(ref field, value ?? ""); } = "";
    public string Height { get; set => Set(ref field, value ?? ""); } = "";
    public string Weight { get; set => Set(ref field, value ?? ""); } = "";
    public string Eyes { get; set => Set(ref field, value ?? ""); } = "";
    public string Skin { get; set => Set(ref field, value ?? ""); } = "";
    public string Hair { get; set => Set(ref field, value ?? ""); } = "";
    public string PersonalityTraits { get; set => Set(ref field, value ?? ""); } = "";
    public string Ideals { get; set => Set(ref field, value ?? ""); } = "";
    public string Bonds { get; set => Set(ref field, value ?? ""); } = "";
    public string Flaws { get; set => Set(ref field, value ?? ""); } = "";
    public string Appearance { get; set => Set(ref field, value ?? ""); } = "";
    public string Backstory { get; set => Set(ref field, value ?? ""); } = "";
    public string Allies { get; set => Set(ref field, value ?? ""); } = "";
    public string Treasure { get; set => Set(ref field, value ?? ""); } = "";
    public string Notes { get; set => Set(ref field, value ?? ""); } = "";
}

public sealed class AbilityScores : Observable
{
    public int Strength { get; set => SetClamped(ref field, value, 1, 30); } = 10;
    public int Dexterity { get; set => SetClamped(ref field, value, 1, 30); } = 10;
    public int Constitution { get; set => SetClamped(ref field, value, 1, 30); } = 10;
    public int Intelligence { get; set => SetClamped(ref field, value, 1, 30); } = 10;
    public int Wisdom { get; set => SetClamped(ref field, value, 1, 30); } = 10;
    public int Charisma { get; set => SetClamped(ref field, value, 1, 30); } = 10;

    public int Get(Ability a) => a switch
    {
        Ability.Strength => Strength,
        Ability.Dexterity => Dexterity,
        Ability.Constitution => Constitution,
        Ability.Intelligence => Intelligence,
        Ability.Wisdom => Wisdom,
        _ => Charisma,
    };

    public void Set(Ability a, int value)
    {
        switch (a)
        {
            case Ability.Strength: Strength = value; break;
            case Ability.Dexterity: Dexterity = value; break;
            case Ability.Constitution: Constitution = value; break;
            case Ability.Intelligence: Intelligence = value; break;
            case Ability.Wisdom: Wisdom = value; break;
            default: Charisma = value; break;
        }
    }
}

public sealed class SavingThrow : Observable
{
    public Ability Ability { get; set => Set(ref field, value); }
    public bool Proficient { get; set => Set(ref field, value); }
    public int Bonus { get; set => SetClamped(ref field, value, -50, 50); }
}

public sealed class SkillEntry : Observable
{
    public Skill Skill { get; set => Set(ref field, value); }
    public ProficiencyLevel Proficiency { get; set => Set(ref field, value); }
    public int Bonus { get; set => SetClamped(ref field, value, -50, 50); }
}

public sealed class Combat : Observable
{
    /// <summary>Base AC as entered by the user (armor + dex etc.). Effects/items add on top.</summary>
    public int ArmorClass { get; set => SetClamped(ref field, value, 0, 50); } = 10;
    public int InitiativeBonus { get; set => SetClamped(ref field, value, -20, 20); }
    public int Speed { get; set => SetClamped(ref field, value, 0, 500); } = 30;
    public int MaxHp { get; set => SetClamped(ref field, value, 1, 9999); } = 10;
    public int CurrentHp { get; set => SetClamped(ref field, value, 0, 9999); } = 10;
    public int TemporaryHp { get; set => SetClamped(ref field, value, 0, 9999); }
    public int HitDieSize { get; set => SetClamped(ref field, value, 4, 20); } = 8;
    public int HitDiceRemaining { get; set => SetClamped(ref field, value, 0, 20); } = 1;
    public int DeathSaveSuccesses { get; set => SetClamped(ref field, value, 0, 3); }
    public int DeathSaveFailures { get; set => SetClamped(ref field, value, 0, 3); }
    public bool IsStable { get; set => Set(ref field, value); }
}

public sealed class Modifier : Observable
{
    /// <summary>A key from <see cref="Rules.StatTargets"/>, e.g. "ac" or "skill.stealth".</summary>
    public string Target { get; set => Set(ref field, (value ?? "").Trim().ToLowerInvariant()); } = "";
    public ModifierKind Kind { get; set => Set(ref field, value); }
    public int Value { get; set => SetClamped(ref field, value, -100, 100); }
}

public sealed class Attack : Observable
{
    public string Name { get; set => Set(ref field, value ?? ""); } = "";
    /// <summary>Null means no ability modifier is added (e.g. a flat-bonus attack).</summary>
    public Ability? Ability { get; set => Set(ref field, value); } = Domain.Ability.Strength;
    public bool Proficient { get; set => Set(ref field, value); } = true;
    public int Bonus { get; set => SetClamped(ref field, value, -50, 50); }
    public string Damage { get; set => Set(ref field, value ?? ""); } = "";
    public string Notes { get; set => Set(ref field, value ?? ""); } = "";
}

public sealed class Proficiency : Observable
{
    public ProficiencyCategory Category { get; set => Set(ref field, value); }
    public string Name { get; set => Set(ref field, value ?? ""); } = "";
}

public sealed class Feature : Observable
{
    public string Name { get; set => Set(ref field, value ?? ""); } = "";
    public FeatureSource Source { get; set => Set(ref field, value); } = FeatureSource.Custom;
    public string Description { get; set => Set(ref field, value ?? ""); } = "";
}

public sealed class Item : Observable
{
    public string Name { get; set => Set(ref field, value ?? ""); } = "";
    public int Quantity { get; set => SetClamped(ref field, value, 0, 1_000_000); } = 1;
    public double Weight { get; set => Set(ref field, Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100_000)); }
    public bool Equipped { get; set => Set(ref field, value); }
    public bool RequiresAttunement { get; set => Set(ref field, value); }
    public bool Attuned { get; set => Set(ref field, value); }
    public string Description { get; set => Set(ref field, value ?? ""); } = "";
    /// <summary>Applied while the item is equipped (and attuned, if it requires attunement).</summary>
    public ObservableCollection<Modifier> Modifiers { get; init; } = [];
}

public sealed class Currency : Observable
{
    public int Copper { get; set => SetClamped(ref field, value, 0, int.MaxValue); }
    public int Silver { get; set => SetClamped(ref field, value, 0, int.MaxValue); }
    public int Electrum { get; set => SetClamped(ref field, value, 0, int.MaxValue); }
    public int Gold { get; set => SetClamped(ref field, value, 0, int.MaxValue); }
    public int Platinum { get; set => SetClamped(ref field, value, 0, int.MaxValue); }
}

/// <summary>Any limited-use pool: Rage, Ki, Channel Divinity, Pact slots, a custom counter…</summary>
public sealed class Resource : Observable
{
    public string Name { get; set => Set(ref field, value ?? ""); } = "";
    public int Maximum { get; set => SetClamped(ref field, value, 0, 999); } = 1;
    public int Current { get; set => SetClamped(ref field, value, 0, 999); } = 1;
    public RecoveryTiming Recovery { get; set => Set(ref field, value); } = RecoveryTiming.LongRest;
    /// <summary>Amount restored by the recovering rest; null restores to maximum.</summary>
    public int? RecoveryAmount { get; set => Set(ref field, value is null ? null : Math.Clamp(value.Value, 0, 999)); }
    public string Notes { get; set => Set(ref field, value ?? ""); } = "";
}

/// <summary>A temporary or ongoing effect (Bless, a potion, a curse). Conditions are tracked separately.</summary>
public sealed class Effect : Observable
{
    public string Name { get; set => Set(ref field, value ?? ""); } = "";
    public string Description { get; set => Set(ref field, value ?? ""); } = "";
    public string Duration { get; set => Set(ref field, value ?? ""); } = "";
    public EffectExpiry Expiry { get; set => Set(ref field, value); }
    public ObservableCollection<Modifier> Modifiers { get; init; } = [];
}

public sealed class Spell : Observable
{
    public string Name { get; set => Set(ref field, value ?? ""); } = "";
    public int Level { get; set => SetClamped(ref field, value, 0, 9); }
    public string School { get; set => Set(ref field, value ?? ""); } = "";
    public string CastingTime { get; set => Set(ref field, value ?? ""); } = "";
    public string Range { get; set => Set(ref field, value ?? ""); } = "";
    public string Components { get; set => Set(ref field, value ?? ""); } = "";
    public string Duration { get; set => Set(ref field, value ?? ""); } = "";
    public bool Concentration { get; set => Set(ref field, value); }
    public bool Ritual { get; set => Set(ref field, value); }
    public bool Prepared { get; set => Set(ref field, value); }
    public string Description { get; set => Set(ref field, value ?? ""); } = "";
    public string HigherLevels { get; set => Set(ref field, value ?? ""); } = "";
}

public sealed class SpellSlotLevel : Observable
{
    public int Level { get; set => SetClamped(ref field, value, 1, 9); } = 1;
    public int Maximum { get; set { if (value != field) { SetClamped(ref field, value, 0, 20); Raise(nameof(Available)); } } }
    public int Expended { get; set { if (value != field) { SetClamped(ref field, value, 0, 20); Raise(nameof(Available)); } } }
    public int Available => Math.Max(0, Maximum - Expended);
}

public sealed class Spellcasting : Observable
{
    public Ability? Ability { get; set => Set(ref field, value); }
    public string SpellcastingClass { get; set => Set(ref field, value ?? ""); } = "";
    /// <summary>Name of the spell currently being concentrated on; empty when none.</summary>
    public string Concentration { get; set => Set(ref field, value ?? ""); } = "";
    public ObservableCollection<SpellSlotLevel> Slots { get; init; } =
        [.. Enumerable.Range(1, 9).Select(l => new SpellSlotLevel { Level = l })];
    public ObservableCollection<Spell> Spells { get; init; } = [];

    public SpellSlotLevel SlotLevel(int level) => Slots.First(s => s.Level == level);
}
