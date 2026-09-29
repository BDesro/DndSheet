using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Rules;

namespace Hearthsheet.Tests;

public class GameplayTests
{
    private static Character Fighter(int maxHp = 20)
    {
        var c = new Character();
        c.Identity.Level = 4;
        c.Combat.MaxHp = maxHp;
        c.Combat.CurrentHp = maxHp;
        c.Combat.HitDieSize = 10;
        c.Combat.HitDiceRemaining = 4;
        return c;
    }

    [Fact]
    public void Damage_ReducesHp_AndHealCapsAtMax()
    {
        var c = Fighter();
        c.TakeDamage(7);
        Assert.Equal(13, c.Combat.CurrentHp);
        c.Heal(100);
        Assert.Equal(20, c.Combat.CurrentHp);
    }

    [Fact]
    public void TemporaryHp_AbsorbsDamageFirst_AndDoesNotStack()
    {
        var c = Fighter();
        c.GrantTemporaryHitPoints(5);
        c.GrantTemporaryHitPoints(3);
        Assert.Equal(5, c.Combat.TemporaryHp);

        c.TakeDamage(8);
        Assert.Equal(0, c.Combat.TemporaryHp);
        Assert.Equal(17, c.Combat.CurrentHp);
    }

    [Fact]
    public void DroppingToZero_MakesUnconsciousAndDying()
    {
        var c = Fighter();
        c.TakeDamage(25);
        Assert.Equal(0, c.Combat.CurrentHp);
        Assert.Contains(Condition.Unconscious, c.Conditions);
        Assert.True(CharacterRules.IsDying(c));
        Assert.False(CharacterRules.IsDead(c));
    }

    [Fact]
    public void MassiveDamage_KillsOutright()
    {
        var c = Fighter();
        c.TakeDamage(40); // 20 overflow >= 20 max
        Assert.True(CharacterRules.IsDead(c));
    }

    [Fact]
    public void DamageAtZero_AddsDeathSaveFailures_CriticalAddsTwo()
    {
        var c = Fighter();
        c.TakeDamage(20);
        c.TakeDamage(1);
        Assert.Equal(1, c.Combat.DeathSaveFailures);
        c.TakeDamage(1, critical: true);
        Assert.True(CharacterRules.IsDead(c));
    }

    [Fact]
    public void DeathSaves_ThreeSuccessesStabilize()
    {
        var c = Fighter();
        c.TakeDamage(20);
        c.RecordDeathSave(DeathSaveResult.Success);
        c.RecordDeathSave(DeathSaveResult.Failure);
        c.RecordDeathSave(DeathSaveResult.Success);
        c.RecordDeathSave(DeathSaveResult.Success);
        Assert.True(c.Combat.IsStable);
        Assert.False(CharacterRules.IsDying(c));
        Assert.Equal("Death saves only apply while dying.", c.RecordDeathSave(DeathSaveResult.Failure));
    }

    [Fact]
    public void DeathSaves_CriticalFailureCountsTwice_AndThreeFailuresKill()
    {
        var c = Fighter();
        c.TakeDamage(20);
        c.RecordDeathSave(DeathSaveResult.CriticalFailure);
        Assert.Equal(2, c.Combat.DeathSaveFailures);
        c.RecordDeathSave(DeathSaveResult.Failure);
        Assert.True(CharacterRules.IsDead(c));
        c.Heal(10);
        Assert.Equal(0, c.Combat.CurrentHp);
    }

    [Fact]
    public void DeathSaves_Natural20RegainsOneHpAndWakes()
    {
        var c = Fighter();
        c.TakeDamage(20);
        c.RecordDeathSave(DeathSaveResult.Failure);
        c.RecordDeathSave(DeathSaveResult.CriticalSuccess);
        Assert.Equal(1, c.Combat.CurrentHp);
        Assert.Equal(0, c.Combat.DeathSaveFailures);
        Assert.DoesNotContain(Condition.Unconscious, c.Conditions);
    }

    [Fact]
    public void HealingFromZero_ResetsDeathSavesAndRemovesUnconscious()
    {
        var c = Fighter();
        c.TakeDamage(20);
        c.RecordDeathSave(DeathSaveResult.Failure);
        c.Heal(5);
        Assert.Equal(5, c.Combat.CurrentHp);
        Assert.Equal(0, c.Combat.DeathSaveFailures);
        Assert.DoesNotContain(Condition.Unconscious, c.Conditions);
    }

    [Fact]
    public void SpendHitDie_HealsRollPlusCon_AndValidatesRoll()
    {
        var c = Fighter();
        c.Abilities.Constitution = 14; // +2
        c.TakeDamage(15);
        var healed = c.SpendHitDie(6);
        Assert.Equal(8, healed);
        Assert.Equal(13, c.Combat.CurrentHp);
        Assert.Equal(3, c.Combat.HitDiceRemaining);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SpendHitDie(11));
    }

    [Fact]
    public void SpendHitDie_NegativeConNeverHealsBelowZero()
    {
        var c = Fighter();
        c.Abilities.Constitution = 3; // -4
        c.TakeDamage(10);
        Assert.Equal(0, c.SpendHitDie(1));
    }

    [Fact]
    public void SpendHitDie_FailsWithNoneRemaining()
    {
        var c = Fighter();
        c.Combat.HitDiceRemaining = 0;
        Assert.Throws<InvalidOperationException>(() => c.SpendHitDie(5));
    }

    [Fact]
    public void Resources_UseAndRestoreWithinBounds()
    {
        var c = Fighter();
        var ki = new Resource { Name = "Ki", Maximum = 4, Current = 4 };
        c.Resources.Add(ki);
        Assert.True(c.UseResource(ki, 3));
        Assert.False(c.UseResource(ki, 2));
        Assert.Equal(1, ki.Current);
        c.RestoreResource(ki, 10);
        Assert.Equal(4, ki.Current);
    }

    [Fact]
    public void CastSpell_UsesLowestAvailableSlot_AndTracksConcentration()
    {
        var c = Fighter();
        c.Spellcasting.SlotLevel(1).Maximum = 1;
        c.Spellcasting.SlotLevel(2).Maximum = 1;
        var bless = new Spell { Name = "Bless", Level = 1, Concentration = true };
        var hold = new Spell { Name = "Hold Person", Level = 2, Concentration = true };

        c.CastSpell(bless);
        Assert.Equal(1, c.Spellcasting.SlotLevel(1).Expended);
        c.CastSpell(bless); // upcasts into level 2
        Assert.Equal(1, c.Spellcasting.SlotLevel(2).Expended);
        Assert.Equal("Bless", c.Spellcasting.Concentration);

        var result = c.CastSpell(hold);
        Assert.StartsWith("No spell slot", result);
        c.Spellcasting.SlotLevel(2).Expended = 0;
        Assert.Contains("Dropped concentration on Bless", c.CastSpell(hold));
    }

    [Fact]
    public void CastSpell_CantripAndRitualAreFree()
    {
        var c = Fighter();
        c.CastSpell(new Spell { Name = "Light", Level = 0 });
        c.CastSpell(new Spell { Name = "Detect Magic", Level = 1, Ritual = true }, asRitual: true);
        Assert.All(c.Spellcasting.Slots, s => Assert.Equal(0, s.Expended));
    }

    [Fact]
    public void Conditions_AreUnique()
    {
        var c = Fighter();
        c.AddCondition(Condition.Prone);
        c.AddCondition(Condition.Prone);
        Assert.Single(c.Conditions);
        c.RemoveCondition(Condition.Prone);
        Assert.Empty(c.Conditions);
    }
}
