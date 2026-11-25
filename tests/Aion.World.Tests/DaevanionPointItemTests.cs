using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DaevanionPointItemTests
{
    const long Now = 1_760_040_000_000;

    static byte[] Create(int creationId, string name) =>
        new PacketWriter().U16(0x361b).String(name).U8(1).U32((uint)creationId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();

    static byte[] UseByItemId(long uid, int itemId, int count = 1) =>
        new PacketWriter().U16(RidePackets.ItemUseByItemIdRq).I64(uid).I32(itemId).I32(count).I32(0).ToArray();

    [Fact]
    public async Task Morph_crystal_use_grants_bonus_points_and_consumes_item()
    {
        const int crystal = 531360003;
        Assert.Equal("GetDaevanionPoint", RewardBoxes.Embedded.Use(crystal)!.Effect);

        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(crystal, 1, 901));
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(2))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(Create(6, "CrystalUse"), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = 16 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 2);

        var before = session.DaevanionAvailableForTests(1);
        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(901).ToArray(), Now + 3);
        Assert.Equal(0, replies[0][2]);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == crystal && i.Quantity > 0);
        Assert.Equal(before + 3, session.DaevanionAvailableForTests(1));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.UpdateItemDaevanionPointNt);
    }

    [Fact]
    public async Task ItemUseByItemID_0x5614_grants_daevanion_and_battle_crystal_points()
    {
        const int morphCrystal = 531360003;
        const int battleCrystal = 533560003;
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(morphCrystal, 2, 901));
        store.Items.Add(FakeCharacterStore.BagItem(battleCrystal, 1, 902));
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(4))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(Create(6, "Crystal5614"), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = 45 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 2);

        var morphReplies = await session.HandleAsync(UseByItemId(901, morphCrystal, 2), Now + 3);
        var morphRs = morphReplies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseByItemIdRs);
        Assert.Equal(0, BitConverter.ToInt32(morphRs, 2));
        Assert.Equal(6, session.DaevanionAvailableForTests(1));

        var battleReplies = await session.HandleAsync(UseByItemId(902, battleCrystal), Now + 4);
        var battleRs = battleReplies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseByItemIdRs);
        Assert.Equal(0, BitConverter.ToInt32(battleRs, 2));
        Assert.Equal(3, session.DaevanionAvailableForTests(5));
    }
}
