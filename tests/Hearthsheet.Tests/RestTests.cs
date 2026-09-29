using Hearthsheet.Core.Content;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Rules;

namespace Hearthsheet.Tests;

public class RestTests
{
    private readonly RestService _rest = RestService.CreateDefault();

    private static Character Wizard()
    {
        var c = CharacterFactory.Create(new NewCharacterOptions("Arannis", "Wizard", 5, "Elf", "Sage"));
        c.Resources.Add(new Resource { Name = "Arcane Recovery", Maximum = 1, Current = 0, Recovery = RecoveryTiming.LongRest });
        c.Resources.Add(new Resource { Name = "Channel", Maximum = 2, Current = 0, Recovery = RecoveryTiming.ShortRest });
        c.Resources.Add(new Resource { Name = "Lucky coin", Maximum = 3, Current = 1, Recovery = RecoveryTiming.Manual });
        c.Resources.Add(new Resource { Name = "Partial", Maximum = 5, Current = 0, Recovery = RecoveryTiming.LongRest, RecoveryAmount = 2 });
        return c;
    }

    [Fact]
    public void ShortRest_RecoversShortRestResourcesOnly()
    {
        var c = Wizard();
        c.TakeDamage(10);
        c.Spellcasting.SlotLevel(1).Expended = 2;
        var hp = c.Combat.CurrentHp;

        var report = _rest.Rest(c, RestKind.Short);

        Assert.True(report.Completed);
        Assert.Equal(2, c.Resources.Single(r => r.Name == "Channel").Current);
        Assert.Equal(0, c.Resources.Single(r => r.Name == "Arcane Recovery").Current);
        Assert.Equal(2, c.Spellcasting.SlotLevel(1).Expended);
        Assert.Equal(hp, c.Combat.CurrentHp);
        Assert.Contains(report.NeedsAttention, n => n.Contains("Lucky coin"));
    }

    [Fact]
    public void LongRest_RestoresHpSlotsResourcesAndHalfHitDice()
    {
        var c = Wizard();
        c.TakeDamage(10);
        c.GrantTemporaryHitPoints(4);
        c.Combat.HitDiceRemaining = 0;
        c.Spellcasting.SlotLevel(1).Expended = 4;
        c.Spellcasting.SlotLevel(3).Expended = 1;
        c.ExhaustionLevel = 2;

        var report = _rest.Rest(c, RestKind.Long);

        Assert.True(report.Completed);
        Assert.Equal(CharacterRules.MaxHitPoints(c), c.Combat.CurrentHp);
        Assert.Equal(0, c.Combat.TemporaryHp);
        Assert.Equal(2, c.Combat.HitDiceRemaining); // level 5 → regain 2
        Assert.All(c.Spellcasting.Slots, s => Assert.Equal(0, s.Expended));
        Assert.Equal(1, c.Resources.Single(r => r.Name == "Arcane Recovery").Current);
        Assert.Equal(2, c.Resources.Single(r => r.Name == "Partial").Current);
        Assert.Equal(1, c.ExhaustionLevel);
    }

    [Fact]
    public void LongRest_RegainsAtLeastOneHitDie()
    {
        var c = new Character();
        c.Combat.HitDiceRemaining = 0;
        _rest.Rest(c, RestKind.Long);
        Assert.Equal(1, c.Combat.HitDiceRemaining);
    }

    [Fact]
    public void LongRest_AtZeroHp_GivesNoBenefit()
    {
        var c = Wizard();
        c.TakeDamage(c.Combat.CurrentHp);
        c.RecordDeathSave(DeathSaveResult.Success);
        c.RecordDeathSave(DeathSaveResult.Success);
        c.RecordDeathSave(DeathSaveResult.Success);

        var report = _rest.Rest(c, RestKind.Long);

        Assert.False(report.Completed);
        Assert.Equal(0, c.Combat.CurrentHp);
    }

    [Fact]
    public void Rest_ExpiresEffectsByTiming()
    {
        var c = Wizard();
        c.Effects.Add(new Effect { Name = "Bless", Expiry = EffectExpiry.ShortRest });
        c.Effects.Add(new Effect { Name = "Mage Armor", Expiry = EffectExpiry.LongRest });
        c.Effects.Add(new Effect { Name = "Curse", Expiry = EffectExpiry.Manual });

        _rest.Rest(c, RestKind.Short);
        Assert.Equal(["Mage Armor", "Curse"], c.Effects.Select(e => e.Name));

        _rest.Rest(c, RestKind.Long);
        Assert.Equal(["Curse"], c.Effects.Select(e => e.Name));
    }

    [Fact]
    public void RestSteps_AreReplaceable()
    {
        var c = Wizard();
        c.TakeDamage(10);
        var hp = c.Combat.CurrentHp;
        var customRest = new RestService([new SpellSlotRecovery()]);
        customRest.Rest(c, RestKind.Long);
        Assert.Equal(hp, c.Combat.CurrentHp);
    }

    [Fact]
    public void Factory_AppliesClassDefaults()
    {
        var c = CharacterFactory.Create(new NewCharacterOptions("P", "Paladin", 5, "Human", "Acolyte"));
        Assert.Equal(10, c.Combat.HitDieSize);
        Assert.True(c.Save(Ability.Wisdom).Proficient);
        Assert.True(c.Save(Ability.Charisma).Proficient);
        Assert.Equal(Ability.Charisma, c.Spellcasting.Ability);
        Assert.Equal(4, c.Spellcasting.SlotLevel(1).Maximum);
        Assert.Equal(2, c.Spellcasting.SlotLevel(2).Maximum);
        Assert.Equal(5, c.Combat.HitDiceRemaining);
        Assert.Equal(c.Combat.MaxHp, c.Combat.CurrentHp);

        var warlock = CharacterFactory.Create(new NewCharacterOptions("W", "warlock", 5, "", ""));
        var pact = Assert.Single(warlock.Resources);
        Assert.Equal(RecoveryTiming.ShortRest, pact.Recovery);
        Assert.Equal(2, pact.Maximum);
    }
}
