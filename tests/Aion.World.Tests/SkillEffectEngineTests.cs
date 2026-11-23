using Aion.World;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The skill effect engine (docs/SKILL-EFFECT-ENGINE.md): data, stacking, triggers, drains, life cycle, NPC ratio damage.</summary>
[Collection("SkillEffectEngineFlags")]
public sealed class SkillEffectEngineTests
{
    const long Now = 7_000_000;
    const int AbsorbEssence = 15760000, AbsorbEssenceAbnormal = 157600001, AbsorbEssenceProc = 15760007;
    const int FlameArrow = 15210000, FlameArrowDrainVariant = 15210020;

    static Target Npc(int key = 9100, string subType = "NormalMonster") =>
        new() { Key = key, Name = "Dummy", HpMax = 100_000, Hp = 100_000, Level = 10, SubType = subType, Position = new Vector3d(100, 0, 0) };

    static async Task<(GameSession Session, Target Npc)> SessionWithNpcAsync(int level = 30)
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now, level: level);
        var npc = Npc();
        session.AddTargetForTests(npc);
        return (session, npc);
    }

    // ---------- data ----------

    [Fact]
    public void Generated_data_loads_with_every_section()
    {
        Assert.NotEmpty(SkillEffectEngine.SkillEffectTypeCounts);
        Assert.NotEmpty(SkillEffectEngine.AbnormalEffectTypeCounts);
        Assert.True(SkillEffectEngine.HasTriggers(AbsorbEssenceAbnormal));
        Assert.NotNull(SkillEffectEngine.Proc(AbsorbEssenceProc));
        Assert.NotNull(SkillEffectEngine.DrainOf(FlameArrowDrainVariant));
        Assert.NotNull(SkillEffectEngine.RatioOf(1800655));
        Assert.Contains(AbsorbEssenceAbnormal, SkillEffectEngine.PassiveAbnormals(AbsorbEssence));
    }

    [Fact]
    public void Effect_values_follow_skill_effect_level_rows()
    {
        var row = SkillEffectEngine.Proc(AbsorbEssenceProc)!.Rows.Single(r => r.Type == "MpHeal");
        Assert.Equal("25", SkillEffectEngine.EffectValuesAt(row.LevelGroup, 1, row.Values)[0]);
        Assert.Equal("30", SkillEffectEngine.EffectValuesAt(row.LevelGroup, 2, row.Values)[0]);
        Assert.Equal("35", SkillEffectEngine.EffectValuesAt(row.LevelGroup, 3, row.Values)[0]);
    }

    [Fact]
    public void Absorb_essence_trigger_is_on_hit_with_one_second_cooldown()
    {
        var spec = SkillEffectEngine.Triggers(AbsorbEssenceAbnormal).First(t => !t.Variant);
        Assert.Equal("OnHit", spec.On);
        Assert.Equal(AbsorbEssenceProc, spec.ProcSkill);
        Assert.Equal(10_000, spec.Chance);
        Assert.Equal(1000, spec.CooldownMs);
        Assert.Equal(AbsorbEssence, spec.Skill);
    }

    // ---------- registry ----------

    [Fact]
    public void Registry_names_a_handler_for_every_damage_heal_and_trigger_type()
    {
        foreach (var type in new[] { "Damage", "HpHeal", "MpHeal", "MpHealRatio", "APHeal", "MPBurnRatio", "Abnormal_True", "Dispel_True", "DecreaseCooltime" })
            Assert.True(SkillEffectRegistry.IsHandledSkillEffect(type), type);
        foreach (var type in new[] { "TriggerSkillByAttackAll", "TriggerSkillByGetDamage", "TriggerSkillByKill", "HpBarrier", "Stun", "Dot_TargetMaxHP", "Mot", "OnExpireAbnormal" })
            Assert.True(SkillEffectRegistry.IsHandledAbnormalEffect(type), type);
    }

    [Fact]
    public void Registry_types_are_client_types_or_documented_server_types()
    {
        var client = SkillEffectEngine.SkillEffectTypeCounts.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notInClient = SkillEffectRegistry.SkillEffectEntries.Where(e => !client.Contains(e.Type)).Select(e => e.Type).ToList();
        Assert.True(notInClient.Count <= 3, string.Join(", ", notInClient));
    }

    [Fact]
    public void Unknown_skill_effect_type_is_reported_once()
    {
        SkillEffectRegistry.ResetLogForTests();
        Assert.False(SkillEffectRegistry.NoteSkillEffect(1, "Rebirth_TestOnly"));
        Assert.False(SkillEffectRegistry.NoteSkillEffect(2, "Rebirth_TestOnly"));
        Assert.True(SkillEffectRegistry.NoteSkillEffect(1, "Damage"));
    }

    // ---------- stacking (AbnormalEngine.Resolve) ----------

    static AbnormalStackRules.Rule Rule(string time, string replace, int count = 1) => new(500, count, time, replace);

    [Fact]
    public void Resolve_same_id_renews_and_longer_keeps_the_later_expiry()
    {
        var active = new[] { new AbnormalEngine.Active(1, 10, 500, 1, 2000) };
        var renew = AbnormalEngine.Resolve(active, 10, 1, 5000, Rule("Renew", "Replace"));
        Assert.Equal(AbnormalEngine.Outcome.Refresh, renew.Outcome);
        Assert.False(renew.KeepLonger);
        Assert.True(AbnormalEngine.Resolve(active, 10, 1, 5000, Rule("Longer", "Replace")).KeepLonger);
    }

    [Fact]
    public void Resolve_separate_stacks_up_to_the_overlap_count()
    {
        var one = new[] { new AbnormalEngine.Active(1, 10, 500, 1, 2000) };
        Assert.Equal(AbnormalEngine.Outcome.Add, AbnormalEngine.Resolve(one, 10, 1, 5000, Rule("Separate", "Replace", 2)).Outcome);
        var two = new[] { one[0], new AbnormalEngine.Active(2, 10, 500, 1, 4000) };
        var full = AbnormalEngine.Resolve(two, 10, 1, 5000, Rule("Separate", "Replace", 2));
        Assert.Equal(AbnormalEngine.Outcome.Refresh, full.Outcome);
        Assert.Equal(1, full.RefreshUid);
    }

    [Fact]
    public void Resolve_group_replace_priority_and_time()
    {
        var other = new[] { new AbnormalEngine.Active(7, 11, 500, 3, 9000) };
        var replace = AbnormalEngine.Resolve(other, 12, 1, 5000, Rule("Renew", "Replace"));
        Assert.Equal(AbnormalEngine.Outcome.Add, replace.Outcome);
        Assert.Equal([7], replace.Remove);
        Assert.Equal(AbnormalEngine.Outcome.Reject, AbnormalEngine.Resolve(other, 12, 1, 5000, Rule("Renew", "Priority")).Outcome);
        Assert.Equal(AbnormalEngine.Outcome.Add, AbnormalEngine.Resolve(other, 12, 3, 5000, Rule("Renew", "PriorityReplace")).Outcome);
        Assert.Equal(AbnormalEngine.Outcome.Reject, AbnormalEngine.Resolve(other, 12, 1, 5000, Rule("Renew", "Time")).Outcome);
        Assert.Equal(AbnormalEngine.Outcome.Add, AbnormalEngine.Resolve(other, 12, 1, 10_000, Rule("Renew", "Time")).Outcome);
    }

    // ---------- Sorcerer: MP restore on hit (regression) ----------

    [Fact]
    public async Task Sorcerer_absorb_essence_restores_mp_on_hit_with_cooldown()
    {
        var (session, npc) = await SessionWithNpcAsync();
        session.LearnForTests(AbsorbEssence);
        session.RefreshPassiveStatBuffs(Now);
        session.SetResource('M', 0);

        session.AfterCombatHitDealtForTests(FlameArrow, npc, 500, critical: false, Now + 100);
        Assert.Equal(25, session.Resource.Mp);

        session.AfterCombatHitDealtForTests(FlameArrow, npc, 500, critical: true, Now + 600);
        Assert.Equal(25, session.Resource.Mp);   // internal cooldown 1000 ms

        session.AfterCombatHitDealtForTests(FlameArrow, npc, 500, critical: true, Now + 1200);
        Assert.Equal(50, session.Resource.Mp);
    }

    [Fact]
    public async Task Client_triggers_flag_off_leaves_mp_untouched()
    {
        var (session, npc) = await SessionWithNpcAsync();
        session.LearnForTests(AbsorbEssence);
        session.RefreshPassiveStatBuffs(Now);
        session.SetResource('M', 0);
        SkillEffectEngine.ClientTriggers = false;
        try
        {
            session.AfterCombatHitDealtForTests(FlameArrow, npc, 500, critical: false, Now + 100);
            Assert.Equal(0, session.Resource.Mp);
        }
        finally { SkillEffectEngine.ClientTriggers = true; }
    }

    [Fact]
    public async Task Unlearned_passive_does_not_proc()
    {
        var (session, npc) = await SessionWithNpcAsync();
        session.RefreshPassiveStatBuffs(Now);
        session.SetResource('M', 0);
        session.AfterCombatHitDealtForTests(FlameArrow, npc, 500, critical: false, Now + 100);
        Assert.Equal(0, session.Resource.Mp);
    }

    // ---------- passive on damaged ----------

    [Fact]
    public async Task Blood_absorption_heals_only_below_half_hp_once_per_minute()
    {
        var (session, npc) = await SessionWithNpcAsync();
        session.LearnForTests(11730000);
        session.RefreshPassiveStatBuffs(Now);
        session.SetHpForTests(9000, 10_000);
        session.AfterPlayerDamagedForTests(100, npc.Key, Now + 100);
        Assert.Equal(9000, session.Resource.Hp);   // above half: the minute cooldown is kept

        session.SetHpForTests(4000, 10_000);
        session.AfterPlayerDamagedForTests(100, npc.Key, Now + 200);
        Assert.Equal(6600, session.Resource.Hp);   // + 26% of max

        session.SetHpForTests(4000, 10_000);
        session.AfterPlayerDamagedForTests(100, npc.Key, Now + 30_000);
        Assert.Equal(4000, session.Resource.Hp);
    }

    // ---------- life steal ----------

    [Fact]
    public async Task Flame_arrow_drain_variant_heals_one_percent_of_damage_capped_at_three_percent_max_hp()
    {
        var (session, npc) = await SessionWithNpcAsync();
        session.SetHpForTests(1000, 10_000);
        session.AfterCombatHitDealtForTests(FlameArrowDrainVariant, npc, 20_000, critical: false, Now + 100);
        Assert.Equal(1200, session.Resource.Hp);

        session.AfterCombatHitDealtForTests(FlameArrowDrainVariant, npc, 20_000, critical: false, Now + 200);
        Assert.Equal(1200, session.Resource.Hp);   // once per cast

        session.AfterCombatHitDealtForTests(FlameArrowDrainVariant, npc, 1_000_000, critical: false, Now + 2000);
        Assert.Equal(1500, session.Resource.Hp);   // cap 3% of 10000
    }

    [Fact]
    public void Drain_amount_respects_hit_results()
    {
        var drain = new SkillEffectEngine.Drain("DrainByDamageAndCasterMaxHPOnce", 100, 100, 300, ["Critical"], 1);
        Assert.Equal(0, SkillEffectEngine.DrainAmount(drain, 10_000, 10_000, critical: false, new Random(1)));
        Assert.Equal(100, SkillEffectEngine.DrainAmount(drain, 10_000, 10_000, critical: true, new Random(1)));
    }

    // ---------- DoT, stun, shield, expiry chain ----------

    [Fact]
    public async Task Dot_target_max_hp_ticks_on_the_player()
    {
        var (session, npc) = await SessionWithNpcAsync();
        session.TryApplyPlayerDebuff(12000473, 5000, Now, casterKey: npc.Key);
        Assert.Contains(12000473, session.ActiveBuffs);
        var max = session.Resource.HpMax;
        session.SetResource('H', max);
        session.BuffTickForTests(Now + 1000);
        Assert.InRange(session.Resource.Hp, max * 9 / 10, max - 1);
    }

    [Fact]
    public async Task Stun_holds_the_player_and_ends_on_time()
    {
        var (session, npc) = await SessionWithNpcAsync();
        session.EngineApplyAbnormal(12000001, 2000, npc.Key, 0, Now);
        Assert.True(session.HeldByAbnormal);
        session.BuffTickForTests(Now + 2100);
        Assert.False(session.HeldByAbnormal);
    }

    [Fact]
    public async Task Hp_barrier_absorbs_npc_damage()
    {
        var (session, npc) = await SessionWithNpcAsync();
        session.EngineApplyAbnormal(12167801, 10_000, 0, 0, Now);
        Assert.Contains(12167801, session.ActiveBuffs);
        var max = session.Resource.HpMax;
        session.SetResource('H', max);
        session.NpcHitsPlayer(300, Now + 100, killerKey: npc.Key);
        Assert.Equal(max, session.Resource.Hp);   // barrier 500
        session.NpcHitsPlayer(300, Now + 200, killerKey: npc.Key);
        Assert.Equal(max - 100, session.Resource.Hp);
    }

    [Fact]
    public async Task On_expire_abnormal_puts_the_follow_up_on()
    {
        var (session, _) = await SessionWithNpcAsync();
        session.EngineApplyAbnormal(16032801, 1000, 0, 0, Now);
        Assert.Contains(16032801, session.ActiveBuffs);
        session.BuffTickForTests(Now + 1100);
        Assert.DoesNotContain(16032801, session.ActiveBuffs);
        Assert.Contains(12000001, session.ActiveBuffs);
    }

    [Fact]
    public async Task Cast_skill_row_casts_its_proc_every_interval()
    {
        var (session, _) = await SessionWithNpcAsync();
        session.EngineApplyAbnormal(171600001, 5000, 0, 0, Now);
        Assert.Contains(171600001, session.ActiveBuffs);
        session.SetResource('H', 10);
        session.BuffTickForTests(Now + 1000);
        Assert.Equal(10, session.Resource.Hp);
        session.BuffTickForTests(Now + 2000);
        Assert.InRange(session.Resource.Hp, 100, 110);
        session.BuffTickForTests(Now + 2500);
        Assert.InRange(session.Resource.Hp, 100, 110);
    }

    [Fact]
    public async Task Ap_heal_client_spelling_restores_ap()
    {
        var (session, _) = await SessionWithNpcAsync();
        session.SetApForTests(0, 1000);
        session.ReceiveFriendlyRow(["APHeal", "40", "40"], Now);
        Assert.Equal(40, session.ApForTests);
    }

    // ---------- NPC ----------

    [Fact]
    public void Bakarma_ratio_skill_hits_for_five_times_max_hp()
    {
        Assert.Equal(50_000, SkillEffectEngine.RatioDamageAgainst(1800655, 3000, 10_000));
        Assert.Null(SkillEffectEngine.RatioDamageAgainst(1800655, 0, 0));
        Assert.Null(SkillEffectEngine.RatioDamageAgainst(1, 3000, 10_000));
    }

    [Fact]
    public void Npc_ai_ratio_damage_reads_player_hp()
    {
        var ai = new NpcAi(new Random(1)) { PlayerHpOf = _ => (2000, 8000) };
        Assert.Equal(40_000, ai.RatioDamage(1800655, 1, victimIsNpc: false));
        Assert.Null(ai.RatioDamage(9_999_999, 1, victimIsNpc: false));
    }

    [Fact]
    public void Npc_ratio_damage_flag_off_falls_back()
    {
        SkillEffectEngine.NpcRatioDamage = false;
        try { Assert.Null(SkillEffectEngine.RatioDamageAgainst(1800655, 3000, 10_000)); }
        finally { SkillEffectEngine.NpcRatioDamage = true; }
    }

    [Fact]
    public void Npc_skills_missing_from_the_ported_table_use_engine_rows()
    {
        var rows = PortedSkillAbnormals.NpcRows(15090000);
        Assert.Contains(rows, r => r.Abnormal == 150900001);
    }

    // ---------- legacy stacking path ----------

    [Fact]
    public async Task Legacy_stacking_flag_still_replaces_same_group()
    {
        SkillEffectEngine.AbnormalStacking = false;
        try
        {
            var (session, npc) = await SessionWithNpcAsync();
            session.EngineApplyAbnormal(12000001, 2000, npc.Key, 0, Now);
            session.EngineApplyAbnormal(12000001, 4000, npc.Key, 0, Now + 100);
            Assert.Single(session.ActiveBuffs, id => id == 12000001);
        }
        finally { SkillEffectEngine.AbnormalStacking = true; }
    }
}

[CollectionDefinition("SkillEffectEngineFlags", DisableParallelization = true)]
public sealed class SkillEffectEngineFlagsCollection;
