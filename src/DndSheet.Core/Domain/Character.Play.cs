using DndSheet.Core.Rules;

namespace DndSheet.Core.Domain;

/// <summary>Gameplay operations. Every UI action routes through these so the rules live in one place.</summary>
public sealed partial class Character
{
    /// <summary>
    /// Applies damage: temporary HP absorb first, then current HP. Massive damage (overflow at or above
    /// max HP) kills outright; damage while at 0 HP causes death-save failures.
    /// </summary>
    public string TakeDamage(int amount, bool critical = false)
    {
        if (amount <= 0) return "No damage.";
        if (CharacterRules.IsDead(this)) return "Character is dead; damage ignored.";

        var max = CharacterRules.MaxHitPoints(this);
        var remaining = amount;
        var absorbed = Math.Min(Combat.TemporaryHp, remaining);
        Combat.TemporaryHp -= absorbed;
        remaining -= absorbed;
        var prefix = absorbed > 0 ? $"Temp HP absorbed {absorbed}. " : "";
        if (remaining == 0) return $"{prefix}Took {amount} damage.";

        if (Combat.CurrentHp == 0)
        {
            Combat.IsStable = false;
            if (remaining >= max)
            {
                Combat.DeathSaveFailures = 3;
                return $"{prefix}Took {remaining} damage at 0 HP: massive damage, character dies.";
            }
            Combat.DeathSaveFailures += critical ? 2 : 1;
            return $"{prefix}Took {remaining} damage at 0 HP: {(critical ? 2 : 1)} death save failure(s).";
        }

        var overflow = remaining - Combat.CurrentHp;
        Combat.CurrentHp = Math.Max(0, Combat.CurrentHp - remaining);
        if (Combat.CurrentHp > 0) return $"{prefix}Took {remaining} damage.";

        if (overflow >= max)
        {
            Combat.DeathSaveFailures = 3;
            return $"{prefix}Took {remaining} damage: massive damage, character dies.";
        }
        FallUnconscious();
        return $"{prefix}Took {remaining} damage and dropped to 0 HP.";
    }

    /// <summary>Heals up to max HP. Healing from 0 HP ends dying; the dead cannot be healed.</summary>
    public string Heal(int amount)
    {
        if (amount <= 0) return "No healing.";
        if (CharacterRules.IsDead(this)) return "Character is dead; healing has no effect.";
        var before = Combat.CurrentHp;
        SetHitPoints(before + amount);
        return $"Healed {Combat.CurrentHp - before} HP.";
    }

    /// <summary>Directly sets current HP (clamped to 0..max) while keeping dying/unconscious state consistent.</summary>
    public void SetHitPoints(int value)
    {
        var before = Combat.CurrentHp;
        Combat.CurrentHp = Math.Clamp(value, 0, CharacterRules.MaxHitPoints(this));
        if (before == 0 && Combat.CurrentHp > 0) Revive();
        else if (before > 0 && Combat.CurrentHp == 0) FallUnconscious();
    }

    /// <summary>Temporary HP don't stack: the higher of current and new is kept.</summary>
    public void GrantTemporaryHitPoints(int amount) =>
        Combat.TemporaryHp = Math.Max(Combat.TemporaryHp, amount);

    public string RecordDeathSave(DeathSaveResult result)
    {
        if (!CharacterRules.IsDying(this)) return "Death saves only apply while dying.";
        switch (result)
        {
            case DeathSaveResult.CriticalSuccess:
                SetHitPoints(1);
                return "Natural 20: regained 1 HP.";
            case DeathSaveResult.CriticalFailure:
                Combat.DeathSaveFailures += 2;
                break;
            case DeathSaveResult.Failure:
                Combat.DeathSaveFailures += 1;
                break;
            default:
                Combat.DeathSaveSuccesses += 1;
                break;
        }
        if (CharacterRules.IsDead(this)) return "Third death save failure: character dies.";
        if (Combat.DeathSaveSuccesses >= 3)
        {
            Combat.IsStable = true;
            Combat.DeathSaveSuccesses = 0;
            Combat.DeathSaveFailures = 0;
            return "Third success: character is stable.";
        }
        return $"Death saves: {Combat.DeathSaveSuccesses} success, {Combat.DeathSaveFailures} failure.";
    }

    public void ResetDeathSaves()
    {
        Combat.DeathSaveSuccesses = 0;
        Combat.DeathSaveFailures = 0;
        Combat.IsStable = false;
    }

    /// <summary>Spends one hit die with the given roll (1..die size); heals roll + CON modifier (min 0).</summary>
    public int SpendHitDie(int roll)
    {
        if (Combat.HitDiceRemaining <= 0) throw new InvalidOperationException("No hit dice remaining.");
        if (roll < 1 || roll > Combat.HitDieSize)
            throw new ArgumentOutOfRangeException(nameof(roll), $"Roll must be between 1 and {Combat.HitDieSize}.");
        if (CharacterRules.IsDead(this)) throw new InvalidOperationException("A dead character cannot spend hit dice.");

        Combat.HitDiceRemaining--;
        var healing = Math.Max(0, roll + CharacterRules.AbilityModifier(this, Ability.Constitution));
        var before = Combat.CurrentHp;
        SetHitPoints(before + healing);
        return Combat.CurrentHp - before;
    }

    public bool UseResource(Resource resource, int amount = 1)
    {
        if (amount <= 0 || resource.Current < amount) return false;
        resource.Current -= amount;
        return true;
    }

    public void RestoreResource(Resource resource, int amount = 1) =>
        resource.Current = Math.Min(resource.Maximum, resource.Current + Math.Max(0, amount));

    /// <summary>Expends a slot of exactly the given level. Returns false when none is available.</summary>
    public bool ExpendSpellSlot(int level)
    {
        var slot = Spellcasting.SlotLevel(level);
        if (slot.Available <= 0) return false;
        slot.Expended++;
        return true;
    }

    public void RestoreSpellSlot(int level)
    {
        var slot = Spellcasting.SlotLevel(level);
        if (slot.Expended > 0) slot.Expended--;
    }

    /// <summary>
    /// Casts a spell: cantrips and rituals-as-rituals are free; otherwise uses the lowest available slot at or
    /// above the requested level. Concentration spells replace any current concentration.
    /// </summary>
    public string CastSpell(Spell spell, int? slotLevel = null, bool asRitual = false)
    {
        string result;
        if (spell.Level == 0 || (asRitual && spell.Ritual))
        {
            result = asRitual ? $"Cast {spell.Name} as a ritual." : $"Cast {spell.Name}.";
        }
        else
        {
            var minimum = Math.Max(spell.Level, slotLevel ?? spell.Level);
            var slot = Spellcasting.Slots.Where(s => s.Level >= minimum && s.Available > 0).MinBy(s => s.Level);
            if (slot is null) return $"No spell slot of level {minimum} or higher available for {spell.Name}.";
            slot.Expended++;
            result = $"Cast {spell.Name} using a level {slot.Level} slot.";
        }
        if (spell.Concentration)
        {
            var dropped = Spellcasting.Concentration;
            Spellcasting.Concentration = spell.Name;
            if (dropped.Length > 0 && dropped != spell.Name) result += $" Dropped concentration on {dropped}.";
        }
        return result;
    }

    public void AddCondition(Condition condition)
    {
        if (!Conditions.Contains(condition)) Conditions.Add(condition);
    }

    public void RemoveCondition(Condition condition) => Conditions.Remove(condition);

    private void FallUnconscious()
    {
        ResetDeathSaves();
        AddCondition(Condition.Unconscious);
    }

    private void Revive()
    {
        ResetDeathSaves();
        RemoveCondition(Condition.Unconscious);
    }
}
