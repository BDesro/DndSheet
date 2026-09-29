using Hearthsheet.Core.Domain;

namespace Hearthsheet.Core.Rules;

public sealed class RestReport
{
    public RestKind Kind { get; init; }
    public bool Completed { get; set; } = true;
    public List<string> Changes { get; } = [];
    /// <summary>Things the rest could not decide on its own (manual resources, table rulings).</summary>
    public List<string> NeedsAttention { get; } = [];
}

/// <summary>
/// One piece of rest behavior. Rest rules vary by edition, table and class, so each rule is a
/// separate step and the set of steps is chosen at composition time rather than hard-coded.
/// </summary>
public interface IRestStep
{
    void Apply(Character c, RestKind kind, RestReport report);
}

/// <summary>
/// Runs the configured rest steps. Hit-die spending during a short rest is interactive and happens
/// before this via <see cref="Character.SpendHitDie"/>.
/// </summary>
public sealed class RestService(IReadOnlyList<IRestStep> steps)
{
    public static RestService CreateDefault() => new(
    [
        new LongRestHitPoints(),
        new LongRestHitDice(),
        new SpellSlotRecovery(),
        new ResourceRecovery(),
        new ExhaustionRecovery(),
        new EffectExpiration(),
    ]);

    public RestReport Rest(Character c, RestKind kind)
    {
        var report = new RestReport { Kind = kind };
        if (CharacterRules.IsDead(c))
        {
            report.Completed = false;
            report.NeedsAttention.Add("The character is dead and cannot benefit from resting.");
            return report;
        }
        if (kind == RestKind.Long && c.Combat.CurrentHp == 0)
        {
            // 5e: a creature needs at least 1 HP at the start of a long rest to gain its benefits.
            report.Completed = false;
            report.NeedsAttention.Add("A long rest requires at least 1 hit point to gain its benefits.");
            return report;
        }
        foreach (var step in steps) step.Apply(c, kind, report);
        if (report.Changes.Count == 0) report.Changes.Add("Nothing needed recovering.");
        return report;
    }
}

public sealed class LongRestHitPoints : IRestStep
{
    public void Apply(Character c, RestKind kind, RestReport report)
    {
        if (kind != RestKind.Long) return;
        var max = CharacterRules.MaxHitPoints(c);
        if (c.Combat.CurrentHp < max)
        {
            report.Changes.Add($"HP restored to {max} (+{max - c.Combat.CurrentHp}).");
            c.SetHitPoints(max);
        }
        if (c.Combat.TemporaryHp > 0)
        {
            report.Changes.Add($"Temporary HP ({c.Combat.TemporaryHp}) ended.");
            c.Combat.TemporaryHp = 0;
        }
        c.ResetDeathSaves();
    }
}

/// <summary>Long rest regains half the character's total hit dice (minimum one).</summary>
public sealed class LongRestHitDice : IRestStep
{
    public void Apply(Character c, RestKind kind, RestReport report)
    {
        if (kind != RestKind.Long) return;
        var total = CharacterRules.HitDiceTotal(c);
        var before = c.Combat.HitDiceRemaining;
        c.Combat.HitDiceRemaining = Math.Min(total, before + Math.Max(1, total / 2));
        if (c.Combat.HitDiceRemaining > before)
            report.Changes.Add($"Regained {c.Combat.HitDiceRemaining - before} hit dice ({c.Combat.HitDiceRemaining}/{total}).");
    }
}

public sealed class SpellSlotRecovery : IRestStep
{
    public void Apply(Character c, RestKind kind, RestReport report)
    {
        if (kind != RestKind.Long) return;
        var restored = 0;
        foreach (var slot in c.Spellcasting.Slots)
        {
            restored += slot.Expended;
            slot.Expended = 0;
        }
        if (restored > 0) report.Changes.Add($"Restored {restored} spell slot(s).");
    }
}

public sealed class ResourceRecovery : IRestStep
{
    public void Apply(Character c, RestKind kind, RestReport report)
    {
        foreach (var r in c.Resources)
        {
            var recovers = r.Recovery == RecoveryTiming.ShortRest ||
                           (r.Recovery == RecoveryTiming.LongRest && kind == RestKind.Long);
            if (r.Recovery == RecoveryTiming.Manual)
            {
                if (r.Current < r.Maximum) report.NeedsAttention.Add($"{r.Name} ({r.Current}/{r.Maximum}) recovers manually.");
                continue;
            }
            if (!recovers || r.Current >= r.Maximum) continue;
            var before = r.Current;
            r.Current = r.RecoveryAmount is { } amount ? Math.Min(r.Maximum, r.Current + amount) : r.Maximum;
            report.Changes.Add($"{r.Name}: {before} → {r.Current}/{r.Maximum}.");
        }
    }
}

/// <summary>Both 2014 and 2024 rules remove one exhaustion level per long rest (with food and drink).</summary>
public sealed class ExhaustionRecovery : IRestStep
{
    public void Apply(Character c, RestKind kind, RestReport report)
    {
        if (kind != RestKind.Long || c.ExhaustionLevel == 0) return;
        c.ExhaustionLevel--;
        report.Changes.Add($"Exhaustion reduced to level {c.ExhaustionLevel}.");
    }
}

public sealed class EffectExpiration : IRestStep
{
    public void Apply(Character c, RestKind kind, RestReport report)
    {
        var expired = c.Effects
            .Where(e => e.Expiry == EffectExpiry.ShortRest || (e.Expiry == EffectExpiry.LongRest && kind == RestKind.Long))
            .ToList();
        foreach (var e in expired)
        {
            c.Effects.Remove(e);
            report.Changes.Add($"Effect ended: {e.Name}.");
        }
        if (kind == RestKind.Long && c.Spellcasting.Concentration.Length > 0)
        {
            report.Changes.Add($"Concentration on {c.Spellcasting.Concentration} ended.");
            c.Spellcasting.Concentration = "";
        }
    }
}
