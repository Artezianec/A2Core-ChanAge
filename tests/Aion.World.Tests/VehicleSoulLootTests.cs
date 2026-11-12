using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Field soul drops (GetVehicleSoul, AutoUseWhenAcquire): pickup must remove the ground bag once,
/// grant and consume the item once, and tell other players on the map that the bag is gone
/// (playtest 2026-11-11: Soul bags stayed visible after pickup / item_use).
/// </summary>
public sealed class VehicleSoulLootTests
{
    const long Now = 1_762_886_500_000;
    const int SoulItem = 534660162;   // GetVehicleSoul [1128, 1] Drana Slime
    const int SoulRide = 1128;

    [Fact]
    public async Task Picking_up_a_vehicle_soul_uses_it_once_and_removes_the_bag()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        const long uid = 0x11_22_33_44_55_66L;
        var at = session.Position ?? Vector3d.Zero;
        session.RegisterGroundDrop(new GameSession.GroundDrop(2001, uid, SoulItem, 1, at, at, Now + GameSession.DropLifetimeMs));

        var loot = await session.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(uid).U8(0).ToArray(), Now + 100);
        Assert.Contains(loot, p => p.SequenceEqual(LootPackets.LootReply(0, uid, false)));
        Assert.Contains(loot, p => GameSessionTestHelpers.Op(p) == LootPackets.DropRemoveNt);
        Assert.DoesNotContain(session.GroundDrops, d => d == uid);
        Assert.Equal(1, store.RideRows.Single(r => r.RideId == SoulRide).SoulPoints);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == SoulItem && i.Location == "bag");

        var again = await session.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(uid).U8(0).ToArray(), Now + 200);
        Assert.Contains(again, p => p.SequenceEqual(LootPackets.LootReply(1, uid, false)));
        Assert.Equal(1, store.RideRows.Single(r => r.RideId == SoulRide).SoulPoints);
    }

    [Fact]
    public async Task Pickup_broadcasts_drop_removal_to_other_players_on_the_map()
    {
        var world = new SharedWorld(GameData.Embedded);
        var aliceStore = new FakeCharacterStore();
        var bobStore = new FakeCharacterStore();
        var alice = await EnterOn(world, aliceStore, "LootA", 1);
        var bob = await EnterOn(world, bobStore, "LootB", 2);
        var spawn = GameData.Embedded.Spawns(1010).First(s => s.NpcId == 2100002);
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        alice.Teleport(1010, at, Now);
        bob.Teleport(1010, at, Now);
        await alice.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await bob.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);

        var target = alice.Targets.Values.First(t => t.NpcId == 2100002);
        target.Hp = 1;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(alice);
        var castAt = Now + 1_000;
        var kill = await MapEventScenarioHelpers.CombatCastAsync(alice, skill, target.Key, at, castAt);
        Assert.True(target.Dead);
        Assert.Contains(kill, p => GameSessionTestHelpers.Op(p) == LootPackets.DropNt);
        var uid = alice.GroundDrops.First();

        var bobSawDrop = await bob.TickAsync(castAt + MapEventScenarioHelpers.SkillHitsLandMs(skill) + 10);
        Assert.Contains(bobSawDrop, p => GameSessionTestHelpers.Op(p) == LootPackets.DropNt);

        var loot = await alice.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(uid).U8(0).ToArray(), castAt + 2_000);
        Assert.Contains(loot, p => p.SequenceEqual(LootPackets.LootReply(0, uid, false)));
        Assert.Contains(loot, p => GameSessionTestHelpers.Op(p) == LootPackets.DropRemoveNt);

        var bobSawRemove = await bob.TickAsync(castAt + 2_010);
        Assert.Contains(bobSawRemove, p => GameSessionTestHelpers.Op(p) == LootPackets.DropRemoveNt);
        Assert.DoesNotContain(alice.GroundDrops, d => d == uid);
    }

    static async Task<GameSession> EnterOn(SharedWorld world, FakeCharacterStore store, string name, uint creation)
    {
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = world, WorldId = store.WorldId, CharacterLevel = 20, TrainingDummy = false },
            new Random((int)creation))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States.Single().WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }
}
