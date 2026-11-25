using Aion.Protocol.Native;

namespace Aion.World;

// Abnormal engine v2 on the player session: passive stat buffs, conditional proc subscriptions, death/logout cleanup.
partial class GameSession
{
    /// <summary>
    /// Applies hidden passive abnormals for every learned passive that has server-known effect rows.
    /// Wire passives (weapon permissions) stay <see cref="PassiveAbnormals"/>; this is the stat modifier layer.
    /// </summary>
    internal List<byte[]> RefreshPassiveStatBuffs(long now, bool resendWire = false)
    {
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
        var wanted = new HashSet<int>();
        foreach (var skill in learned)
        {
            foreach (var abnormal in LearnedPassiveAbnormals(skill))
            {
                if (AbnormalSpecs.Info(abnormal) is null && !SkillEffectEngine.HasTriggers(abnormal)) continue;
                wanted.Add(abnormal);
            }
        }
        var remove = buffs.Where(b => b.PassiveHidden && !wanted.Contains(b.AbnormalId)).ToList();
        if (remove.Count > 0) replies.AddRange(RemoveBuffs(remove, ExpireEtc));
        foreach (var abnormal in wanted)
        {
            if (buffs.Any(b => b.PassiveHidden && b.AbnormalId == abnormal)) continue;
            AddBuff(abnormal, -1, 0, now, replies, path: SelfAbnormalPath.Unrestricted, passiveHidden: true);
        }
        if (resendWire) replies.AddRange(PassiveAbnormals(resend: true));
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>Conditional passive procs after combat events (data in passive-triggers.json).</summary>
    List<byte[]> FirePassiveTriggers(PassiveTriggers.Kind kind, long now, int damage = 0, bool critical = false)
    {
        if (dead || resources is null) return [];
        var replies = new List<byte[]>();
        var hpPermille = resources.HpMax > 0 ? resources.Hp * 10_000 / resources.HpMax : 10_000;
        foreach (var trigger in PassiveTriggers.ForLearned(learned))
        {
            if (trigger.Kind != kind) continue;
            if (SkillEffectEngine.ClientTriggers && LegacyTriggerCovered(trigger.SkillId)) continue;
            if (kind == PassiveTriggers.Kind.OnCrit && !critical) continue;
            if (kind == PassiveTriggers.Kind.OnHpBelow && hpPermille > trigger.HpPermilleThreshold) continue;
            if (kind == PassiveTriggers.Kind.OnDamaged && damage <= 0) continue;
            foreach (var row in trigger.EffectRows)
                replies.AddRange(ApplyPassiveTriggerRow(row, trigger.SkillId, now));
        }
        return replies;
    }

    List<byte[]> ApplyPassiveTriggerRow(string[] row, int skillId, long now)
    {
        if (row.Length == 0) return [];
        if (row[0] == "Abnormal_True" && row.Length > 3 && int.TryParse(row[3], out var abnormal))
        {
            var speedBefore = BuffStat("MoveSpeed");
            var inner = new List<byte[]>();
            AddBuff(abnormal, Math.Max(1, rng.Next(Math.Min(SkillEffects.Value(row, 0), SkillEffects.Value(row, 1)), Math.Max(SkillEffects.Value(row, 0), SkillEffects.Value(row, 1)) + 1)), skillId, now, inner, path: SelfAbnormalPath.FriendlyReceive);
            inner.AddRange(BuffStatsChanged(speedBefore));
            return inner;
        }
        if (SkillEffects.HealTypes.Contains(row[0])) return ReceiveFriendlyRow(row, now);
        return [];
    }

    /// <summary>A passive-triggers.json entry whose skill's passive abnormals carry client trigger rows: the engine fires those instead.</summary>
    static bool LegacyTriggerCovered(int skillId) =>
        SkillEffectEngine.HasTriggers(skillId) || Boxes.Passives(skillId).Concat(SkillEffectEngine.PassiveAbnormals(skillId)).Any(SkillEffectEngine.HasTriggers);

    internal void AfterCombatHitDealtCrit(bool critical, long now)
    {
        var replies = FirePassiveTriggers(PassiveTriggers.Kind.OnHit, now, critical: critical);
        if (critical) replies.AddRange(FirePassiveTriggers(PassiveTriggers.Kind.OnCrit, now, critical: true));
        if (replies.Count > 0) worldPlayer?.Send(replies);
    }

    List<byte[]> AfterPlayerDamaged(int damage, long now, int attackerKey = 0)
    {
        var replies = AfterPlayerDamagedByEngine(damage, attackerKey, now);
        replies.AddRange(FirePassiveTriggers(PassiveTriggers.Kind.OnDamaged, now, damage: damage));
        if (resources is { HpMax: > 0 })
            replies.AddRange(FirePassiveTriggers(PassiveTriggers.Kind.OnHpBelow, now, damage: damage));
        return replies;
    }

    internal void AddPassiveHiddenBuffForTests(int abnormalId, long now, List<byte[]> replies) =>
        AddBuff(abnormalId, -1, 0, now, replies, passiveHidden: true);

    internal int BuffStatForTests(string stat) => BuffStat(stat);

    internal List<byte[]> CleanseDebuffsForTests(long now) => Cleanse(AbnormalSpecs.Debuff, int.MaxValue, now);

    /// <summary>Tests: mark a debuff expired without running BuffTick (stale combat flags must not block movement).</summary>
    internal void ForceExpireBuffForTests(int abnormalId) { foreach (var b in buffs.Where(b => b.AbnormalId == abnormalId)) b.ExpireAt = 0; }
}