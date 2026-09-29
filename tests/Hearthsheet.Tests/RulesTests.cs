using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Rules;

namespace Hearthsheet.Tests;

public class RulesTests
{
    [Theory]
    [InlineData(1, -5)]
    [InlineData(8, -1)]
    [InlineData(9, -1)]
    [InlineData(10, 0)]
    [InlineData(11, 0)]
    [InlineData(12, 1)]
    [InlineData(15, 2)]
    [InlineData(20, 5)]
    [InlineData(30, 10)]
    public void AbilityModifier_FollowsFloorOfHalfDifference(int score, int expected) =>
        Assert.Equal(expected, CharacterRules.AbilityModifier(score));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(8, 3)]
    [InlineData(9, 4)]
    [InlineData(13, 5)]
    [InlineData(17, 6)]
    [InlineData(20, 6)]
    public void ProficiencyBonus_ByLevel(int level, int expected) =>
        Assert.Equal(expected, CharacterRules.ProficiencyBonus(level));

    [Fact]
    public void Skill_CombinesAbilityProficiencyExpertiseAndBonus()
    {
        var c = new Character();
        c.Identity.Level = 5; // PB +3
        c.Abilities.Dexterity = 16; // +3

        Assert.Equal(3, CharacterRules.SkillModifier(c, Skill.Stealth));

        c.SkillEntry(Skill.Stealth).Proficiency = ProficiencyLevel.Proficient;
        Assert.Equal(6, CharacterRules.SkillModifier(c, Skill.Stealth));

        c.SkillEntry(Skill.Stealth).Proficiency = ProficiencyLevel.Expertise;
        Assert.Equal(9, CharacterRules.SkillModifier(c, Skill.Stealth));

        c.SkillEntry(Skill.Stealth).Bonus = 1;
        Assert.Equal(10, CharacterRules.SkillModifier(c, Skill.Stealth));
    }

    [Fact]
    public void Skill_HalfProficiencyRoundsDown()
    {
        var c = new Character();
        c.Identity.Level = 5; // PB 3 → half = 1
        c.SkillEntry(Skill.History).Proficiency = ProficiencyLevel.Half;
        Assert.Equal(1, CharacterRules.SkillModifier(c, Skill.History));
    }

    [Fact]
    public void SavingThrow_UsesProficiencyAndBonuses()
    {
        var c = new Character();
        c.Abilities.Wisdom = 14;
        c.Save(Ability.Wisdom).Proficient = true;
        Assert.Equal(4, CharacterRules.SavingThrow(c, Ability.Wisdom));
        Assert.Equal(0, CharacterRules.SavingThrow(c, Ability.Strength));

        // Cloak of Protection style: +1 to all saves while equipped.
        var cloak = new Item { Name = "Cloak", Equipped = true, RequiresAttunement = true, Attuned = true };
        cloak.Modifiers.Add(new Modifier { Target = StatTargets.AllSaves, Value = 1 });
        c.Items.Add(cloak);
        Assert.Equal(5, CharacterRules.SavingThrow(c, Ability.Wisdom));
        Assert.Equal(1, CharacterRules.SavingThrow(c, Ability.Strength));
    }

    [Fact]
    public void ItemModifiers_ApplyOnlyWhenEquippedAndAttunedIfRequired()
    {
        var c = new Character();
        c.Combat.ArmorClass = 15;
        var ring = new Item { Name = "Ring of Protection", RequiresAttunement = true };
        ring.Modifiers.Add(new Modifier { Target = "AC", Value = 1 }); // targets are normalized to lower case
        c.Items.Add(ring);

        Assert.Equal(15, CharacterRules.ArmorClass(c));
        ring.Equipped = true;
        Assert.Equal(15, CharacterRules.ArmorClass(c));
        ring.Attuned = true;
        Assert.Equal(16, CharacterRules.ArmorClass(c));
    }

    [Fact]
    public void Effects_SetOverridesBaseThenBonusesAdd()
    {
        var c = new Character();
        c.Abilities.Strength = 8;
        var gauntlets = new Effect { Name = "Giant Strength" };
        gauntlets.Modifiers.Add(new Modifier { Target = StatTargets.AbilityScore(Ability.Strength), Kind = ModifierKind.Set, Value = 19 });
        c.Effects.Add(gauntlets);
        Assert.Equal(19, CharacterRules.AbilityScore(c, Ability.Strength));
        Assert.Equal(4, CharacterRules.AbilityModifier(c, Ability.Strength));

        var bless = new Effect { Name = "Enhance" };
        bless.Modifiers.Add(new Modifier { Target = StatTargets.AbilityScore(Ability.Strength), Value = 2 });
        c.Effects.Add(bless);
        Assert.Equal(21, CharacterRules.AbilityScore(c, Ability.Strength));
    }

    [Fact]
    public void Derived_CombatAndSpellValues()
    {
        var c = new Character();
        c.Identity.Level = 9; // PB 4
        c.Abilities.Dexterity = 14;
        c.Abilities.Wisdom = 12;
        c.Abilities.Intelligence = 18;
        c.Combat.InitiativeBonus = 1;
        c.SkillEntry(Skill.Perception).Proficiency = ProficiencyLevel.Proficient;
        c.Spellcasting.Ability = Ability.Intelligence;

        Assert.Equal(3, CharacterRules.Initiative(c));
        Assert.Equal(15, CharacterRules.PassivePerception(c));
        Assert.Equal(16, CharacterRules.SpellSaveDc(c));
        Assert.Equal(8, CharacterRules.SpellAttackBonus(c));

        c.Spellcasting.Ability = null;
        Assert.Null(CharacterRules.SpellSaveDc(c));
    }

    [Fact]
    public void AttackBonus_UsesAbilityProficiencyAndBonus()
    {
        var c = new Character();
        c.Abilities.Strength = 16;
        var sword = new Attack { Name = "Longsword", Ability = Ability.Strength, Proficient = true, Bonus = 1 };
        Assert.Equal(6, CharacterRules.AttackBonus(c, sword));
        sword.Ability = null;
        Assert.Equal(3, CharacterRules.AttackBonus(c, sword));
    }

    [Fact]
    public void Setters_ClampOutOfRangeValues()
    {
        var c = new Character();
        c.Abilities.Strength = 99;
        c.Identity.Level = 0;
        c.Combat.CurrentHp = -5;
        Assert.Equal(30, c.Abilities.Strength);
        Assert.Equal(1, c.Identity.Level);
        Assert.Equal(0, c.Combat.CurrentHp);
    }
}
