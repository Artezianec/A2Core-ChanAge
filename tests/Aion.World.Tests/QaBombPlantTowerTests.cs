using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBombPlantTowerTests
{
    const long Now = 1_760_950_000_000;

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();
    static byte[] GroundReady(int skill) => new PacketWriter().U16(GameSession.SkillGroundReadyRq).I32(skill).ToArray();
    static byte[] GroundStart(int skill) => new PacketWriter().U16(GameSession.SkillGroundStartRq).I32(skill).ToArray();

    [Fact]
    public async Task EnvObjBombPlantable_false_after_successful_plant()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        var objects = WorldObjects.ObjectsOn(100002);
        var tower = objects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400824);
        var box = objects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400823);
        var t = Now + 20_000;
        await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, t);
        await session.HandleAsync(Use(WorldObjects.FirstKey + box.i), t + 100);
        await session.TickAsync(t + 100 + GameSession.CarryPickupMs);
        Assert.True(session.EnvObjBombPlantable(tower.i, tower.o, t + 200, tower.o.At));
        await MapEventScenarioHelpers.SyncPositionAsync(session, tower.o.At, t + 500);
        await session.HandleAsync(GroundStart(5003), t + 600);
        await session.TickAsync(t + 2000);
        Assert.False(session.EnvObjBombPlantable(tower.i, tower.o, t + 2000, tower.o.At));
        Assert.Equal("not_carrying", session.BombPlantRefusalReason(t + 2000, 5003));
    }

    [Fact]
    public async Task A_stale_selected_tower_does_not_refuse_the_tower_in_reach()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);

        var objects = WorldObjects.ObjectsOn(100002);
        var towers = objects.Select((o, i) => (o, i)).Where(x => x.o.Env == 1400824).ToList();
        var box = objects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400823);
        var tower = towers[0];
        var farTower = towers[1];
        var t = Now + 20_000;
        await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, t);
        await session.HandleAsync(Use(WorldObjects.FirstKey + box.i), t + 100);
        await session.TickAsync(t + 100 + GameSession.CarryPickupMs);
        await MapEventScenarioHelpers.SyncPositionAsync(session, tower.o.At, t + 500);
        // The client still has another tower as its main target; planting must use the one under the player.
        await session.HandleAsync(new PacketWriter().U16(0x3833).U8(0).Varint((uint)(WorldObjects.FirstKey + farTower.i)).ToArray(), t + 550);
        Assert.Equal("ok", session.BombPlantRefusalReason(t + 600, 5003));
        var used = await session.HandleAsync(GroundStart(5003), t + 600);
        Assert.True(used.Any(p => p.Length > 2 && p[0] == 0x35 && p[1] == 0x36
            && new PacketReader(p) { Offset = 2 }.Varint() == WorldObjects.FirstKey + tower.i), "the tower in reach is destroyed");
        Assert.Contains(used, p => p.Length > 8 && BitConverter.ToUInt16(p, 0) == 0x3802 && p.AsSpan().IndexOf(BitConverter.GetBytes(5003)) >= 0);
        Assert.Contains(used, p => p.Length > 8 && BitConverter.ToUInt16(p, 0) == 0x3851 && p.AsSpan().IndexOf(BitConverter.GetBytes(5003)) >= 0);
        var ended = await session.TickAsync(t + 2_000);
        Assert.Contains(ended, p => p.Length > 8 && BitConverter.ToUInt16(p, 0) == 0x3806 && p.AsSpan().IndexOf(BitConverter.GetBytes(5003)) >= 0);
    }

    [Fact]
    public async Task Far_ground_ready_refuses_out_of_range_without_crediting_tower()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        var objects = WorldObjects.ObjectsOn(100002);
        var tower = objects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400824);
        var box = objects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400823);
        var t = Now + 20_000;
        await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, t);
        await session.HandleAsync(Use(WorldObjects.FirstKey + box.i), t + 100);
        await session.TickAsync(t + 100 + GameSession.CarryPickupMs);
        var far = tower.o.At with { X = tower.o.At.X + GameSession.BombPlantReach(tower.o) + 500 };
        await MapEventScenarioHelpers.SyncPositionAsync(session, far, t + 500);
        // Ready from anywhere enters the aim and must not plant or consume the bomb.
        var aim = await session.HandleAsync(GroundReady(5003), t + 550);
        Assert.Contains(aim, p => p.Length == 8 && BitConverter.ToUInt16(p, 0) == GameSession.SkillGroundReadyRs
            && BitConverter.ToUInt16(p, 2) == 0 && BitConverter.ToInt32(p, 4) == 5003);
        Assert.Contains(aim, p => p.Length >= 8 && BitConverter.ToUInt16(p, 0) == 0x384c
            && p[^1] == 1 && BitConverter.ToInt32(p, p.Length - 5) == 5003);
        Assert.True(session.EnvObjBombTowerRemaining(tower.i, tower.o, t + 555));
        Assert.Equal(1196, session.CarriedGroup);
        Assert.Equal("out_of_range", session.BombPlantRefusalReason(t + 600, 5003));
        await session.HandleAsync(GroundStart(5003), t + 600);
        await session.TickAsync(t + 2000);
        Assert.True(session.EnvObjBombTowerRemaining(tower.i, tower.o, t + 2000));
        Assert.Contains(session.MapEventNotes, n => n.Contains("Bomb plant refused: out_of_range", StringComparison.Ordinal));
    }
}