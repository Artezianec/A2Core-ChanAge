using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

public sealed class SkinSetShopUseTests
{
    const long Now = 1_760_100_000_000;
    const int MidnightSlasherItem = 535510221;

    [Fact]
    public async Task Using_midnight_slasher_skin_set_item_grants_set_skins()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now, level: 55);
        var stack = FakeCharacterStore.BagItem(MidnightSlasherItem, 1, 8801);
        store.Items.Add(stack);
        await session.InventoryChangesAsync(default);

        var used = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.ItemUseRq).I64(stack.WireId).ToArray(), Now + 1);
        Assert.Equal(RidePackets.ItemUseRs, Op(used[0]));
        Assert.Equal(0, BitConverter.ToUInt16(used[0], 2));
        Assert.DoesNotContain(store.Items, i => i.TemplateId == MidnightSlasherItem && i.Quantity > 0);
        Assert.Contains(used, p => Op(p) == GameSession.SkinUpdateNt);
    }
}