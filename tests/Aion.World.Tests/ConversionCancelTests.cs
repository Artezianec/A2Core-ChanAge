using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>ConversionCancel_RQ (0x8d88): end an active transformation from the buff UI without clearing item cooldowns.</summary>
public sealed class ConversionCancelTests
{
    const long Now = 1_760_000_000_500;
    const int ShugoCandy = 510800006, ShugoMorphAbnormal = 42001401;

    static byte[] CancelRq() => PacketWriter.Opcode(GameSession.ConversionCancelRq).ToArray();

    [Fact]
    public async Task Refused_when_not_transformed()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var rs = Assert.Single(await session.HandleAsync(CancelRq(), Now), p => GameSessionTestHelpers.Op(p) == GameSession.ConversionCancelRs);
        Assert.Equal(GameSession.ConversionInvalidData, new PacketReader(rs) { Offset = 2 }.U16());
        Assert.DoesNotContain(await session.HandleAsync(CancelRq(), Now), p => GameSessionTestHelpers.Op(p) == GameSession.ConversionCanceledNt);
    }

    [Fact]
    public async Task Cancels_polymorph_buff_restores_appearance_and_keeps_cooldown()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(ShugoCandy, 1, 50));
        await session.InventoryChangesAsync(default);
        var used = await session.HandleAsync(PacketWriter.Opcode(RidePackets.ItemUseRq).I64(50).ToArray(), Now);
        Assert.Contains(used, p => GameSessionTestHelpers.Op(p) == 0x3655);
        Assert.Contains(session.ActiveBuffs, id => id == ShugoMorphAbnormal);

        var cancelAt = Now + 200;
        var replies = await session.HandleAsync(CancelRq(), cancelAt);
        Assert.Equal(0, new PacketReader(Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ConversionCancelRs)) { Offset = 2 }.U16());
        Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ConversionCanceledNt);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.Contains(replies, p => p.SequenceEqual(RidePackets.PolymorphUpdate(session.PlayerKey, 0)));
        Assert.DoesNotContain(session.ActiveBuffs, id => id == ShugoMorphAbnormal);

        store.Items.Add(FakeCharacterStore.BagItem(ShugoCandy, 1, 52));
        await session.InventoryChangesAsync(default);
        var blocked = await session.HandleAsync(PacketWriter.Opcode(RidePackets.ItemUseByItemIdRq).I64(52).I32(ShugoCandy).I32(1).ToArray(), cancelAt + 100);
        var useRs = Assert.Single(blocked, p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseByItemIdRs);
        Assert.NotEqual((ushort)0, new PacketReader(useRs) { Offset = 2 }.U16());

        var afterExpire = await session.TickAsync(cancelAt + 900_100);
        Assert.DoesNotContain(afterExpire, p => p.SequenceEqual(RidePackets.PolymorphUpdate(session.PlayerKey, 0)));
    }

    [Fact]
    public async Task Morph_still_ends_when_timer_expires()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(ShugoCandy, 1, 51));
        await session.InventoryChangesAsync(default);
        await session.HandleAsync(PacketWriter.Opcode(RidePackets.ItemUseRq).I64(51).ToArray(), Now);
        Assert.Contains(session.ActiveBuffs, id => id == ShugoMorphAbnormal);

        var expired = await session.TickAsync(Now + 901_000);
        Assert.DoesNotContain(session.ActiveBuffs, id => id == ShugoMorphAbnormal);
        Assert.Contains(expired, p => p.SequenceEqual(RidePackets.PolymorphUpdate(session.PlayerKey, 0)));
    }
}