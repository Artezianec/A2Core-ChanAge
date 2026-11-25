using Aion.Protocol.Native;

namespace Aion.World;

// Skill effect engine on the player session (docs/SKILL-EFFECT-ENGINE.md): the client TriggerSkillBy* rows of the player's
// abnormals (learned passives' hidden buffs included) fire their proc skills on combat events; Damage rows with drain columns
// heal the caster; OnExpireAbnormal, AbnormalClear HpDecreaseCount and DecreaseCooltimeByKill follow the abnormal's life cycle.
// The boss mechanics engine (NpcAi) and scripts reach the same handlers through EngineApplyAbnormal / EngineCastProcSkill.
partial class GameSession
{
    /// <summary>A combat event the client trigger rows listen to.</summary>
    internal readonly record struct TriggerEvent(
        string On,
        int SkillId = 0,
        int OtherKey = 0,
        int Damage = 0,
        bool Critical = false,
        int AbnormalId = 0,
        bool VictimPlayer = false);

    readonly Dictionary<(int Abnormal, int Effect), long> triggerReadyAt = [];
    readonly Dictionary<(int Uid, int Effect), int> triggerCounts = [];
    int triggerDepth;
    (int Skill, long At) lastDrain;
    const int DrainWindowMs = 400;

    /// <summary>(HP, max HP) for HP-ratio NPC skills (NpcAi.PlayerHpOf).</summary>
    internal (int Hp, int HpMax) HpPair => resources is { } r ? (r.Hp, r.HpMax) : (0, 0);

    static bool IsBossTarget(Target t) => t.SubType is "HeroMonster" or "LegendMonster";

    /// <summary>Fires the trigger rows of the player's abnormals for one event; the proc replies (the caller sends them).</summary>
    internal List<byte[]> FireClientTriggers(TriggerEvent ev, long now)
    {
        if (!SkillEffectEngine.ClientTriggers || dead || resources is null || triggerDepth > 1) return [];
        var replies = new List<byte[]>();
        triggerDepth++;
        try
        {
            foreach (var buff in buffs.Where(b => b.ExpireAt > now && SkillEffectEngine.HasTriggers(b.AbnormalId)).ToList())
                foreach (var spec in SkillEffectEngine.Triggers(buff.AbnormalId))
                {
                    if (spec.Variant || spec.On != ev.On) continue;
                    var level = spec.Skill > 0 ? Math.Max(1, SkillLevel(spec.Skill)) : Math.Max(1, buff.Level);
                    var at = SkillEffectEngine.TriggerAt(spec, level);
                    if (!TriggerMatches(at, ev, now)) continue;
                    if (IsEmergencyHeal(at) && resources.HpMax > 0 && (long)resources.Hp * 10_000 / resources.HpMax > EmergencyHealHpRatio) continue;
                    var readyKey = (buff.AbnormalId, at.EffectId);
                    if (triggerReadyAt.TryGetValue(readyKey, out var ready) && now < ready) continue;
                    var countKey = (buff.Uid, at.EffectId);
                    if (at.MaxCount > 0 && triggerCounts.GetValueOrDefault(countKey) >= at.MaxCount) continue;
                    if (at.Chance < 10_000 && rng.Next(10_000) >= at.Chance) continue;
                    if (at.CooldownMs > 0) triggerReadyAt[readyKey] = now + at.CooldownMs;
                    if (at.MaxCount > 0) triggerCounts[countKey] = triggerCounts.GetValueOrDefault(countKey) + 1;
                    var done = CastProcSkill(at.ProcSkill, level, ev.OtherKey, now, replies);
                    PlaytestDebug.Action("skill_trigger", ("on", ev.On), ("abnormalId", buff.AbnormalId), ("effectId", at.EffectId),
                        ("procSkill", at.ProcSkill), ("proc", SkillEffectEngine.SkillName(at.ProcSkill)), ("level", level), ("rows", done));
                }
        }
        finally { triggerDepth--; }
        return replies;
    }

    /// <summary>
    /// OURS (CANDIDATE): an OnDamaged proc that heals HP with a cooldown of 30 s or more (Blood Absorption 26%, Revitalization
    /// Contract 35%, 60 s) only fires at or below half HP. The export has no HP-threshold column for these rows; firing on the
    /// first scratch would waste the minute-long cooldown, which cannot be the design.
    /// </summary>
    const int EmergencyHealHpRatio = 5_000;

    static bool IsEmergencyHeal(SkillEffectEngine.TriggerSpec spec) =>
        spec.On == "OnDamaged" && spec.CooldownMs >= 30_000
        && SkillEffectEngine.Proc(spec.ProcSkill)?.Rows.Any(r => r.OnSelf && r.Type is "HpHeal" or "HpHealRatio") == true;

    bool TriggerMatches(SkillEffectEngine.TriggerSpec spec, TriggerEvent ev, long now)
    {
        if (spec.Results is { Length: > 0 } results && ev.On is "OnHit" or "OnDamaged")
        {
            var result = ev.Critical ? "Critical" : "Normal";
            if (!results.Contains(result, StringComparer.OrdinalIgnoreCase)) return false;
        }
        if (spec.Element is { Length: > 0 } element
            && !string.Equals(SkillEffectEngine.ElementOf(ev.SkillId), element, StringComparison.OrdinalIgnoreCase)) return false;
        var other = ev.OtherKey != 0 && targets?.TryGetValue(ev.OtherKey, out var t) == true ? t : null;
        if (spec.BossOnly && (other is null || !IsBossTarget(other))) return false;
        if (spec.TargetEffect is { Length: > 0 } effects)
        {
            if (other is null) return false;
            var any = effects.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(e => NpcHasAbnormalEffect(other.Key, e, now, playerKey));
            if (!any) return false;
        }
        if (ev.On == "OnKill" && spec.Victim is { Length: > 0 } victim)
        {
            var kinds = victim.Split(',', StringSplitOptions.TrimEntries);
            if (!kinds.Contains(ev.VictimPlayer ? "PC" : "NPC", StringComparer.OrdinalIgnoreCase)) return false;
        }
        if (ev.On is "OnGetAbnormal" or "OnAddAbnormal")
        {
            if (spec.Category is { Length: > 0 } category)
            {
                var debuff = AbnormalSpecs.Debuff(ev.AbnormalId);
                if (category.Equals("DeBuff", StringComparison.OrdinalIgnoreCase) != debuff) return false;
            }
            if (spec.Match > 0 && ev.AbnormalId != spec.Match && AbnormalStackRules.GroupOf(ev.AbnormalId) != spec.Match) return false;
        }
        return true;
    }

    /// <summary>
    /// Runs a proc skill's SkillEffect rows at a level: heals and resource gains on the player, abnormals on the player (self rows)
    /// or on <paramref name="otherKey"/> (target rows), cleanses / purges, cooldown changes, damage to the target. Returns the rows run.
    /// </summary>
    int CastProcSkill(int procSkill, int level, int otherKey, long now, List<byte[]> replies)
    {
        if (SkillEffectEngine.Proc(procSkill) is not { } proc) return 0;
        var done = 0;
        var other = otherKey != 0 && targets?.TryGetValue(otherKey, out var t) == true && !t.Dead ? t : null;
        foreach (var row in proc.Rows)
        {
            var values = SkillEffectEngine.EffectValuesAt(row.LevelGroup, level, row.Values);
            string[] r = [row.Type, .. values];
            int V(int i) => i < values.Count && int.TryParse(values[i], out var v) ? v : 0;
            switch (row.Type)
            {
                case var heal when SkillEffects.HealTypes.Contains(heal):
                    replies.AddRange(Heal(r, now));
                    done++;
                    break;
                case "Abnormal_True" or "Abnormal" or "Abnormal_Cc" when V(2) > 0:
                {
                    var chance = values.Count > 4 && V(4) > 0 ? V(4) : 10_000;
                    if (chance < 10_000 && rng.Next(10_000) >= chance) break;
                    var duration = Math.Max(1, rng.Next(Math.Min(V(0), V(1)), Math.Max(V(0), V(1)) + 1));
                    if (row.OnSelf)
                    {
                        var speedBefore = BuffStat("MoveSpeed");
                        AddBuff(V(2), duration, procSkill, now, replies, path: SelfAbnormalPath.FriendlyReceive);
                        replies.AddRange(BuffStatsChanged(speedBefore));
                    }
                    else if (other is not null)
                        ApplyNpcDebuff(other, V(2), duration, now, replies, skillId: procSkill, casterKey: playerKey);
                    else if (PvpVictimOf(otherKey) is { } victim)
                        victim.Send(victim.Session.TryApplyPlayerDebuff(V(2), duration, now, position, playerKey));
                    done++;
                    break;
                }
                case "Dispel_True" or "Dispel_Cc" or "Dispel":
                    if (row.OnSelf) replies.AddRange(ReceiveFriendlyRow(r, now));
                    else if (other is not null) PurgeNpc(other, row.Type == "Dispel" ? Math.Max(1, V(0)) : int.MaxValue);
                    done++;
                    break;
                case "DecreaseCooltime":
                    replies.AddRange(AdjustCooldowns(-PositiveEffectMs(V(0)), V(1), now));
                    done++;
                    break;
                case "Damage" when other is not null:
                {
                    var damageRow = new DamageRow([V(0), V(1)], [V(2), V(3)], null, row.Id, Value4: values.Count > 4 ? values[4] : null);
                    var hit = PlayerRowHits([damageRow], other.Defense, victimLevel: other.Level)[0];
                    replies.AddRange(ProcDamage(other, procSkill, row.Id, hit.Amount, hit.Critical, now));
                    done++;
                    break;
                }
                default:
                    SkillEffectRegistry.NoteSkillEffect(procSkill, row.Type);
                    break;
            }
        }
        return done;
    }

    List<byte[]> ProcDamage(Target npc, int skill, int effectId, int damage, bool critical, long now)
    {
        if (npc.Dead || npc.Immune || damage <= 0 || !PlayerMayDamageNpc(npc)) return [];
        damage = npc.Landed(damage);
        npc.Hp = Math.Max(0, npc.Hp - damage);
        var replies = new List<byte[]>
        {
            GamePackets.SkillHit(npc.Key, playerKey, skill, effectId, damage, critical, 1),
            GamePackets.NpcHp(npc.Key, npc.Hp),
        };
        if (npc.Dead) replies.AddRange(PlayerKilledNpc(npc, skill, playerKey, now, critical));
        else if (targets is not null) replies.AddRange(ai.Engage(targets, npc, now, playerKey, Threat(damage)));
        return replies;
    }

    /// <summary>
    /// After one of the player's hits landed on an NPC: life steal (once per cast), the client OnHit triggers, then the
    /// passive-triggers.json procs the client rows do not cover.
    /// </summary>
    internal List<byte[]> AfterCombatHitDealt(int skillId, Target target, int damage, bool critical, long now)
    {
        var replies = new List<byte[]>();
        if (SkillEffectEngine.ClientTriggers && SkillEffectEngine.DrainOf(skillId) is { } drain && resources is { } r && !dead
            && !(lastDrain.Skill == skillId && now - lastDrain.At < DrainWindowMs && drain.Kind.EndsWith("Once", StringComparison.Ordinal)))
        {
            var amount = SkillEffectEngine.DrainAmount(drain, damage, r.HpMax, critical, rng);
            if (amount > 0)
            {
                lastDrain = (skillId, now);
                r.Hp = Math.Min(r.HpMax, r.Hp + amount);
                replies.Add(GamePackets.NpcHp(playerKey, r.Hp));
                PlaytestDebug.Action("skill_drain", ("skillId", skillId), ("damage", damage), ("healed", amount));
            }
        }
        replies.AddRange(FireClientTriggers(new TriggerEvent("OnHit", skillId, target.Key, damage, critical), now));
        replies.AddRange(FirePassiveTriggers(PassiveTriggers.Kind.OnHit, now, critical: critical));
        if (critical) replies.AddRange(FirePassiveTriggers(PassiveTriggers.Kind.OnCrit, now, critical: true));
        return replies;
    }

    /// <summary>After the player killed an NPC: OnKill triggers and DecreaseCooltimeByKill rows.</summary>
    List<byte[]> AfterPlayerKill(Target victim, int skillId, long now)
    {
        if (!SkillEffectEngine.ClientTriggers) return [];
        var replies = FireClientTriggers(new TriggerEvent("OnKill", skillId, victim.Key), now);
        foreach (var buff in buffs.Where(b => b.ExpireAt > now).ToList())
            foreach (var (_, type, values) in AbnormalSpecs.Info(buff.AbnormalId)?.Effects ?? [])
            {
                // DecreaseCooltimeByKill [CurrentRatio, ratio /10000, skill, chance /10000, ...]: the skill's running cooldown shrinks by the ratio.
                if (type != "DecreaseCooltimeByKill" || values.Count < 3 || !int.TryParse(values[2], out var skill)) continue;
                var ratio = int.TryParse(values[1], out var rr) ? rr : 10_000;
                var chance = values.Count > 3 && int.TryParse(values[3], out var c) && c > 0 ? c : 10_000;
                if (chance < 10_000 && rng.Next(10_000) >= chance) continue;
                var group = data.CooldownGroup(skill);
                if (group <= 0 || !cooldowns.TryGetValue(group, out var readyAt) || readyAt <= now) continue;
                var cut = (int)Math.Min(int.MaxValue, (readyAt - now) * ratio / 10_000);
                replies.AddRange(AdjustCooldowns(-cut, group, now));
            }
        return replies;
    }

    /// <summary>After the player took damage: OnDamaged triggers (the attacker is the target side) and AbnormalClear HpDecreaseCount.</summary>
    List<byte[]> AfterPlayerDamagedByEngine(int damage, int attackerKey, long now)
    {
        if (!SkillEffectEngine.ClientTriggers || resources is not { Hp: > 0 }) return [];
        var replies = FireClientTriggers(new TriggerEvent("OnDamaged", lastNpcHitSkill, attackerKey, damage), now);
        var cleared = new List<Buff>();
        foreach (var buff in buffs.Where(b => b.ExpireAt > now && !b.PassiveHidden))
            foreach (var (_, type, values) in AbnormalSpecs.Info(buff.AbnormalId)?.Effects ?? [])
            {
                // AbnormalClear [HpDecreaseCount, hits, chance /10000, ...]: the abnormal ends after that many HP-losing hits.
                if (type != "AbnormalClear" || values.Count < 2 || values[0] != "HpDecreaseCount") continue;
                var hits = int.TryParse(values[1], out var h) && h > 0 ? h : 1;
                var chance = values.Count > 2 && int.TryParse(values[2], out var c) && c > 0 ? c : 10_000;
                if (++buff.ChargesUsed < hits) continue;
                if (chance < 10_000 && rng.Next(10_000) >= chance) { buff.ChargesUsed = 0; continue; }
                cleared.Add(buff);
            }
        if (cleared.Count > 0)
        {
            var speedBefore = BuffStat("MoveSpeed");
            replies.AddRange(RemoveBuffs(cleared.Distinct().ToList(), ExpireEtc));
            replies.AddRange(BuffStatsChanged(speedBefore));
        }
        return replies;
    }

    /// <summary>A buff just landed on the player: OnGetAbnormal triggers of the others.</summary>
    void AfterBuffAdded(Buff buff, long now, List<byte[]> replies)
    {
        if (buff.Casts.Any(c => c.IntervalMs <= 0))
        {
            CollectDueCasts(buff, now);
            RunDueCasts(now, replies);
        }
        if (!SkillEffectEngine.ClientTriggers || buff.PassiveHidden || triggerDepth > 0) return;
        replies.AddRange(FireClientTriggers(new TriggerEvent("OnGetAbnormal", buff.SkillId, buff.CasterKey ?? 0, AbnormalId: buff.AbnormalId), now));
    }

    /// <summary>A CastSkill row on a buff: the proc skill, its interval (0: once, when the buff lands), the next cast.</summary>
    sealed class BuffCast
    {
        public required int Skill;
        public required long IntervalMs;
        public long NextAt;
        public bool Done;
    }

    /// <summary>
    /// CastSkill [interval ms, skill, ...]: every interval while the abnormal lasts (first after one interval). An interval of
    /// 9999999 ms or more (10000000000 on 120 rows) casts once when it lands (CANDIDATE).
    /// </summary>
    static BuffCast BuffCastOf(int skill, string interval, long now)
    {
        var ms = long.TryParse(interval, out var v) ? v : 0;
        var once = ms <= 0 || ms >= 9_999_999;
        return new BuffCast { Skill = skill, IntervalMs = once ? 0 : ms, NextAt = once ? now : now + ms };
    }

    readonly List<(Buff Buff, BuffCast Cast)> dueCasts = [];

    void CollectDueCasts(Buff b, long now)
    {
        foreach (var c in b.Casts)
            if (!c.Done && now >= c.NextAt && c.NextAt <= b.ExpireAt && !dueCasts.Any(d => d.Cast == c)) dueCasts.Add((b, c));
    }

    /// <summary>Runs the CastSkill rows due now (after the buff loop: a proc may add or remove buffs).</summary>
    void RunDueCasts(long now, List<byte[]> replies)
    {
        if (dueCasts.Count == 0) return;
        var due = dueCasts.ToList();
        dueCasts.Clear();
        if (!SkillEffectEngine.ClientTriggers) return;
        foreach (var (buff, cast) in due)
        {
            if (cast.IntervalMs <= 0) cast.Done = true;
            else cast.NextAt += cast.IntervalMs * Math.Max(1, (now - cast.NextAt) / cast.IntervalMs + 1);
            if (!buffs.Contains(buff)) continue;
            var level = Math.Max(1, buff.Level);
            var rows = CastProcSkill(cast.Skill, level, selectedTarget, now, replies);
            PlaytestDebug.Action("skill_trigger", ("on", "CastSkill"), ("abnormalId", buff.AbnormalId), ("procSkill", cast.Skill),
                ("proc", SkillEffectEngine.SkillName(cast.Skill)), ("level", level), ("rows", rows));
        }
    }

    /// <summary>OnExpireAbnormal [abnormal, reason, ms]: an abnormal that ran out (reason TimeOut / None) puts the next one on.</summary>
    void AfterBuffsExpired(IReadOnlyList<Buff> expired, long now, List<byte[]> replies)
    {
        if (!SkillEffectEngine.ClientTriggers) return;
        foreach (var buff in expired)
            foreach (var (_, type, values) in AbnormalSpecs.Info(buff.AbnormalId)?.Effects ?? [])
            {
                if (type != "OnExpireAbnormal" || values.Count < 3 || !int.TryParse(values[0], out var next) || next <= 0) continue;
                if (!values[1].Equals("TimeOut", StringComparison.OrdinalIgnoreCase) && values[1] != "None") continue;
                if (!int.TryParse(values[2], out var ms) || ms <= 0) continue;
                var speedBefore = BuffStat("MoveSpeed");
                AddBuff(next, ms, buff.SkillId, now, replies, path: SelfAbnormalPath.Scripted);
                replies.AddRange(BuffStatsChanged(speedBefore));
            }
    }

    /// <summary>The learned passives' abnormals with server-side rows: Boxes' passives and the engine's (client Abnormal_Passive).</summary>
    IEnumerable<int> LearnedPassiveAbnormals(int skill) =>
        Boxes.Passives(skill).Concat(SkillEffectEngine.PassiveAbnormals(skill)).Distinct();

    // ---------- API for the boss mechanics engine and scripts ----------

    /// <summary>
    /// Puts an abnormal on the player through the engine's stacking (AbnormalEngine.Resolve) and effect rows. A monster caster
    /// (<paramref name="casterKey"/> not the player) makes it a debuff with immunities checked. The replies are the caller's to send.
    /// </summary>
    public List<byte[]> EngineApplyAbnormal(int abnormalId, int durationMs, int casterKey, int sourceSkill, long now)
    {
        if (resources is null || dead) return [];
        if (casterKey != 0 && casterKey != playerKey && AbnormalSpecs.Debuff(abnormalId))
        {
            lastNpcHitSkill = sourceSkill > 0 ? sourceSkill : lastNpcHitSkill;
            return TryApplyPlayerDebuff(abnormalId, durationMs, now, casterKey != 0 && targets?.TryGetValue(casterKey, out var c) == true ? c.Position : null, casterKey);
        }
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
        AddBuff(abnormalId, durationMs, sourceSkill, now, replies, path: SelfAbnormalPath.Scripted);
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>Runs a proc skill's rows (heals, abnormals, damage) as the player's at a level, against <paramref name="targetKey"/>.</summary>
    public List<byte[]> EngineCastProcSkill(int procSkill, int level, int targetKey, long now)
    {
        var replies = new List<byte[]>();
        CastProcSkill(procSkill, Math.Max(1, level), targetKey, now, replies);
        return replies;
    }

    internal List<byte[]> AfterCombatHitDealtForTests(int skillId, Target target, int damage, bool critical, long now) =>
        AfterCombatHitDealt(skillId, target, damage, critical, now);

    internal List<byte[]> AfterPlayerKillForTests(Target victim, int skillId, long now) => AfterPlayerKill(victim, skillId, now);

    internal List<byte[]> AfterPlayerDamagedForTests(int damage, int attackerKey, long now) => AfterPlayerDamaged(damage, now, attackerKey);

    internal List<byte[]> BuffTickForTests(long now) => BuffTick(now);

    internal void LearnForTests(int skillId) => learned.Add(skillId);

    internal void AddTargetForTests(Target target) => (targets ??= [])[target.Key] = target;

    internal void SetHpForTests(int hp, int hpMax)
    {
        if (resources is null) return;
        resources.HpMax = hpMax;
        resources.Hp = hp;
    }
}
