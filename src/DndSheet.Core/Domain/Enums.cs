namespace DndSheet.Core.Domain;

public enum Ability { Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma }

public enum Skill
{
    Acrobatics, AnimalHandling, Arcana, Athletics, Deception, History, Insight, Intimidation,
    Investigation, Medicine, Nature, Perception, Performance, Persuasion, Religion,
    SleightOfHand, Stealth, Survival,
}

/// <summary>Half covers Jack of All Trades-style features.</summary>
public enum ProficiencyLevel { None, Half, Proficient, Expertise }

public enum ProficiencyCategory { Armor, Weapon, Tool, Language, Other }

public enum FeatureSource { Class, Subclass, Species, Background, Feat, Trait, Custom }

/// <summary>Which rest refills a resource. Manual resources are flagged for the user after a rest.</summary>
public enum RecoveryTiming { Manual, ShortRest, LongRest }

/// <summary>When an active effect is removed automatically. ShortRest means "at the end of any rest".</summary>
public enum EffectExpiry { Manual, ShortRest, LongRest }

public enum RestKind { Short, Long }

public enum DeathSaveResult { Success, Failure, CriticalSuccess, CriticalFailure }

/// <summary>Bonus adds to the value; Set replaces the base value (highest Set wins).</summary>
public enum ModifierKind { Bonus, Set }

public enum Condition
{
    Blinded, Charmed, Deafened, Frightened, Grappled, Incapacitated, Invisible, Paralyzed,
    Petrified, Poisoned, Prone, Restrained, Stunned, Unconscious,
}

public static class AbilityInfo
{
    public static readonly Ability[] All = Enum.GetValues<Ability>();

    public static string Abbreviation(this Ability a) => a switch
    {
        Ability.Strength => "STR",
        Ability.Dexterity => "DEX",
        Ability.Constitution => "CON",
        Ability.Intelligence => "INT",
        Ability.Wisdom => "WIS",
        _ => "CHA",
    };
}

public static class SkillInfo
{
    public static readonly Skill[] All = Enum.GetValues<Skill>();

    public static Ability GoverningAbility(this Skill s) => s switch
    {
        Skill.Athletics => Ability.Strength,
        Skill.Acrobatics or Skill.SleightOfHand or Skill.Stealth => Ability.Dexterity,
        Skill.Arcana or Skill.History or Skill.Investigation or Skill.Nature or Skill.Religion => Ability.Intelligence,
        Skill.AnimalHandling or Skill.Insight or Skill.Medicine or Skill.Perception or Skill.Survival => Ability.Wisdom,
        _ => Ability.Charisma,
    };

    public static string DisplayName(this Skill s) => s switch
    {
        Skill.AnimalHandling => "Animal Handling",
        Skill.SleightOfHand => "Sleight of Hand",
        _ => s.ToString(),
    };
}
