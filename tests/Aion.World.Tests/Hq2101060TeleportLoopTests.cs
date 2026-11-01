using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ2101060 step 2 (EnterVolumePC Trigger_HQ2101040_04) and DreamCrossroad_D_01_MapEvent: the ascension exit must fire once,
/// not on every return to map 200001 while AQ2602000 is Complete and the witch-cabin step is open (playtest loop to Region_03_2).
/// </summary>
public sealed class Hq2101060TeleportLoopTests
{
    const long Now = 1_760_100_000_000;
    const int DreamCrossroadMap = 200001;
    const int IshalgenMap = 1100;
    static readonly Vector3d DreamCrossroadExitSpawn = new(21274, -23850, 5064.9);
    static readonly Vector3d WitchCabinTriggerCenter = new(21910, -24430, 5022.2);

    static byte[] Loaded() => new PacketWriter().U16(0x3622).I32(1).ToArray();
    static byte[] Move(Vector3d at) => MapEventScenarioHelpers.MoveUpdate(at);

    static async Task<GameSession> OnIshalgenStep2Async(FakeCharacterStore store, long now)
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: 2, level: 6);
        await session.QuestCommandAsync("QuestAdd", "HQ2101060", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ2101060", default, stepOrder: 2);
        Assert.Equal(2, session.Quests!.Get("HQ2101060")!.Step);
        await session.QuestCommandAsync("QuestAdd", "AQ2602000", default);
        await session.QuestCommandAsync("QuestComplete", "AQ2602000", default);
        Assert.Equal(QuestLog.Complete, session.Quests!.Get("AQ2602000")!.Status);
        return session;
    }

    [Fact]
    public void EnterVolumePC_slack_zero_when_value_duplicates_map_id()
    {
        var objective = new QuestObjective("EnterVolumePC", Target: "Trigger_HQ2101040_04", Value: 1100, Count: 1, Map: 1100);
        Assert.Equal(0, QuestLog.EnterVolumeSlack(objective));
        Assert.Equal(500, QuestLog.EnterVolumeSlack(new QuestObjective("EnterVolumePC", Target: "Trig", Value: 500, Count: 1)));
    }

    [Fact]
    public async Task DreamCrossroad_field_exit_runs_once_then_allows_reentry_without_second_teleport()
    {
        var store = new FakeCharacterStore();
        var session = await OnIshalgenStep2Async(store, Now);
        session.Teleport(IshalgenMap, DreamCrossroadExitSpawn, Now + 100);
        await session.HandleAsync(Loaded(), Now + 110);

        Assert.NotEmpty(session.EnterDungeonById(DreamCrossroadMap, Now + 200));
        await session.HandleAsync(Loaded(), Now + 210);
        await session.TickAsync(Now + 220);
        Assert.Equal(DreamCrossroadMap, session.MapId);
        Assert.NotNull(session.MapEventRuns.FirstOrDefault(r =>
            r.Name.Equals("DreamCrossroad_D_01_MapEvent", StringComparison.OrdinalIgnoreCase)));

        await session.TickAsync(Now + 31_000);
        await session.HandleAsync(Loaded(), Now + 31_100);
        Assert.Equal(IshalgenMap, session.MapId);
        Assert.Equal(0, session.InstanceKey);

        var before = session.Position;
        Assert.NotEmpty(session.EnterDungeonById(DreamCrossroadMap, Now + 40_000));
        await session.HandleAsync(Loaded(), Now + 40_010);
        await session.TickAsync(Now + 40_020);
        Assert.Equal(DreamCrossroadMap, session.MapId);
        Assert.Null(session.MapEventRuns.FirstOrDefault(r =>
            r.Name.Equals("DreamCrossroad_D_01_MapEvent", StringComparison.OrdinalIgnoreCase)));

        session.Teleport(IshalgenMap, before ?? DreamCrossroadExitSpawn, Now + 50_000);
        await session.HandleAsync(Loaded(), Now + 50_010);
        var after = session.Position;
        Assert.True(after is null || before is null || after.Value.DistanceSquared(before.Value) < 400 * 400,
            "Re-entering Dream Crossroad after the one-shot exit must not PCTeleport the player back to Region_03_2");
    }

    [Fact]
    public async Task Ishalgen_exit_spawn_does_not_auto_complete_witch_cabin_volume()
    {
        var store = new FakeCharacterStore();
        var session = await OnIshalgenStep2Async(store, Now + 60_000);
        session.Teleport(IshalgenMap, DreamCrossroadExitSpawn, Now + 60_100);
        await session.HandleAsync(Move(DreamCrossroadExitSpawn), Now + 60_110);
        await session.TickAsync(Now + 60_120);

        var record = session.Quests!.Get("HQ2101060")!;
        Assert.Equal(QuestLog.Incomplete, record.Status);
        Assert.Equal(0, record.Goals[0]);

        await session.HandleAsync(Move(WitchCabinTriggerCenter), Now + 60_200);
        await session.TickAsync(Now + 60_210);
        record = session.Quests!.Get("HQ2101060")!;
        Assert.True(record.Goals[0] >= 1 || record.Status >= QuestLog.Complete,
            "Step 2 should complete only inside Trigger_HQ2101040_04, not at the Dream Crossroad exit artifact");
    }
}
