using System.Globalization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Caster movement of class skills, from the client's own ESkillEffectType rows (usmap): Teleport_Caster,
/// Pre_Cast_Teleport_Caster and Pre_SkillStart_Teleport_Caster move the caster to the target's back. The rows come from
/// tools/data/skill-areas.json ('effects', tools/skill_areas.py); the server never keys on a skill id, so every skill that
/// carries one of the rows jumps (Assassin Shadowstrike 13070000, and any other skill an export names).
/// </summary>
public static class SkillMoves
{
    public const string TeleportCaster = "Teleport_Caster", PreCastTeleport = "Pre_Cast_Teleport_Caster", PreStartTeleport = "Pre_SkillStart_Teleport_Caster";

    /// <summary>The Teleport_Caster row of Leaping Slam: the caster lands on its own side of the target, not behind it.</summary>
    public const string CasterToTargetLocation = "TargetLocation_CasterToTargetDirection";

    /// <summary>OURS: the caster lands this far from the target's edge (cm) when the row carries no distance, and counts as this wide.</summary>
    public const double GapCm = 60, CasterRadiusCm = 40;

    public static bool IsCasterTeleport(string type) => type is TeleportCaster or PreCastTeleport or PreStartTeleport;

    /// <summary>The Pre_ rows move the caster as the cast starts; Teleport_Caster moves it when the skill's first hit lands.</summary>
    public static bool IsBeforeHit(string type) => type is PreCastTeleport or PreStartTeleport;

    /// <summary>The skill's teleport row: its own, else its family's (level / spec variants reuse the family's rows).</summary>
    public static string[]? CasterTeleportFor(int skillId) =>
        SkillAreas.For(skillId)?.CasterTeleport ?? (skillId / 10_000 * 10_000 is var family && family != skillId ? SkillAreas.For(family)?.CasterTeleport : null);

    /// <summary>The skill's CasterMove row (CasterMoveDirection on the client export): Back, Forward, ... plus optional distance cm.</summary>
    public static string[]? CasterMoveFor(int skillId) =>
        SkillAreas.For(skillId)?.EffectRows.FirstOrDefault(r => r.Length > 0 && r[0] == "CasterMove")
        ?? (skillId / 10_000 * 10_000 is var family && family != skillId
            ? SkillAreas.For(family)?.EffectRows.FirstOrDefault(r => r.Length > 0 && r[0] == "CasterMove") : null);

    /// <summary>OURS: back-step distance when the row carries no value (Marking Shot ~ Explosion Trap radius scale).</summary>
    public const int DefaultBackstepCm = 500;

    /// <summary>OURS: forward leap cap when CasterMove Forward has no distance (Leaping Slam and similar lunges).</summary>
    public const int DefaultForwardLeapCm = 1500;

    /// <summary>Lands <paramref name="caster"/> at most <paramref name="maxTravelCm"/> toward <paramref name="target"/> (melee gap included).</summary>
    public static Vector3d TowardTargetPoint(Vector3d caster, Vector3d target, double targetRadius, int maxTravelCm)
    {
        var dx = target.X - caster.X;
        var dy = target.Y - caster.Y;
        var planar = Math.Sqrt(dx * dx + dy * dy);
        if (planar < 1) return caster;
        var stop = Math.Max(0, targetRadius) + CasterRadiusCm + GapCm;
        var travel = Math.Min(Math.Max(0, maxTravelCm), Math.Max(0, planar - stop));
        return new Vector3d(caster.X + dx / planar * travel, caster.Y + dy / planar * travel, caster.Z);
    }

    /// <summary>The point behind a target that faces <paramref name="facingDegrees"/>: on the line through its back, at its edge + the gap.</summary>
    public static Vector3d BehindPoint(Vector3d target, double facingDegrees, double targetRadius)
    {
        var distance = Math.Max(0, targetRadius) + CasterRadiusCm + GapCm;
        var angle = facingDegrees * Math.PI / 180;
        return new Vector3d(target.X - Math.Cos(angle) * distance, target.Y - Math.Sin(angle) * distance, target.Z);
    }

    /// <summary>The yaw in degrees (the cast request's convention) that looks from <paramref name="from"/> at <paramref name="to"/>.</summary>
    public static double Facing(Vector3d from, Vector3d to) => Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI;

    /// <summary>A CasterMoveDirection row that lunges the caster toward its target (Leaping Slam and similar).</summary>
    public static bool IsForwardMove(string[] row) =>
        row.Length > 1 && row[1].Equals("Forward", StringComparison.OrdinalIgnoreCase);

    /// <summary>When a forward-attack leap should connect: the skill act end from Skill.dat, else the default hit delay.</summary>
    public static int ForwardLeapImpactMs(SkillDefinition skill, int attackSpeed = 10_000) =>
        skill.EndMs is { } end and > 0 ? Combat.SkillEndMs(end, attackSpeed) : NpcAi.HitDelayMs;

    static int Cm(string[] row, int index) =>
        row.Length > index && int.TryParse(row[index], out var value) ? value : 0;

    /// <summary>
    /// Where a Teleport_Caster row puts the caster. The row is [effect, location, distance cm, angle degrees, height cm];
    /// the location names the frame the angle is measured in:
    /// <list type="bullet">
    /// <item>TargetLocation_CasterToTargetDirection: from the target along the caster's approach line, so 180° is the
    /// caster's own side (Leaping Slam lands in front of its target and slams it, it does not blink behind).</item>
    /// <item>TargetLocation_TargetDirection: from the target along the target's facing, so 180° is its back
    /// (Shadowstrike).</item>
    /// <item>CasterLocation_* / SpawnLocation_*: a move from the caster toward the target, capped at the row's distance.</item>
    /// <item>MapLocation_*: the row's absolute coordinates.</item>
    /// </list>
    /// The distance is added to the target's radius (the caster stops on its edge); 0 falls back to the edge plus the
    /// melee gap. The row's height is ignored (2D, the client plays its own arc).
    /// </summary>
    public static Vector3d TeleportDestination(string[] row, Vector3d caster, Vector3d target, double targetFacing, double targetRadius)
    {
        var location = row.Length > 1 ? row[1] : "";
        if (location.StartsWith("MapLocation", StringComparison.OrdinalIgnoreCase) && MapPoint(row) is { } mapPoint) return mapPoint;
        var distance = Cm(row, 2);
        if (location.StartsWith("CasterLocation", StringComparison.OrdinalIgnoreCase)
            || location.StartsWith("SpawnLocation", StringComparison.OrdinalIgnoreCase))
            return TowardTargetPoint(caster, target, targetRadius, distance > 0 ? distance : DefaultForwardLeapCm);
        var frame = location.Contains("CasterToTarget", StringComparison.OrdinalIgnoreCase) ? Facing(caster, target) : targetFacing;
        var reach = Math.Max(0, targetRadius) + (distance > 0 ? distance : CasterRadiusCm + GapCm);
        var angle = (frame + Cm(row, 3)) * Math.PI / 180;
        var at = new Vector3d(target.X + Math.Cos(angle) * reach, target.Y + Math.Sin(angle) * reach, target.Z);
        // Already inside the landing ring on that side (a target that walked into the caster): stay put instead of pushing back.
        var cx = caster.X - target.X;
        var cy = caster.Y - target.Y;
        return cx * (at.X - target.X) + cy * (at.Y - target.Y) > 0 && cx * cx + cy * cy <= reach * reach ? caster : at;
    }

    static Vector3d? MapPoint(string[] row)
    {
        var parts = row.Length > 2 ? row[2].Split(',', StringSplitOptions.TrimEntries) : [];
        return parts.Length == 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
            ? new Vector3d(x, y, z) : null;
    }
}
