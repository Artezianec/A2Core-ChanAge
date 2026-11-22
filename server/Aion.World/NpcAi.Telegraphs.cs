using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class NpcAi
{
    /// <summary>When a ground AoE marker is sent; return extra packets (Fire Temple walls, custom mechanics).</summary>
    public Func<MechanicTelegraphs.Started, IReadOnlyList<byte[]>>? MechanicTelegraphStarted;

    List<byte[]> TelegraphForArea(Target caster, int skillId, byte actionUid, Vector3d aim, double yawDegrees, int durationMs, long now)
    {
        var area = SkillAreas.For(skillId);
        return MechanicTelegraphs.ForNpcArea(GameSession.CombatTelegraphRules, caster, skillId, actionUid, area, aim,
            yawDegrees, durationMs, now, MechanicTelegraphStarted);
    }
}