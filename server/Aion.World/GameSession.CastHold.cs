using Aion.Protocol.Native;

namespace Aion.World;

// Held skill key: exact duplicate wires inside swing lockout get result 1. Swing lockout (nextCastAcceptAtMs) is speed-scaled
// swing time (cap 1s), not dat cooldown; summons/traps/zones/placement get 0. Global accept gap uses MinCastGapMs (#283).
// Queued early chain steps are dropped when the client stops resending (~200 ms) or sends movement (#291).
public sealed partial class GameSession
{
    readonly record struct HeldCastWireKey(int SkillId, int TargetKey, ushort PresenceMask, int ChangeToggle, bool HasChangeToggle);

    HeldCastWireKey lastAcceptedCastKey;
    long nextCastAcceptAtMs;
    long lastHeldCastWireMs;
    byte[]? queuedCastWire;
    long queuedCastAtMs;
    long queuedSilentSinceMs;
    /// <summary>Longest a silently queued combo press waits for its reply (longest FixSlot swing lockout is under 1 s).</summary>
    const int MaxSilentQueueMs = 1500;

    /// <summary>Client resends 0x3800 / BasicAtk about every 17 ms while the key is held; no packet for ~200 ms means release.</summary>
    internal const int HeldCastActiveMs = 200;

    /// <summary>Clock slack for held lockout (official Ranger chain captures: ~17 ms resend cadence, ~50–100 ms RTT).</summary>
    internal const int CastLockoutToleranceMs = 75;

    static HeldCastWireKey HeldCastWireKeyOf(CastRequest request, byte[] body) =>
        new(request.SkillId, request.TargetKey, BitConverter.ToUInt16(body, 2), request.ChangeToggle, request.HasChangeToggle);

    void ClearHeldCastState()
    {
        lastAcceptedCastKey = default;
        nextCastAcceptAtMs = 0;
        lastHeldCastWireMs = 0;
        queuedCastWire = null;
        queuedCastAtMs = 0;
    }

    const int MaxHeldSwingLockoutMs = 1000;

    static bool SkipsHeldSwingLockout(SkillDefinition definition, int skillId) =>
        definition.Damage.Length == 0 || PcSummons.For(skillId) is not null || PcTraps.For(skillId) is not null || PcZones.For(skillId) is not null;

    /// <summary>Unscaled swing window before CombatSpeed (same basis as 0x3802 attack speed).</summary>
    int HeldSwingBaseMs(SkillDefinition definition, int skillId)
    {
        if (SkipsHeldSwingLockout(definition, skillId)) return 0;
        var projectile = CastArea(skillId) is { Projectile: > 0 };
        var swingEnd = SkillCooldown.SwingEndMs(definition);
        if (SkillCooldown.IsBasicAutoAttack(definition))
            return Math.Max(SkillCooldown.BasicAttackGcdMs, swingEnd);
        if (projectile && definition.CooldownMs == 0)
            return Math.Max(SkillCooldown.ProjectileRecastMs, swingEnd);
        if (definition.Damage.Length > 1 && definition.CooldownMs == 0)
            return Math.Max(SkillCooldown.EffectiveMs(definition), swingEnd);
        return swingEnd > 0 ? swingEnd : SkillCooldown.MinCastGapMs;
    }

    int CombatTimingSpeedBp() => Math.Max(1, AttackSpeed);

    int SpeedScaledMs(int baseMs) =>
        baseMs <= 0 ? 0 : Math.Max(SkillCooldown.MinCastGapMs, (int)((long)baseMs * 10_000 / CombatTimingSpeedBp()));

    int HeldCastLockoutMs(SkillDefinition definition, int skillId)
    {
        var baseMs = HeldSwingBaseMs(definition, skillId);
        if (baseMs <= 0) return 0;
        return Math.Min(SpeedScaledMs(baseMs), MaxHeldSwingLockoutMs);
    }

    /// <summary>Cooldown-group lockout after accept; basics scale with attack speed like pre-#283.</summary>
    int PlayerCastLockoutMs(SkillDefinition definition, int skillId)
    {
        var projectile = CastArea(skillId) is { Projectile: > 0 };
        if (SkillCooldown.IsBasicAutoAttack(definition))
            return SpeedScaledMs(Math.Max(SkillCooldown.BasicAttackGcdMs, SkillCooldown.SwingEndMs(definition)));
        return SkillCooldown.LockoutMs(definition, projectile);
    }

    void ScheduleNextCastAccept(long now, SkillDefinition definition, int skillId, HeldCastWireKey key, bool freeRecast = false)
    {
        lastAcceptedCastKey = key;
        nextCastAcceptAtMs = freeRecast ? now : now + HeldCastLockoutMs(definition, skillId);
    }

    bool CastLockoutOpen(long now) => now + CastLockoutToleranceMs >= nextCastAcceptAtMs;

    int EffectiveMinCastGapMs() => SpeedScaledMs(SkillCooldown.MinCastGapMs);

    bool IsExactHeldCastDuplicate(CastRequest request, byte[] body, long now) =>
        now + CastLockoutToleranceMs < nextCastAcceptAtMs && HeldCastWireKeyOf(request, body) == lastAcceptedCastKey;

    internal long HeldCastLockoutRemainingMs(long now) => nextCastAcceptAtMs <= now ? 0 : nextCastAcceptAtMs - now;

    internal void SetCombatSpeedBonusForTests(int bonusBp) => combatSpeedOverrideBp = bonusBp;

    void NoteHeldCastWireReceived(long now) => lastHeldCastWireMs = now;

    bool HeldCastStillActive(long now) => lastHeldCastWireMs > 0 && now - lastHeldCastWireMs <= HeldCastActiveMs;

    bool HasDeferredQueuedCast(long now) => queuedCastWire is not null && now < queuedCastAtMs;

    List<byte[]> RefuseReplacedQueuedCast()
    {
        if (queuedCastWire is not { } old || ParseCast(old) is not { } request)
        {
            queuedCastWire = null;
            queuedCastAtMs = 0;
            return [];
        }
        queuedCastWire = null;
        queuedCastAtMs = 0;
        return [CastReplyFor(request, false)];
    }

    /// <summary>Key released or movement: refuse a pending queued cast so Tick does not accept an extra swing.</summary>
    List<byte[]> AbandonQueuedHeldCast(long now)
    {
        if (queuedCastWire is null) return [];
        return RefuseReplacedQueuedCast();
    }

    void QueueHeldCastWire(byte[] body, long acceptAtMs)
    {
        queuedCastWire = body;
        queuedCastAtMs = acceptAtMs;
    }

    List<byte[]> DrainQueuedHeldCast(long now)
    {
        if (queuedCastWire is not { } wire) return [];
        if (!HeldCastStillActive(now))
            return AbandonQueuedHeldCast(now);
        if (now < queuedCastAtMs) return [];
        queuedCastWire = null;
        queuedCastAtMs = 0;
        var replies = CastReplies(wire, now);
        pendingSkillCastRefusalReason = null;
        // Still early (re-queued silently by CastReplies): it is answered on the next drain.
        if (replies.Count == 0 && queuedCastWire is not null) return [];
        if (replies.Count == 0)
        {
            if (ParseCast(wire) is { } request)
                return IsDodgeId(request.SkillId)
                    ? DodgeUnlockRefusal(IsCombatDodgeId(request.SkillId) ? request.SkillId : request.SkillId - 1)
                    : [CastReplyFor(request, false)];
            return replies;
        }
        if (replies.Any(p => Opcode(p) == 0x3801 && CastResultCode(p) == 0)) return replies;
        if (ParseCast(wire) is { } refused)
            return IsDodgeId(refused.SkillId)
                ? DodgeUnlockRefusal(IsCombatDodgeId(refused.SkillId) ? refused.SkillId : refused.SkillId - 1)
                : [CastReplyFor(refused, false)];
        return replies;
    }

    void DropQueuedHeldCastSilently()
    {
        queuedCastWire = null;
        queuedCastAtMs = 0;
    }

    /// <summary>
    /// Early FixSlot combo press while held: no reply now (a 0x3801 failure cancels the client's swing animation). The wire
    /// is answered exactly once: success when it fires at lockout end, or failure when it is replaced, abandoned on key
    /// release, or refused at drain, so the client never waits forever (4e89355 freeze).
    /// </summary>
    List<byte[]> QueueBasicChainCastSilently(byte[] body, CastRequest request, int skill, long acceptAtMs, long now)
    {
        var key = HeldCastWireKeyOf(request, body);
        var sameQueued = queuedCastWire is { } prior && ParseCast(prior) is { } priorRequest && HeldCastWireKeyOf(priorRequest, prior) == key;
        var since = sameQueued ? queuedSilentSinceMs : now;
        // Never hold a silent cast open without a reply: a press that keeps re-queueing (refused at every drain while the
        // key stays held) is answered once it waited MaxSilentQueueMs, so the client never waits on it forever.
        if (now - since > MaxSilentQueueMs)
        {
            var dropped = sameQueued ? [] : RefuseReplacedQueuedCast();
            if (sameQueued) DropQueuedHeldCastSilently();
            return [.. dropped, CastReplyFor(request, false)];
        }
        queuedSilentSinceMs = since;
        pendingSkillCastRefusalReason = "queued";
        PlaytestDebug.Action("skill_cast", ("skillId", skill), ("skill", data.Skill(skill)?.Name ?? skill.ToString()),
            ("targetKey", request.TargetKey), ("queued", true));
        if (queuedCastWire is { } existing && ParseCast(existing) is { } queued && HeldCastWireKeyOf(queued, existing) == key)
        {
            queuedCastWire = body;
            return [];
        }
        var replies = RefuseReplacedQueuedCast();
        QueueHeldCastWire(body, acceptAtMs);
        return replies;
    }

    /// <summary>Queue an early chain step; refuses a replaced queued wire and always answers the client (0x3801 failure) so it never locks.</summary>
    List<byte[]> MaybeQueueEarlyChainCast(byte[] body, CastRequest request, bool chainSkill, bool chainAllowed, bool castGapOk, long now, long acceptAtMs)
    {
        if (!chainSkill || !chainAllowed || castGapOk) return [];
        if (data.IsBasicAttackChainSkill(request.SkillId))
            return BasicChainLockoutRefusal(request, now);
        var key = HeldCastWireKeyOf(request, body);
        if (queuedCastWire is { } existing && ParseCast(existing) is { } queued && HeldCastWireKeyOf(queued, existing) == key)
            return [CastReplyFor(request, false)];
        var replies = RefuseReplacedQueuedCast();
        QueueHeldCastWire(body, acceptAtMs);
        replies.Add(CastReplyFor(request, false));
        return replies;
    }
}
