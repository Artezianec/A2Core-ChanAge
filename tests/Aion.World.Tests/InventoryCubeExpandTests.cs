using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class InventoryCubeExpandTests
{
    const long Now = 1_700_000_000_000;

    static int InventoryGoldExtendTail(byte[] depot5611) =>
        BitConverter.ToInt32(depot5611, depot5611.Length - 20);

    [Fact]
    public async Task Gold_bag_extension_deducts_kina_and_replies_success()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 124_500, 9001));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var rq = new PacketWriter().U16(GameSession.ItemContainerSlotExtendRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(0).ToArray();
        var replies = await session.HandleAsync(rq, Now + 1);

        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtendRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtendNt);
        Assert.Equal(1, store.ContainerExtends[character].InventoryByGold);
        Assert.Equal(119_500, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);

        var depot = replies.Last(p => GameSessionTestHelpers.Op(p) == 0x5611);
        Assert.Equal(1, InventoryGoldExtendTail(depot));
    }

    [Fact]
    public async Task World_entry_depot_tail_reflects_persisted_bag_extensions()
    {
        var store = new FakeCharacterStore();
        _ = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.ContainerExtends[character] = new ContainerSlotExtendCounts(0, 1, 0, 0, 0, 0);

        var session2 = new GameSession(GameSessionTestHelpers.LiveGameData, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session2.HandleAsync(GameSessionTestHelpers.Login(), Now + 10);
        var wireId = store.States[0].WireId;
        await session2.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 10);
        var entry = await session2.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10);

        var depot = entry.First(p => GameSessionTestHelpers.Op(p) == 0x5611);
        Assert.Equal(1, InventoryGoldExtendTail(depot));
    }

    [Fact]
    public async Task Mismatched_costIdx_resyncs_depot_so_next_request_uses_current_count()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.ContainerExtends[character] = new ContainerSlotExtendCounts(0, 1, 0, 0, 0, 0);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 124_500, 9005));

        var stale = new PacketWriter().U16(GameSession.ItemContainerSlotExtend8dRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(0).ToArray();
        var reject = await session.HandleAsync(stale, Now + 1);
        var rs = reject.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dRs);
        Assert.Equal(4, BitConverter.ToInt32(rs, 2));
        var depot = reject.First(p => GameSessionTestHelpers.Op(p) == 0x5611);
        Assert.Equal(1, InventoryGoldExtendTail(depot));

        var next = new PacketWriter().U16(GameSession.ItemContainerSlotExtend8dRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(1).ToArray();
        var ok = await session.HandleAsync(next, Now + 2);
        Assert.Equal(0, BitConverter.ToInt32(ok.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dRs), 2));
        Assert.Equal(2, store.ContainerExtends[character].InventoryByGold);
    }

    [Fact]
    public async Task Live_8d4f_run_same_as_5848_gold_extension()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 124_500, 9003));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var rq = new PacketWriter().U16(GameSession.ItemContainerSlotExtend8dRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(0).ToArray();
        var replies = await session.HandleAsync(rq, Now + 1);

        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dNt);
        Assert.Equal(119_500, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task Extension_refuses_when_kina_insufficient()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 1000, 9002));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var rq = new PacketWriter().U16(GameSession.ItemContainerSlotExtendRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(0).ToArray();
        var rs = (await session.HandleAsync(rq, Now + 1)).First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtendRs);
        Assert.Equal(3, BitConverter.ToInt32(rs, 2));
        Assert.Equal(1000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
    }
}
