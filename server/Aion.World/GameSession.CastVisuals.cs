using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    /// <summary>
    /// Sorcerer Hellfire (family 15060000): the client expects three cast SkillEffect rows (...0011 on self,
    /// ...0021 and ...0031 on the target) before the projectile row; without them the skill shows no VFX and
    /// damage rows never land (live 2026-11-25, OBSERVED world-npcs skill_effects timing).
    /// </summary>
    List<byte[]> HellfireCastVisuals(int skill, CastRequest request, byte actionUid)
    {
        if (SkillSideData.Family(skill) != 15_060_000) return [];
        var family = SkillSideData.Family(skill);
        var visualTarget = request.TargetKey is > 0 and var k && k != playerKey ? k : playerKey;
        var replies = new List<byte[]>
        {
            GamePackets.SkillEffect(playerKey, playerKey, skill, actionUid, SkillSideData.WireEffectId(family * 100 + 11, skill)),
        };
        if (visualTarget != playerKey)
        {
            replies.Add(GamePackets.SkillEffect(visualTarget, playerKey, skill, actionUid, SkillSideData.WireEffectId(family * 100 + 21, skill)));
            replies.Add(GamePackets.SkillEffect(visualTarget, playerKey, skill, actionUid, SkillSideData.WireEffectId(family * 100 + 31, skill)));
        }
        return replies;
    }
}