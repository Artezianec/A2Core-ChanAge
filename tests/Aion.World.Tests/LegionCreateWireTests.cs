using System.Linq;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LegionCreateWireTests
{
    [Fact]
    public async Task Create_legion_rs_uses_i32_result_and_consumes_kina()
    {
        const long now = 1_760_300_000_000;
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 150_000, 1));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now);
        var kinaBefore = store.Items.Sum(i => i.Quantity);

        var replies = await session.HandleAsync(
            new PacketWriter().U16(GameSession.GuildCreateRq).String("Dawnguard").U8(0).String("").I32(3).String("hello").ToArray(), now);

        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.GuildCreateRs);
        Assert.Equal([0x01, 0x8a, 0x00, 0x00, 0x00, 0x00], rs.AsSpan(0, 6).ToArray());
        Assert.NotNull(session.Guild);
        Assert.Equal("Dawnguard", session.Guild!.Name);
        Assert.Equal(kinaBefore - GameSession.GuildCreateKina, store.Items.Sum(i => i.Quantity));
    }
}