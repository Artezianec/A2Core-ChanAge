using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Party loot bags must stay pickupable for the killer when no other member is in range
/// (playtest: visible soul/loot bags that reject 0x5620 with no 0x5621 success).
/// </summary>
public sealed class PartyLootPickupTests
{
    const long Now = 1_762_900_000_000;
    const int SoulItem = 534660162;
    const int SoulRide = 1128;

    [Fact]
    public async Task Killer_alone_on_map_can_pick_up_party_kill_drop()
    {
        var world = new SharedWorld(GameData.Embedded);
        var killerStore = new FakeCharacterStore();
        var allyStore = new FakeCharacterStore();
        var killer = await Enter(world, killerStore, "SoloKiller", 1);
        var ally = await Enter(world, allyStore, "FarAlly", 2);
        await killer.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionRandom, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), Now);
        var party = world.Social.Parties.Values.Single();
        party.Members.Add(ally.WorldPlayer!);

        var spawn = GameData.Embedded.Spawns(1010).First(s => s.NpcId == 2100002);
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        killer.Teleport(1010, at, Now);
        await killer.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        ally.Teleport(1010, Vector3d.Zero, Now);
        await ally.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);

        var target = killer.Targets.Values.First(t => t.NpcId == 2100002);
        target.Hp = 1;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(killer);
        var castAt = Now + 1_000;
        var kill = await MapEventScenarioHelpers.CombatCastAsync(killer, skill, target.Key, at, castAt);
        Assert.Contains(kill, p => GameSessionTestHelpers.Op(p) == LootPackets.DropNt);
        var uid = killer.GroundDrops.First();
        Assert.NotEmpty(killer.GroundDrops);

        var loot = await killer.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(uid).U8(0).ToArray(), castAt + 2_000);
        Assert.Contains(loot, p => p.SequenceEqual(LootPackets.LootReply(0, uid, false)));
    }

    [Fact]
    public async Task Party_dice_soul_reward_auto_consumes_into_pet_souls()
    {
        var world = new SharedWorld(GameData.Embedded);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore();
        var alice = await Enter(world, storeA, "SoulA", 3);
        var bob = await Enter(world, storeB, "SoulB", 4);
        await alice.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionManual, PartyOptions.JoinFree, PartyOptions.SpecialDice, 1)), Now);
        await alice.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "SoulB"), Now);
        await bob.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(
            world.Social.Parties.Values.Single().Key, PartyInviteAnswer.Accept)]), Now + 1);
        var party = world.Social.Parties.Values.Single();
        var roll = new PartyDiceRoll
        {
            DiceUid = 77, PartyKey = party.Key, ItemId = SoulItem, Count = 1, KillerKey = alice.PlayerKey,
            Center = Vector3d.Zero, At = Vector3d.Zero, ExpiresAtMs = Now + GameSession.PartyLootRollMs,
        };
        roll.Eligible.Add(alice.PlayerKey);
        roll.Eligible.Add(bob.PlayerKey);
        roll.Responses[alice.PlayerKey] = null;
        roll.Responses[bob.PlayerKey] = null;
        world.Social.DiceRolls[roll.DiceUid] = roll;
        await alice.HandleAsync(new PacketWriter().U16(DistributionOpcodes.RollDiceRq).I64(roll.DiceUid).ToArray(), Now + 2);
        await bob.HandleAsync(new PacketWriter().U16(DistributionOpcodes.RollDiceRq).I64(roll.DiceUid).ToArray(), Now + 3);
        await alice.TickAsync(Now + 4);
        await bob.TickAsync(Now + 5);
        Assert.DoesNotContain(world.Social.DiceRolls, kv => kv.Key == roll.DiceUid);
        Assert.Equal(1, storeB.RideRows.Single(r => r.RideId == SoulRide).SoulPoints);
        Assert.DoesNotContain(storeB.Items, i => i.TemplateId == SoulItem && i.Location == "bag");
    }

    static async Task<GameSession> Enter(SharedWorld world, FakeCharacterStore store, string name, uint creation)
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