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
        var area = CastArea(skill);
        if (area is null || !MechanicTelegraphs.WantsMarker(area)) return [];
        var here = position ?? request.Location;
        var aim = request.Location == Vector3d.Zero ? here : request.Location;
        var duration = MechanicTelegraphs.PlayerLandMs(definition);
        if (targets is null || !targets.TryGetValue(playerKey, out var self)) return [];
        return MechanicTelegraphs.ForNpcArea(combatTelegraphRules, self, skill, actionUid, area, aim, request.Yaw, duration, 0, null);
    }
}