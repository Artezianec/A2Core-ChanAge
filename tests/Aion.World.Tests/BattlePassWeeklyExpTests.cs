using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

public sealed class BattlePassWeeklyExpTests
{
    const long LiveNow = 1792000000000L;   // 2026-10-14, Light global pass 10044 active
    const int GlobalPass = 10044;
    const int FreeExp = 810433000;

    static async Task<(GameSession Session, FakeCharacterStore Store, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, long now,
        bool enforceCap = false)
    {
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, EnforceBattlePassWeeklyCap = enforceCap },
            new Random(5))
        { Store = store };
        await session.HandleAsync(Login(), now);
        await session.HandleAsync(LightCreate(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return (session, store, entry);
    }

    static int PeriodExp(byte[] packet, int groupId)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        for (var n = r.Varint(); n > 0; n--)
        {
            var id = r.I32();
            r.I32();
            var period = r.I32();
            r.I64(); r.I64();
            for (var c = r.Varint(); c > 0; c--) r.I32();
            for (var c = r.Varint(); c > 0; c--) { r.I32(); r.U8(); }
            r.Varint(); r.Bool(); r.I32(); r.Bool();
            if (id == groupId) return period;
        }
        throw new Xunit.Sdk.XunitException($"Group {groupId} not in packet");
    }

    [Fact]
    public async Task Weekly_exp_round_trips_across_sessions()
    {
        var store = new FakeCharacterStore();
        var (session, _, entry) = await EnterAsync(store, LiveNow);
        var baseline = PeriodExp(entry.Last(p => Op(p) == GameSession.BattlePassInfoListNt), GlobalPass);
        session.GrantBattlePassExp(2500, LiveNow);
        await session.FlushDeferredPersistenceAsync(LiveNow + 10, default);
        Assert.Equal(baseline + 2500, store.BattlePassWeekly.Single(r => r.GroupId == GlobalPass).Exp);

        var (session2, _, entry2) = await EnterAsync(store, LiveNow + 60_000);
        Assert.Equal(baseline + 2500, PeriodExp(entry2.Last(p => Op(p) == GameSession.BattlePassInfoListNt), GlobalPass));
        Assert.Equal(baseline + 2500, session2.AchievementCount(FreeExp));
    }

    [Fact]
    public void Stale_week_row_shows_zero_until_new_accrual()
    {
        var session = Session(new FakeCharacterStore());
        var oldWeek = ContentResetSchedule.WeeklyStartMs(LiveNow) - 7 * 86_400_000L;
        session.SeedBattlePassWeeklyForTests(GlobalPass, oldWeek, 4200);
        Assert.Equal(0, session.BattlePassWeeklyExpForTests(GlobalPass, LiveNow));
        _ = session.GrantBattlePassExp(90, LiveNow);
        Assert.Equal(90, session.BattlePassWeeklyExpForTests(GlobalPass, LiveNow));
    }

    [Fact]
    public async Task Online_weekly_reset_zeros_period_exp_without_touching_level_exp()
    {
        var store = new FakeCharacterStore();
        var weekStart = ContentResetSchedule.WeeklyStartMs(LiveNow);
        var (session, _, entry) = await EnterAsync(store, LiveNow);
        var baselineTotal = session.AchievementCount(FreeExp);
        var baselineWeekly = PeriodExp(entry.Last(p => Op(p) == GameSession.BattlePassInfoListNt), GlobalPass);
        session.GrantBattlePassExp(1800, LiveNow);
        Assert.Equal(baselineTotal + 1800, session.AchievementCount(FreeExp));

        var nextWeek = weekStart + 7 * 86_400_000L + 1;
        var resets = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), nextWeek);
        var passList = resets.Last(p => Op(p) == GameSession.BattlePassInfoListNt);
        Assert.Equal(0, PeriodExp(passList, GlobalPass));
        Assert.Equal(baselineTotal + 1800, session.AchievementCount(FreeExp));
        Assert.Equal(0, session.BattlePassWeeklyExpForTests(GlobalPass, nextWeek));
    }

    [Fact]
    public async Task Weekly_cap_clamps_mission_grants_when_enforced()
    {
        var store = new FakeCharacterStore();
        var group = AchievementTables.Embedded.PassGroups.First(g => g.Id == GlobalPass);
        var mission = AchievementTables.Embedded.Of(910430700)!;
        var stepExp = mission.Points(1);

        var (session, _, _) = await EnterAsync(store, LiveNow, enforceCap: true);
        session.SeedBattlePassWeeklyForTests(GlobalPass, ContentResetSchedule.WeeklyStartMs(LiveNow), group.Limit - 100);
        session.SeedAchievementForTests(910430700, mission.StepCount(1) - 1, LiveNow);
        var before = session.AchievementCount(FreeExp);
        session.AchieveForTests("KillNpcAll", 1, now: LiveNow);
        Assert.Equal(before + Math.Min(stepExp, 100), session.AchievementCount(FreeExp));
    }

    [Fact]
    public async Task Gm_grant_bypasses_weekly_cap()
    {
        var store = new FakeCharacterStore();
        var group = AchievementTables.Embedded.PassGroups.First(g => g.Id == GlobalPass);
        store.BattlePassWeekly.Add(new BattlePassWeeklyRecord(GlobalPass, ContentResetSchedule.WeeklyStartMs(LiveNow), group.Limit));

        var (session, _, _) = await EnterAsync(store, LiveNow, enforceCap: true);
        var before = session.AchievementCount(FreeExp);
        session.GrantBattlePassExp(50, LiveNow);
        Assert.Equal(before + 50, session.AchievementCount(FreeExp));
    }

    [Fact]
    public async Task Failed_flush_remarks_weekly_dirty()
    {
        var store = new FakeCharacterStore { BattlePassWeeklySaveFails = true };
        var (session, _, entry) = await EnterAsync(store, LiveNow);
        var baselineWeekly = PeriodExp(entry.Last(p => Op(p) == GameSession.BattlePassInfoListNt), GlobalPass);
        session.GrantBattlePassExp(10, LiveNow);
        await session.FlushDeferredPersistenceAsync(LiveNow + 10, default);
        Assert.Equal(1, store.BattlePassWeeklySaves);
        Assert.Empty(store.BattlePassWeekly);
        store.BattlePassWeeklySaveFails = false;
        await session.FlushDeferredPersistenceAsync(LiveNow + 20, default);
        Assert.Equal(2, store.BattlePassWeeklySaves);
        Assert.Equal(baselineWeekly + 10, store.BattlePassWeekly.Single(r => r.GroupId == GlobalPass).Exp);
    }

    [Fact]
    public async Task Fresh_fake_store_weekly_load_is_empty_without_throwing()
    {
        var store = new FakeCharacterStore();
        Assert.Empty(await store.BattlePassWeeklyAsync(Guid.NewGuid(), default));
        Assert.True(await store.SaveBattlePassWeeklyAsync(Guid.NewGuid(), [], default));
    }
}