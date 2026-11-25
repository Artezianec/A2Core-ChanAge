using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    static CombatTelegraphOptions combatTelegraphRules = CombatTelegraphOptions.Default;

    /// <summary>Aion:Combat:GroundMarkers (docs/GROUND-TELEGRAPHS.md); tests may reset with <see cref="CombatTelegraphOptions.Default"/>.</summary>
    public static void ConfigureCombatTelegraphs(CombatTelegraphOptions options) =>
        combatTelegraphRules = options.Validated();

    public static CombatTelegraphOptions CombatTelegraphRules => combatTelegraphRules;

    List<byte[]> PlayerGroundTelegraph(int skill, byte actionUid, CastRequest request, SkillDefinition definition)
    {
        // Zone casts (Bittercold Wind) attach the AoE marker when the summon appears, not at accept time.
        if (PcZones.For(skill) is not null) return [];
        var area = CastArea(skill);
        if (area is null || !MechanicTelegraphs.WantsMarker(area)) return [];
        var here = position ?? request.Location;
        var aim = request.Location == Vector3d.Zero ? here : request.Location;
        if (area.OnTarget && request.TargetKey is > 0 and var tk && tk != playerKey
            && targets?.TryGetValue(tk, out var locked) == true)
            aim = locked.Position;
        var duration = MechanicTelegraphs.PlayerLandMs(definition);
        if (area.Projectile > 0)
            duration = Math.Max(duration, definition.EndMs is { } endMs ? Combat.SkillEndMs(endMs, AttackSpeed) : duration);
        if (!combatTelegraphRules.GroundMarkers) return [];
        var centre = MechanicTelegraphs.EffectCentre(area, here, aim);
        return [MechanicTelegraphs.RangeNotice(playerKey, skill, actionUid, effectSeq: 1, centre, request.Yaw, duration)];
    }
}