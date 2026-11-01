using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ItemUseHandlerTests
{
    const long Now = 1_760_030_000_000;

    [Fact]
    public async Task GetGodstone_item_use_via_0x5614_consumes_and_updates_collection()
    {
        const int godstoneItem = 513830001;
        Assert.Equal("GetGodstone", RewardBoxes.Embedded.Use(godstoneItem)!.Effect);
        Assert.True(RewardBoxes.Embedded.ImplementsUseEffect("GetGodstone"));

        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(godstoneItem, 1, 880));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var plain = new PacketWriter().U16(RidePackets.ItemUseByItemIdRq).I64(880).I32(godstoneItem).I32(1).I32(0).ToArray();
        Assert.Equal(22, plain.Length);
        var replies = await session.HandleAsync(plain, Now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseByItemIdRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.DoesNotContain(store.Items, i => i.TemplateId == godstoneItem && i.Quantity > 0);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.GodstoneUpdateNt);
    }

    [Fact]
    public async Task Missing_use_data_replies_with_failure_result()
    {
        const int unknownUsable = 999_999_001;
        Assert.Null(RewardBoxes.Embedded.Use(unknownUsable));

        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(unknownUsable, 1, 881));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(881).ToArray(), Now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseRs);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(rs, 2));
    }
}
