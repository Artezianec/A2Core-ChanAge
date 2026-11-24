using Aion.Protocol.Native;
using Aion.World;

namespace Aion.World.Tests;

/// <summary>Fast cast simulation: accept, schedule hits, wire 0x3804 effect ids.</summary>
static class LearnableSkillCastAudit
{
    public enum RuntimeStatus { Ok, Fail, Skip }

    public sealed record Row(int SkillId, string Name, RuntimeStatus Status, string Reason);

    static byte[] Cast(int skill, int target, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skill).U8(2).Varint(target).F32(0).Vec3(at).U8(9).ToArray();

    static int CastTarget(GameSession session, SkillDefinition def, Target mob) =>
        def.Target is "MainSelf" or "Self" ? session.PlayerKey : mob.Key;

    static bool Accepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }

    public static async Task<Row> ProbeAsync(GameSession session, GameData data, string className, int skillId, long now, Target mob)
    {
        var def = data.Skill(skillId);
        var name = def?.Name ?? skillId.ToString();
        if (def is null) return new(skillId, name, RuntimeStatus.Fail, "missing_definition");
        if (def.Type is not ("Active" or "Toggle") || SkillCooldown.IsBasicAutoAttack(def))
            return new(skillId, name, RuntimeStatus.Skip, def.Type ?? "skip");
        if (!session.Knows(skillId)) session.LearnForTests(skillId);

        await PrimeAsync(session, data, className, def, now, mob);

        var hp0 = mob.Hp;
        var castAt = now + 5_000;
        var replies = await session.HandleAsync(Cast(skillId, CastTarget(session, def, mob), mob.Position), castAt);
        if (!Accepted(replies))
        {
            var code = GameSession.CastResultCode(replies.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == 0x3801) ?? []);
            return new(skillId, name, RuntimeStatus.Fail, $"cast_refused:{code}");
        }

        var landMs = Math.Max(def.CastingMs, def.Damage is { Length: > 0 }
            ? def.Damage.Max(r => r.HitAtMs) + 200
            : 800) + 600;
        await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt);
        await session.TickAsync(castAt + landMs);

        if (def.Damage is { Length: > 0 })
        {
            foreach (var row in def.Damage)
            {
                var wired = SkillSideData.WireEffectId(row.EffectId, skillId);
                if (replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3804 && ContainsI32(p, wired)))
                    return new(skillId, name, RuntimeStatus.Ok, "ok");
            }
            if (mob.Hp < hp0)
                return new(skillId, name, RuntimeStatus.Ok, "ok");
            return new(skillId, name, RuntimeStatus.Fail, "no_damage");
        }
        return new(skillId, name, RuntimeStatus.Ok, "no_damage_rows");
    }

    static async Task PrimeAsync(GameSession session, GameData data, string className, SkillDefinition def, long now, Target mob)
    {
        if (def.ChainPrev > 0 && data.Skill(def.ChainPrev) is { } prev)
            await session.HandleAsync(Cast(def.ChainPrev, CastTarget(session, prev, mob), mob.Position), now + 100);
        if (def.SkillCond is { Type: var t } cond)
        {
            if (t == SkillCondRegistry.HasAbnormalGroupId && cond.AbnormalGroup is int group)
            {
                foreach (var grantSkill in GrantSkills(data, className, group))
                {
                    if (!session.Knows(grantSkill)) session.LearnForTests(grantSkill);
                if (data.Skill(grantSkill) is { } grantDef)
                    await session.HandleAsync(Cast(grantSkill, CastTarget(session, grantDef, mob), mob.Position), now + 250);
                }
            }
            else if (t == SkillCondRegistry.HasAbnormalEffectType
                && cond.AbnormalEffect is "Stagger" or "Groggy")
                session.BreakNpcStaggerForTests(mob, now + 300, def.ChainPrev > 0 ? def.ChainPrev : mob.Key);
        }
    }

    static IEnumerable<int> GrantSkills(GameData data, string className, int group)
    {
        var profile = data.ProfileForClass(1, 1, className);
        if (profile is null) yield break;
        var kit = data.SkillListForClient(className, data.MaxLevel, profile).Select(k => k.SkillId).ToHashSet();
        foreach (var id in kit)
        {
            if (data.Skill(id) is not { SkillCondGrant: { } g }) continue;
            if (g.Abnormal == group || AbnormalStackRules.GroupOf(g.Abnormal) == group)
                yield return id;
        }
    }
}