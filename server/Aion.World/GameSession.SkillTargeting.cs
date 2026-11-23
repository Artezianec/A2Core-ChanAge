using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    /// <summary>Extra reach beyond Skill.dat NeedSkillUseRange for latency and collision (OURS).</summary>
    public const double CastRangeToleranceCm = 150;

    /// <summary>Where the server places the character: the last accepted position, else its spawn point (before any move).</summary>
    public Vector3d? KnownPosition => position ?? spawn?.Position;

    /// <summary>The centre of a self-centred area cast: the client's location while it lies within the skill's UseRange plus
    /// <see cref="CastRangeToleranceCm"/> of the server's position (horizontally), else the server's position. OURS: the client's
    /// location is never trusted further than the skill reaches (a sent location next to a far monster used to hit it).</summary>
    public Vector3d AreaCentre(SkillDefinition? definition, Vector3d claimed) => AreaCentre(definition, KnownPosition, claimed);

    public static Vector3d AreaCentre(SkillDefinition? definition, Vector3d? server, Vector3d claimed)
    {
        if (server is not { } at) return claimed;
        var maxReach = Math.Max(0, definition?.UseRangeCm ?? 0) + CastRangeToleranceCm;
        return at.Distance2d(claimed) > maxReach ? at : claimed;
    }

    /// <summary>Max vertical separation for a valid skill target (SkillEffectCondition Distance_Height; 2D combat OURS).</summary>
    public const double CastHeightToleranceCm = 500;

    /// <summary>Whether a cast may resolve on its target or ground point (Skill.dat NeedSkillUseRange).</summary>
    internal bool CastTargetReachValidForTests(SkillDefinition definition, int skillId, CastRequest request) =>
        CastTargetReachValid(definition, skillId, request);

    internal void SetPositionForTests(Vector3d at) => position = at;

    /// <summary>Hold-charge 0x381a repeat and 0x381d release: Skill.dat range + projectile aim sanity (no client pass_los).</summary>
    internal bool HoldChargeTargetValidForTests(SkillDefinition definition, int skillId, CastRequest request) =>
        HoldChargeTargetValid(definition, skillId, request);

    bool HoldChargeTargetValid(SkillDefinition definition, int skillId, CastRequest request)
    {
        if (!CastTargetReachValid(definition, skillId, request)) return false;
        if (CastArea(skillId) is not { Projectile: > 0 }) return true;
        var caster = position ?? request.Location;
        var range = data.SkillUseRangeCm(skillId);
        if (range <= 0) return true;
        var max = range + CastRangeToleranceCm;
        if (HorizontalDistance(caster, request.Location) > max) return false;
        if (request.TargetKey >= SharedWorld.FirstPlayerKey) return true;
        if (DestructibleEnvObjAt(request.TargetKey) is { } env) return HorizontalDistance(request.Location, env.At) <= max + env.InteractRange;
        if (targets?.TryGetValue(request.TargetKey, out var npc) != true || npc is null) return false;
        return HorizontalDistance(request.Location, npc.Position) <= max + npc.Radius;
    }

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
        // Object keys (WorldObjects.FirstKey) sit above FirstPlayerKey; destructible env objects (EnvObj_Destroy) are no player or NPC targets: the range is measured to the object, as the client does.
        if (DestructibleEnvObjAt(request.TargetKey) is { } env)
            return HorizontalDistance(caster, env.At) <= max + env.InteractRange
                && Math.Abs(caster.Z - env.At.Z) <= CastHeightToleranceCm;
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

    WorldObject? DestructibleEnvObjAt(int key) =>
        DestructibleEnvObjIndex(key) is { } index ? WorldObjects.ObjectsOn(worldMap)[index] : null;

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
