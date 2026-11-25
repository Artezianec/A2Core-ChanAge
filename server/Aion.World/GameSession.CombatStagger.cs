namespace Aion.World;

public sealed partial class GameSession
{
    sealed record PendingStaggerBreakFollowup(int BossKey, long DueMs);

    readonly List<PendingStaggerBreakFollowup> pendingStaggerBreakFollowups = [];

    internal List<byte[]> BreakNpcStagger(Target target, long now, int breakingSkillId = 0)
    {
        target.StaggerUntil = now + StaggerGaugeCombat.StaggerDurationMs;
        var abnormalId = StaggerGaugeCombat.StaggerBreakAbnormalId(breakingSkillId);
        var sourceSkill = breakingSkillId > 0 ? breakingSkillId : 0;
        var replies = new List<byte[]>();
        ApplyNpcDebuff(target, abnormalId, StaggerGaugeCombat.StaggerDurationMs, now, replies, held: true, skillId: sourceSkill);
        pendingStaggerBreakFollowups.Add(new(target.Key, now + StaggerGaugeCombat.StaggerBreakFollowupDelayMs));
        return replies;
    }

    /// <summary>Stagger window ended: clear NPC buff, groggy refill, and hotbar context for stagger skills.</summary>
    internal List<byte[]> NpcStaggerGaugeTick(long now)
    {
        var replies = ProcessPendingStaggerBreakFollowups(now);
        if (targets is null) return replies;
        var staggerExpired = false;
        foreach (var t in targets.Values)
        {
            if (t.StaggerUntil <= 0 || now < t.StaggerUntil) continue;
            staggerExpired = true;
            RemoveNpcBuffs(t, b => StaggerGaugeCombat.IsGroggyBreakStaggerAbnormal(b.AbnormalId), ExpireTimeout);
        }
        if (staggerExpired) staggerReactiveTargetKey = null;
        replies.AddRange(StaggerGaugeCombat.StaggerTick(targets, now));
        return replies;
    }

    List<byte[]> ProcessPendingStaggerBreakFollowups(long now)
    {
        if (pendingStaggerBreakFollowups.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var pending in pendingStaggerBreakFollowups.Where(p => now >= p.DueMs).ToList())
        {
            pendingStaggerBreakFollowups.Remove(pending);
            if (targets?.TryGetValue(pending.BossKey, out var boss) != true || boss is null || boss.Dead) continue;
            ApplyNpcDebuff(boss, StaggerGaugeCombat.StaggerBossReactionAbnormalId, StaggerGaugeCombat.StaggerDurationMs, now, replies,
                held: true, skillId: 10_000_009, casterKey: boss.Key);
            AddBuff(StaggerGaugeCombat.StaggerPlayerReactionAbnormalId, 1000, 10_000_009, now, replies, path: SelfAbnormalPath.CombatHit);
        }
        return replies;
    }

    internal List<byte[]> BreakNpcStaggerForTests(Target target, long now, int breakingSkillId = 0) =>
        BreakNpcStagger(target, now, breakingSkillId);

    internal List<byte[]> ApplyStaggerGaugeOnNpcHit(Target target, int skillId, int row, long now, int hitDamage = 0)
    {
        var gauge = StaggerGaugeCombat.GaugeDamage(data, skillId, target, hitDamage);
        return StaggerGaugeCombat.AddGaugeDamage(target, gauge, playerKey, now, this, row == 0, breakingSkillId: skillId);
    }

    bool NpcTargetHasAbnormalEffect(int targetKey, string effectType, long now)
    {
        if (effectType.Equals("Stagger", StringComparison.OrdinalIgnoreCase))
            return NpcHasGroggyBreakStagger(targetKey, now);
        if (!npcBuffs.TryGetValue(targetKey, out var list)) return false;
        return list.Any(b => b.ExpireAt > now && AbnormalSpecs.Types(b.AbnormalId).Any(t => t.Equals(effectType, StringComparison.OrdinalIgnoreCase)));
    }

    internal bool NpcHasGroggyBreakStagger(int targetKey, long now) =>
        npcBuffs.TryGetValue(targetKey, out var list)
        && list.Any(b => b.ExpireAt > now && StaggerGaugeCombat.IsGroggyBreakStaggerAbnormal(b.AbnormalId, b.SkillId));

    internal bool PlayerHasGroggyBreakStagger(long now) =>
        buffs.Any(b => now < b.ExpireAt && StaggerGaugeCombat.IsGroggyBreakStaggerAbnormal(b.AbnormalId, b.SkillId));
}
