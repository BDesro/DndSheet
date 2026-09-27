using DndSheet.Core.Domain;

namespace DndSheet.Core.Rules;

/// <summary>Keys that modifiers can target. Free-form strings keep new targets cheap to add.</summary>
public static class StatTargets
{
    public const string ArmorClass = "ac";
    public const string Initiative = "initiative";
    public const string Speed = "speed";
    public const string MaxHp = "hp.max";
    public const string AllSaves = "save.all";
    public const string AllSkills = "skill.all";
    public const string Attack = "attack";
    public const string SpellDc = "spell.dc";
    public const string SpellAttack = "spell.attack";
    public const string PassivePerception = "passive.perception";

    public static string AbilityScore(Ability a) => "ability." + a.Abbreviation().ToLowerInvariant();
    public static string Save(Ability a) => "save." + a.Abbreviation().ToLowerInvariant();
    public static string SkillCheck(Skill s) => "skill." + s.ToString().ToLowerInvariant();

    public static IReadOnlyList<string> All { get; } =
    [
        ArmorClass, Initiative, Speed, MaxHp, Attack, SpellDc, SpellAttack, PassivePerception, AllSaves, AllSkills,
        .. AbilityInfo.All.Select(AbilityScore),
        .. AbilityInfo.All.Select(Save),
        .. SkillInfo.All.Select(SkillCheck),
    ];
}

/// <summary>
/// Derived values. Pure functions over stored state so nothing derived is ever persisted or
/// allowed to drift out of sync between views.
/// </summary>
public static class CharacterRules
{
    public static int AbilityModifier(int score) => (int)Math.Floor((score - 10) / 2.0);

    public static int ProficiencyBonus(int level) => 2 + (Math.Clamp(level, 1, 20) - 1) / 4;

    public static int ProficiencyBonus(Character c) => ProficiencyBonus(c.Identity.Level);

    /// <summary>Modifiers from active effects plus equipped (and, where required, attuned) items.</summary>
    public static IEnumerable<Modifier> ActiveModifiers(Character c) =>
        c.Effects.SelectMany(e => e.Modifiers)
            .Concat(c.Items.Where(IsItemActive).SelectMany(i => i.Modifiers));

    public static bool IsItemActive(Item i) => i.Equipped && (!i.RequiresAttunement || i.Attuned);

    /// <summary>Highest Set modifier replaces the base (if any), then all Bonus modifiers add.</summary>
    public static int Resolve(Character c, int baseValue, params string[] targets)
    {
        var relevant = ActiveModifiers(c).Where(m => targets.Contains(m.Target)).ToList();
        var sets = relevant.Where(m => m.Kind == ModifierKind.Set).ToList();
        var value = sets.Count > 0 ? sets.Max(m => m.Value) : baseValue;
        return value + relevant.Where(m => m.Kind == ModifierKind.Bonus).Sum(m => m.Value);
    }

    public static int AbilityScore(Character c, Ability a) =>
        Math.Clamp(Resolve(c, c.Abilities.Get(a), StatTargets.AbilityScore(a)), 1, 30);

    public static int AbilityModifier(Character c, Ability a) => AbilityModifier(AbilityScore(c, a));

    public static int ProficiencyContribution(ProficiencyLevel level, int proficiencyBonus) => level switch
    {
        ProficiencyLevel.Half => proficiencyBonus / 2,
        ProficiencyLevel.Proficient => proficiencyBonus,
        ProficiencyLevel.Expertise => proficiencyBonus * 2,
        _ => 0,
    };

    public static int SavingThrow(Character c, Ability a)
    {
        var entry = c.Save(a);
        var baseValue = AbilityModifier(c, a) + (entry.Proficient ? ProficiencyBonus(c) : 0) + entry.Bonus;
        return Resolve(c, baseValue, StatTargets.AllSaves, StatTargets.Save(a));
    }

    public static int SkillModifier(Character c, Skill s)
    {
        var entry = c.SkillEntry(s);
        var baseValue = AbilityModifier(c, s.GoverningAbility())
                        + ProficiencyContribution(entry.Proficiency, ProficiencyBonus(c))
                        + entry.Bonus;
        return Resolve(c, baseValue, StatTargets.AllSkills, StatTargets.SkillCheck(s));
    }

    public static int PassivePerception(Character c) =>
        Resolve(c, 10 + SkillModifier(c, Skill.Perception), StatTargets.PassivePerception);

    public static int ArmorClass(Character c) => Resolve(c, c.Combat.ArmorClass, StatTargets.ArmorClass);

    public static int Initiative(Character c) =>
        Resolve(c, AbilityModifier(c, Ability.Dexterity) + c.Combat.InitiativeBonus, StatTargets.Initiative);

    public static int Speed(Character c) => Math.Max(0, Resolve(c, c.Combat.Speed, StatTargets.Speed));

    public static int MaxHitPoints(Character c) => Math.Max(1, Resolve(c, c.Combat.MaxHp, StatTargets.MaxHp));

    public static int HitDiceTotal(Character c) => c.Identity.Level;

    public static int? SpellSaveDc(Character c) => c.Spellcasting.Ability is { } a
        ? Resolve(c, 8 + ProficiencyBonus(c) + AbilityModifier(c, a), StatTargets.SpellDc)
        : null;

    public static int? SpellAttackBonus(Character c) => c.Spellcasting.Ability is { } a
        ? Resolve(c, ProficiencyBonus(c) + AbilityModifier(c, a), StatTargets.SpellAttack)
        : null;

    public static int AttackBonus(Character c, Attack attack)
    {
        var baseValue = (attack.Ability is { } a ? AbilityModifier(c, a) : 0)
                        + (attack.Proficient ? ProficiencyBonus(c) : 0)
                        + attack.Bonus;
        return Resolve(c, baseValue, StatTargets.Attack);
    }

    public static double CarriedWeight(Character c) => c.Items.Sum(i => i.Weight * i.Quantity);

    /// <summary>Standard carrying capacity (STR × 15 lb).</summary>
    public static int CarryingCapacity(Character c) => AbilityScore(c, Ability.Strength) * 15;

    public static bool IsDead(Character c) => c.Combat.DeathSaveFailures >= 3;

    public static bool IsDying(Character c) => c.Combat.CurrentHp == 0 && !IsDead(c) && !c.Combat.IsStable;

    public static string FormatModifier(int value) => value >= 0 ? $"+{value}" : $"−{-value}";
}
