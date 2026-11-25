using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Target Stagger (groggy break): official unlock is the boss Stagger debuff abnormal (no 0x5608);
/// scattershot/rampage skills stay castable for the ~5 s window.
/// </summary>
public sealed class StaggerReactiveSkillContextTests
{
    const long Now = 1_760_300_000_000;
    const int SwordAuraRampage = 11_280_000;
    const int RapidScattershot = 16_340_000;
    const int JointstrikeCurse = 16_140_000;
    const int Dummy = Combat.TrainingDummyKey;

    static byte[] Cast(int skill, int target = Dummy) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<GameSession> EnterGladiatorAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("StaggerRamp").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.NoCool = true;
        return session;
    }

    static async Task<GameSession> EnterElementalistAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(9)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("StaggerScatter").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.NoCool = true;
        return session;
    }

    static bool Accepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static int OpIndex(List<byte[]> replies, ushort op) =>
        replies.FindIndex(p => GameSessionTestHelpers.Op(p) == op);

    [Fact]
    public void Stagger_window_uses_official_five_second_duration_from_data()
    {
        Assert.Equal(5000, StaggerGaugeCombat.StaggerDurationMs);
        var skill = GameSessionTestHelpers.LiveGameData.Skill(SwordAuraRampage)!;
        Assert.Equal("Stagger", skill.SkillCond!.AbnormalEffect);
    }

    [Fact]
    public void Gladiator_rampage_stack_lists_stagger_skill_from_table_export()
    {
        var ctx = GameData.Embedded.DefaultContextSkills(SwordAuraRampage);
        Assert.Equal(SwordAuraRampage, ctx[0]);
    }

    [Fact]
    public void Elementalist_scattershot_stack_lists_stagger_skill_from_table_export()
    {
        var ctx = GameData.Embedded.DefaultContextSkills(RapidScattershot);
        Assert.Equal(RapidScattershot, ctx[0]);
    }

    [Fact]
    public async Task Stagger_break_followups_fire_after_ninety_eight_ms()
    {
        var session = await EnterGladiatorAsync();
        var boss = session.Targets![Dummy];
        var t = Now + 60_000;
        session.BreakNpcStaggerForTests(boss, t, JointstrikeCurse);
        var tick = await session.TickAsync(t + StaggerGaugeCombat.StaggerBreakFollowupDelayMs);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x382a);
    }

    [Fact]
    public async Task Npc_stagger_break_sends_skill_sourced_abnormal_without_5608()
    {
        var session = await EnterGladiatorAsync();
        var boss = session.Targets![Dummy];
        var replies = session.BreakNpcStaggerForTests(boss, Now + 60_000, JointstrikeCurse);
        Assert.True(OpIndex(replies, 0x382a) >= 0);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == GameSession.QuickSlotContextSkillRs);
        var abn = replies.First(p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Contains(BitConverter.GetBytes(161_400_001), abn.AsSpan());
    }

    [Fact]
    public async Task Gladiator_rampage_can_be_spammed_while_target_stagger_lasts()
    {
        var session = await EnterGladiatorAsync();
        var boss = session.Targets![Dummy];
        var t = Now + 60_000;
        session.BreakNpcStaggerForTests(boss, t);
        Assert.True(Accepted(await session.HandleAsync(Cast(SwordAuraRampage, boss.Key), t + 200)));
        Assert.True(Accepted(await session.HandleAsync(Cast(SwordAuraRampage, boss.Key), t + 800)));
        Assert.True(Accepted(await session.HandleAsync(Cast(SwordAuraRampage, boss.Key), t + 2_000)));
        Assert.True(boss.StaggerUntil > t + 2_000);
    }

    [Fact]
    public async Task Elementalist_scattershot_can_be_spammed_while_target_stagger_lasts()
    {
        var session = await EnterElementalistAsync();
        var boss = session.Targets![Dummy];
        var t = Now + 60_000;
        session.BreakNpcStaggerForTests(boss, t);
        Assert.True(Accepted(await session.HandleAsync(Cast(RapidScattershot, boss.Key), t + 200)));
        Assert.True(Accepted(await session.HandleAsync(Cast(RapidScattershot, boss.Key), t + 900)));
        Assert.True(Accepted(await session.HandleAsync(Cast(RapidScattershot, boss.Key), t + 1_800)));
    }

    [Fact]
    public async Task Elementalist_scattershot_spam_ignores_swing_lockout_during_stagger_without_no_cool()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(9)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("StaggerScatter").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var boss = session.Targets![Dummy];
        var t = Now + 60_000;
        session.BreakNpcStaggerForTests(boss, t, 16_140_000);
        for (var i = 0; i < 5; i++)
        {
            var at = t + 200 + i * 300;
            Assert.True(Accepted(await session.HandleAsync(Cast(RapidScattershot, boss.Key), at)),
                $"16340000 should accept during stagger at +{at - t} ms (capture 233052 ~620 ms spam, no GCD block)");
        }
    }

    [Fact]
    public async Task Stagger_reactive_skill_refused_after_official_window()
    {
        var session = await EnterGladiatorAsync();
        var boss = session.Targets![Dummy];
        var t = Now + 60_000;
        session.BreakNpcStaggerForTests(boss, t);
        Assert.True(Accepted(await session.HandleAsync(Cast(SwordAuraRampage, boss.Key), t + 100)));
        var afterWindow = t + StaggerGaugeCombat.StaggerDurationMs + 200;
        await session.TickAsync(afterWindow);
        Assert.False(Accepted(await session.HandleAsync(Cast(SwordAuraRampage, boss.Key), afterWindow + 50)));
    }

    [Fact]
    public async Task Lake_guardian_gauge_break_unlocks_stagger_reactive_skill_on_wire()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await EnterGladiatorAsync();
        session.Teleport(210016, new Vector3d(7309.9, -6974.4, 84.0), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 15);
        var boss = session.Targets!.Values.Single(t => t.NpcId == 2701034);
        boss.Immune = false;
        boss.Relationship = 6;
        if (boss.Dead) boss.Hp = boss.HpMax;
        StaggerGaugeCombat.InitBossGauge(boss);
        const int hit = 100;
        var gauge = StaggerGaugeCombat.GaugeDamage(data, 11010000, boss, hit);
        var castAt = Now + 20_000;
        StaggerGaugeCombat.AddGaugeDamage(boss, gauge, session.PlayerKey, castAt, session, true, breakingSkillId: 11010000);
        var remaining = StaggerGaugeCombat.BossCaptureGroggyMax - boss.StaggerGauge;
        var breakReplies = StaggerGaugeCombat.AddGaugeDamage(boss, remaining, session.PlayerKey, castAt + 500, session, true, breakingSkillId: 11010000);
        Assert.DoesNotContain(breakReplies, p => GameSessionTestHelpers.Op(p) == GameSession.QuickSlotContextSkillRs);
        Assert.Contains(breakReplies, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Contains(breakReplies, p => p.SequenceEqual(GamePackets.UpdateGroggyInfoBreak(boss.Key)));
        Assert.True(Accepted(await session.HandleAsync(Cast(SwordAuraRampage, boss.Key), castAt + 1000)));
    }

    [Fact]
    public async Task Gladiator_chain_skills_accept_on_lake_guardian_after_priming_chain()
    {
        var session = await EnterGladiatorAsync();
        session.Teleport(210016, new Vector3d(7309.9, -6974.4, 84.0), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 15);
        var boss = session.Targets!.Values.Single(t => t.NpcId == 2701034);
        boss.Immune = false;
        boss.Relationship = 6;
        if (boss.Dead) boss.Hp = boss.HpMax;
        session.NoCool = true;
        var t = Now + 20_000;
        Assert.True(Accepted(await session.HandleAsync(Cast(11_020_000, boss.Key), t)));
        Assert.True(Accepted(await session.HandleAsync(Cast(11_030_000, boss.Key), t + 400)));
        Assert.True(Accepted(await session.HandleAsync(Cast(11_040_000, boss.Key), t + 800)));
    }
}
