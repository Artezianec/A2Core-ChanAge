using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    /// <summary>Extra reach beyond Skill.dat NeedSkillUseRange for latency and collision (OURS).</summary>
    public const double CastRangeToleranceCm = 150;

    /// <summary>Max vertical separation for a valid skill target (SkillEffectCondition Distance_Height; 2D combat OURS).</summary>
    public const double CastHeightToleranceCm = 500;

    /// <summary>Whether a cast may resolve on its target or ground point (Skill.dat NeedSkillUseRange).</summary>
    internal bool CastTargetReachValidForTests(SkillDefinition definition, int skillId, CastRequest request) =>
        CastTargetReachValid(definition, skillId, request);

    internal void SetPositionForTests(Vector3d at) => position = at;

    bool CastTargetReachValid(SkillDefinition definition, int skillId, CastRequest request)
    {
        var range = data.SkillUseRangeCm(skillId);
        if (range <= 0) return true;
        var max = range + CastRangeToleranceCm;
        var caster = position ?? request.Location;
        var ground = SkillAreas.For(skillId) is { IsGround: true } ga ? ga : CastArea(skillId) is { IsGround: true } gb ? gb : null;
        if (ground is not null)
        {
            var at = ground.Ground == "Location" ? request.Location : caster;
            if (HorizontalDistance(caster, at) > max) return false;
            return Math.Abs(caster.Z - at.Z) <= CastHeightToleranceCm;
        }
        if (request.TargetKey == playerKey) return true;
        if (request.TargetKey >= SharedWorld.FirstPlayerKey)
        {
            if (worldPlayer?.Map?.Players.GetValueOrDefault(request.TargetKey) is not { } victim) return false;
            return HorizontalDistance(caster, victim.Position) <= max
                && Math.Abs(caster.Z - victim.Position.Z) <= CastHeightToleranceCm;
        }
        if (targets?.TryGetValue(request.TargetKey, out var npc) != true || npc is null) return false;
        return HorizontalDistance(caster, npc.Position) <= max + npc.Radius
            && Math.Abs(caster.Z - npc.Position.Z) <= CastHeightToleranceCm;
    }

    static double HorizontalDistance(Vector3d a, Vector3d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    bool TargetWithinSkillRange(Target target, int skillId, Vector3d caster)
    {
        var range = data.SkillUseRangeCm(skillId);
        if (range <= 0) return true;
        var max = range + CastRangeToleranceCm + target.Radius;
        if (HorizontalDistance(caster, target.Position) > max) return false;
        return Math.Abs(caster.Z - target.Position.Z) <= CastHeightToleranceCm;
    }

    /// <summary>Filters scheduled combat targets to those within skill range (area shape is applied separately).</summary>
    List<Target> FilterCombatTargets(IReadOnlyList<Target> chosen, int skillId, CastRequest request)
    {
        if (chosen.Count == 0) return [];
        var caster = position ?? request.Location;
        return chosen.Where(t => TargetWithinSkillRange(t, skillId, caster)).ToList();
    }
}
