using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    /// <summary>Shortest wait before a cast's 0x3806 end (the dodge's recorded end is +200 ms).</summary>
    public const int MinSkillEndMs = 200;

    readonly List<(int Skill, byte Action, long At)> pendingSkillEnds = [];

    /// <summary>
    /// With the 9-byte 0x3801 accept (#140) the client fired one shot and then waited: it treats the cast as running until its
    /// 0x3806 SkillEnd_NT (live 2026-10-30 13:35, Ranger 14020000: one accept, no further 0x3800). Every accepted non-dodge cast
    /// now ends at its swing end (Skill.dat casting + last hit row), at least <see cref="MinSkillEndMs"/>, as the official
    /// server ends dodges. A projectile skill ends no earlier than its last arrow lands: at range the swing end (600 ms) came
    /// before the landing of Marking Shot (Speed 3500: ~770 ms at 25 m) and ended the cast with its arrow in the air.
    /// </summary>
    void ScheduleSkillEnd(int skill, byte action, SkillDefinition definition, long now)
    {
        var at = now + Math.Max(MinSkillEndMs, SkillCooldown.SwingEndMs(definition));
        foreach (var hit in pendingCombatHits)
            if (hit.SkillId == skill && hit.ActionUid == action && hit.DueMs > at) at = hit.DueMs;
        pendingSkillEnds.Add((skill, action, at));
    }

    List<byte[]> SkillEndTick(long now)
    {
        if (pendingSkillEnds.Count == 0) return [];
        var ended = new List<byte[]>();
        pendingSkillEnds.RemoveAll(e =>
        {
            if (now < e.At) return false;
            ended.Add(GamePackets.SkillEnd(playerKey, e.Skill, e.Action));
            return true;
        });
        if (ended.Count > 0) worldPlayer?.Map?.Broadcast(worldPlayer, ended);
        return ended;
    }

    /// <summary>Client is in a skill until 0x3806 (live 2026-10-30); dodge or the next chain step cancels it early.</summary>
    internal bool SkillCastInProgress(long now) =>
        pendingSkillEnds.Count > 0 || pendingCombatHits.Count > 0 || pendingProjectiles.Count > 0 || now < nextCastAcceptAtMs;

    /// <summary>The last accepted cast is a dodge whose lockout (dash) has not ended yet.</summary>
    bool DodgeStillPlaying(long now) => IsDodgeId(lastAcceptedCastKey.SkillId) && now < nextCastAcceptAtMs + CastLockoutToleranceMs;

    List<byte[]> IgnoreEarlyDodge(CastRequest request, int skill)
    {
        pendingSkillCastRefusalReason = "dodge_in_progress";
        PlaytestDebug.Action("skill_cast", ("skillId", skill), ("skill", data.Skill(skill)?.Name ?? skill.ToString()),
            ("targetKey", request.TargetKey), ("ignored", "dodge_in_progress"));
        return [];
    }

    /// <summary>A refused dodge still ends the swing or dodge in progress so the client leaves the dash state.</summary>
    List<byte[]> DodgeRefusalRelease(long now) => SkillCastInProgress(now) ? InterruptActiveSkillCasts(now) : [];

    bool ShouldInterruptActiveCast(long now, bool dodging, bool chainSkill, bool chainAllowed, bool heldDuplicate) =>
        SkillCastInProgress(now) && (dodging || (chainSkill && chainAllowed && !heldDuplicate));

    /// <summary>
    /// Official: dodge (or the next open chain step) cancels the swing in progress with 0x3806. FixSlot basic combos still
    /// land arrows / rows already scheduled (live Ranger: Rapid Fire must not strip Snipe's in-flight shot, nor Spiral
    /// Rapid Fire's three rows when the next chain step opens early).
    /// </summary>
    List<byte[]> InterruptActiveSkillCasts(long now, bool dropScheduledHits = true)
    {
        if (pendingSkillEnds.Count == 0 && pendingCombatHits.Count == 0 && pendingProjectiles.Count == 0 && now >= nextCastAcceptAtMs)
            return [];
        var ended = new List<byte[]>();
        var cancelled = new HashSet<(int Skill, byte Action)>();
        foreach (var (skill, action, _) in pendingSkillEnds.ToList())
        {
            cancelled.Add((skill, action));
            ended.Add(GamePackets.SkillEnd(playerKey, skill, action));
        }
        pendingSkillEnds.Clear();
        if (cancelled.Count > 0)
        {
            if (dropScheduledHits)
            {
                pendingCombatHits.RemoveAll(h => cancelled.Contains((h.SkillId, h.ActionUid)));
                pendingProjectiles.RemoveAll(p => cancelled.Contains((p.SkillId, p.ActionUid)));
                var skills = cancelled.Select(c => c.Skill).ToHashSet();
                pendingSkillEffects.RemoveAll(e => skills.Contains(e.SkillId));
            }
        }
        else if (now < nextCastAcceptAtMs)
        {
            pendingCombatHits.Clear();
            pendingProjectiles.Clear();
        }
        nextCastAcceptAtMs = Math.Min(nextCastAcceptAtMs, now);
        if (ended.Count > 0) worldPlayer?.Map?.Broadcast(worldPlayer, ended);
        return ended;
    }
}
