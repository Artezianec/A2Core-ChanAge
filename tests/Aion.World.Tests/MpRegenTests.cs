using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MpRegenTests
{
    const long Now = 1_760_200_000_000;

    static async Task<GameSession> EnterAsClassAsync(string className)
    {
        var data = GameData.Embedded;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1 } p && p.Class == className);
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data, new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Mp").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Theory]
    [InlineData("Gladiator")]
    [InlineData("Sorcerer")]
    [InlineData("Templar")]
    [InlineData("Ranger")]
    public async Task Mp_regenerates_out_of_combat_for_every_class(string className)
    {
        var session = await EnterAsClassAsync(className);
        session.SetResource('M', 100);
        var before = session.Resource!.Mp;
        var replies = await session.TickAsync(Now + 2_000);
        Assert.True(session.Resource.Mp > before, $"{className}: MP {before} -> {session.Resource.Mp}");
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) != 0);
    }

    [Theory]
    [InlineData("Gladiator")]
    [InlineData("Sorcerer")]
    [InlineData("Templar")]
    [InlineData("Ranger")]
    public async Task Mp_regenerates_in_combat_at_the_battle_rate(string className)
    {
        var (session, _) = await SkillEffectTests.Setup(className);
        session.NpcHitsPlayer(1, Now + 6_000);
        Assert.True(session.InCombat);
        await session.TickAsync(Now + 6_000);
        session.SetResource('M', 100);
        var before = session.Resource!.Mp;
        await session.TickAsync(Now + 7_000);
        Assert.True(session.InCombat);
        var gained = session.Resource.Mp - before;
        Assert.InRange(gained, 25, 249);
        if (className != "Sorcerer") Assert.Equal(25, gained);
    }

    [Fact]
    public async Task Rest_regen_stays_faster_than_battle_regen_and_sitting_triples_it()
    {
        var session = await EnterAsClassAsync("Gladiator");
        session.SetResource('M', 100);
        var before = session.Resource!.Mp;
        await session.TickAsync(Now + 1_000);
        var standing = session.Resource.Mp - before;
        Assert.Equal(250, standing);
        await session.HandleAsync(new PacketWriter().U16(0x3747).Varint(session.PlayerKey).I32(0).ToArray(), Now + 1_000);
        session.SetResource('M', 100);
        before = session.Resource.Mp;
        await session.TickAsync(Now + 2_000);
        Assert.Equal(standing * GameSession.RestRegenFactor, session.Resource.Mp - before);
    }
}
