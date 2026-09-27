using DndSheet.Core.Domain;
using DndSheet.Core.Rules;

namespace DndSheet.Core.Content;

public enum CasterType { None, Full, Half, Pact }

public sealed record ClassDefinition(
    string Name, int HitDie, Ability[] SavingThrows, Ability? SpellcastingAbility, CasterType Caster);

/// <summary>
/// Mechanical facts from the System Reference Document 5.1 (CC-BY-4.0, see NOTICE.md): class names,
/// hit dice, save proficiencies and spell-slot progressions. No rules text is embedded. Everything here
/// is only used to pre-fill a new character; the user can edit or ignore all of it.
/// </summary>
public static class SrdCatalog
{
    public static IReadOnlyList<ClassDefinition> Classes { get; } =
    [
        new("Barbarian", 12, [Ability.Strength, Ability.Constitution], null, CasterType.None),
        new("Bard", 8, [Ability.Dexterity, Ability.Charisma], Ability.Charisma, CasterType.Full),
        new("Cleric", 8, [Ability.Wisdom, Ability.Charisma], Ability.Wisdom, CasterType.Full),
        new("Druid", 8, [Ability.Intelligence, Ability.Wisdom], Ability.Wisdom, CasterType.Full),
        new("Fighter", 10, [Ability.Strength, Ability.Constitution], null, CasterType.None),
        new("Monk", 8, [Ability.Strength, Ability.Dexterity], null, CasterType.None),
        new("Paladin", 10, [Ability.Wisdom, Ability.Charisma], Ability.Charisma, CasterType.Half),
        new("Ranger", 10, [Ability.Strength, Ability.Dexterity], Ability.Wisdom, CasterType.Half),
        new("Rogue", 8, [Ability.Dexterity, Ability.Intelligence], null, CasterType.None),
        new("Sorcerer", 6, [Ability.Constitution, Ability.Charisma], Ability.Charisma, CasterType.Full),
        new("Warlock", 8, [Ability.Wisdom, Ability.Charisma], Ability.Charisma, CasterType.Pact),
        new("Wizard", 6, [Ability.Intelligence, Ability.Wisdom], Ability.Intelligence, CasterType.Full),
    ];

    public static IReadOnlyList<string> Species { get; } =
        ["Dragonborn", "Dwarf", "Elf", "Gnome", "Half-Elf", "Half-Orc", "Halfling", "Human", "Tiefling"];

    public static IReadOnlyList<string> Alignments { get; } =
    [
        "Lawful Good", "Neutral Good", "Chaotic Good", "Lawful Neutral", "True Neutral",
        "Chaotic Neutral", "Lawful Evil", "Neutral Evil", "Chaotic Evil", "Unaligned",
    ];

    public static IReadOnlyList<int> HitDieSizes { get; } = [6, 8, 10, 12];

    // Full-caster slots per character level (index 0 = level 1), spell levels 1..9.
    private static readonly int[][] FullCasterSlots =
    [
        [2], [3], [4, 2], [4, 3], [4, 3, 2], [4, 3, 3], [4, 3, 3, 1], [4, 3, 3, 2], [4, 3, 3, 3, 1],
        [4, 3, 3, 3, 2], [4, 3, 3, 3, 2, 1], [4, 3, 3, 3, 2, 1], [4, 3, 3, 3, 2, 1, 1], [4, 3, 3, 3, 2, 1, 1],
        [4, 3, 3, 3, 2, 1, 1, 1], [4, 3, 3, 3, 2, 1, 1, 1], [4, 3, 3, 3, 2, 1, 1, 1, 1],
        [4, 3, 3, 3, 3, 1, 1, 1, 1], [4, 3, 3, 3, 3, 2, 1, 1, 1], [4, 3, 3, 3, 3, 2, 2, 1, 1],
    ];

    public static ClassDefinition? FindClass(string name) =>
        Classes.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Spell slots (levels 1..9) for a single-class caster. Half casters follow SRD 5.1 (none at level 1).</summary>
    public static int[] SpellSlots(CasterType caster, int level)
    {
        var effective = caster switch
        {
            CasterType.Full => level,
            CasterType.Half => level < 2 ? 0 : (level + 1) / 2,
            _ => 0,
        };
        var slots = new int[9];
        if (effective > 0) FullCasterSlots[effective - 1].CopyTo(slots, 0);
        return slots;
    }

    public static (int Count, int SlotLevel) PactSlots(int level) => level switch
    {
        1 => (1, 1),
        2 => (2, 1),
        <= 4 => (2, 2),
        <= 6 => (2, 3),
        <= 8 => (2, 4),
        <= 10 => (2, 5),
        <= 16 => (3, 5),
        _ => (4, 5),
    };
}

public sealed record NewCharacterOptions(
    string Name, string ClassName, int Level, string Species, string Background, string PlayerName = "");

public static class CharacterFactory
{
    public static Character Create(NewCharacterOptions options)
    {
        var c = new Character();
        c.Identity.Name = string.IsNullOrWhiteSpace(options.Name) ? "New Character" : options.Name.Trim();
        c.Identity.ClassName = options.ClassName.Trim();
        c.Identity.Level = options.Level;
        c.Identity.Species = options.Species.Trim();
        c.Identity.Background = options.Background.Trim();
        c.Identity.PlayerName = options.PlayerName.Trim();
        if (SrdCatalog.FindClass(options.ClassName) is { } def) ApplyClassDefaults(c, def);
        c.Combat.HitDiceRemaining = c.Identity.Level;
        c.Combat.CurrentHp = CharacterRules.MaxHitPoints(c);
        return c;
    }

    /// <summary>Hit die, save proficiencies, average HP, spellcasting ability and slots for the class.</summary>
    public static void ApplyClassDefaults(Character c, ClassDefinition def)
    {
        var level = c.Identity.Level;
        c.Combat.HitDieSize = def.HitDie;
        foreach (var save in c.SavingThrows) save.Proficient = def.SavingThrows.Contains(save.Ability);

        var con = CharacterRules.AbilityModifier(c.Abilities.Constitution);
        c.Combat.MaxHp = Math.Max(1, def.HitDie + con + (level - 1) * Math.Max(1, def.HitDie / 2 + 1 + con));

        c.Spellcasting.Ability = def.SpellcastingAbility;
        if (def.SpellcastingAbility is not null) c.Spellcasting.SpellcastingClass = def.Name;
        var slots = SrdCatalog.SpellSlots(def.Caster, level);
        foreach (var slot in c.Spellcasting.Slots) slot.Maximum = slots[slot.Level - 1];

        if (def.Caster == CasterType.Pact)
        {
            var (count, slotLevel) = SrdCatalog.PactSlots(level);
            var existing = c.Resources.FirstOrDefault(r => r.Name.StartsWith("Pact Magic slots", StringComparison.Ordinal));
            if (existing is not null) c.Resources.Remove(existing);
            c.Resources.Add(new Resource
            {
                Name = $"Pact Magic slots (level {slotLevel})",
                Maximum = count,
                Current = count,
                Recovery = RecoveryTiming.ShortRest,
            });
        }
    }
}
