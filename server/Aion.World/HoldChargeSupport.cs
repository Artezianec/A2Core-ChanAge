namespace Aion.World;

/// <summary>
/// Hold-to-release skills (RMB / 0x381a): charge step from server hold time between start and release.
/// <c>hold_charge</c> on <see cref="SkillDefinition"/> is optional until SkillCharging*.dat is exported.
/// </summary>
public static class HoldChargeSupport
{
    public static int MaxHoldMs(SkillDefinition definition) =>
        definition.HoldCharge?.MaxHoldMs is int ms and > 0 ? ms : GameSession.SkillChargeMaxHoldMs;

    public static int StageCount(SkillDefinition definition) =>
        definition.HoldCharge?.Levels is { Length: > 0 } levels ? levels.Length : Math.Max(1, definition.Damage.Length);

    public static int StepForElapsed(SkillDefinition definition, long chargeBuildMs)
    {
        var stages = StageCount(definition);
        if (stages <= 1) return 1;
        var maxHold = MaxHoldMs(definition);
        var capped = Math.Min(Math.Max(0, chargeBuildMs), maxHold);
        if (definition.HoldCharge?.Levels is { Length: > 0 } levels)
        {
            var step = 1;
            for (var i = 0; i < levels.Length; i++)
            {
                if (capped >= levels[i].MinHoldMs) step = i + 1;
            }
            return step;
        }
        return Math.Clamp((int)(capped * stages / (double)maxHold) + 1, 1, stages);
    }

    /// <summary>0-based damage row index for a 1-based charging step.</summary>
    public static int DamageRowIndex(SkillDefinition definition, int step)
    {
        if (definition.Damage.Length == 0) return 0;
        if (definition.HoldCharge?.Levels is { Length: > 0 } levels)
        {
            var idx = Math.Clamp(step - 1, 0, levels.Length - 1);
            var row = levels[idx].DamageRow;
            return row >= 0 ? Math.Clamp(row, 0, definition.Damage.Length - 1) : idx;
        }
        return Math.Clamp(step - 1, 0, definition.Damage.Length - 1);
    }
}