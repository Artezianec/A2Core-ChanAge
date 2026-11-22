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
        await session.HandleAsync(GroundReady(5003), t + 600);
        await session.TickAsync(t + 2000);
        Assert.False(session.EnvObjBombPlantable(tower.i, tower.o, t + 2000, tower.o.At));
        Assert.Equal("not_carrying", session.BombPlantRefusalReason(t + 2000, 5003));
    }
}