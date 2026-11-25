using System.Text.Json;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Soul-bind stats (GameSession.SoulBind.cs), adapted from Addiboii/A2Core 13aa9ef and 101b443.</summary>
public sealed class SoulBindStatsTests
{
    [Fact]
    public void Soul_bind_complete_carries_the_bound_stats_as_recorded()
    {
        // Community recording 2026-11-06 #49976: gloves 210540022 at bind count 3.
        Assert.Equal("3056000025F8050000160100030329006A000000C2001400000002000E00000000",
            Convert.ToHexString(GamePackets.SoulBindComplete(0x116000005f825, 3, 0, [(41, 106), (194, 20), (2, 14)])));
        Assert.Equal("305600000100000000000000010000", Convert.ToHexString(GamePackets.SoulBindComplete(1)));
    }

    [Fact]
    public void Item_detail_tail_sends_soul_stats_and_their_types_at_zero()
    {
        // Same recording, 0x561b #49975 of the gloves at bind count 3.
        Assert.Equal("00" + "0329006A000000C2001400000002000E000000" + "03290000000000C20000000000020000000000" + "0000000000",
            Convert.ToHexString(GamePackets.DetailTail(new PacketWriter(), [(41, 106), (194, 20), (2, 14)], true).ToArray()));
        // Without soul stats: creation rolls as _sub_stats, empty _add_sub_stats (unchanged from before).
        Assert.Equal("00" + "011A0105000000" + "00" + "0000000000",
            Convert.ToHexString(GamePackets.DetailTail(new PacketWriter(), [(282, 5)], false).ToArray()));
    }

    [Fact]
    public void Attune_reply_has_the_client_reader_layout()
    {
        var packet = GamePackets.SoulBindAttune(0x53, 128, 0, true, (128, 42), (0, 0));
        var reader = new PacketReader(packet) { Offset = 2 };
        Assert.Equal(0, reader.U16());
        Assert.Equal(0x53, (long)reader.I64());
        Assert.Equal(128, reader.U16());
        Assert.Equal(0, reader.I32());
        Assert.True((reader.U8() & 1) != 0);
        Assert.Equal(128, reader.U16());
        Assert.Equal(42, reader.I32());
        Assert.Equal(0, reader.I32());
        Assert.Equal(0, reader.U8());
        Assert.True(reader.End);
    }

    [Fact]
    public async Task Attune_request_answers_5632_and_applies_the_selected_pool_stat()
    {
        const long Now = 1_793_368_800_000;
        const int Gloves = 210_540_022;   // holds a soul-bind pool (see the bind test below)
        const int Cost = 210_130_001;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var bind = SoulBindPools.Of(Gloves)!;
        var pick = bind.Pool[0][0];
        store.Items.Add(FakeCharacterStore.BagItem(Gloves, 1, 0x60,
            upgrades: JsonSerializer.SerializeToElement(new { soul_bind = 1 })));
        store.Items.Add(FakeCharacterStore.BagItem(Cost, 1, 0x61));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(0x5631).I64(0x60).U16(pick & 0xFFFF).I32(0).I64(0x61).ToArray(), Now + 1);

        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x5632);
        var reader = new PacketReader(rs) { Offset = 2 };
        Assert.Equal(0, reader.U16());
        Assert.Equal(0x60, (long)reader.I64());
        Assert.Equal((short)pick, (short)reader.U16());
        Assert.Equal(0, reader.I32());
        Assert.True((reader.U8() & 1) != 0);
        var newType = (int)reader.U16();
        Assert.NotEqual(pick, newType);   // the window must show a different option
        var stored = store.Items.Single(i => i.WireId == 0x60);
        var stats = stored.Upgrades.GetProperty("soul_stats").EnumerateArray().Select(e => (e[0].GetInt32(), e[1].GetInt32())).ToList();
        Assert.Contains(stats, s => s.Item1 == newType);
        Assert.DoesNotContain(stats, s => s.Item1 == pick);
        Assert.DoesNotContain(store.Items, i => i.WireId == 0x61);
    }

    [Fact]
    public void Binding_draws_half_then_all_of_the_pool_and_raises_kept_stats()
    {
        var gloves = SoulBindPools.Of(210540022);
        Assert.NotNull(gloves);
        var rng = new Random(7);
        var bind1 = SoulBindPools.Bind(gloves!, [], 1, rng);
        var bind2 = SoulBindPools.Bind(gloves!, bind1, 2, rng);
        var bind3 = SoulBindPools.Bind(gloves!, bind2, 3, rng);
        bool Inside(List<(int Type, int Value)> s) => s.All(x => gloves!.Pool.Any(p => p[0] == x.Type && x.Value >= p[1] && x.Value <= p[2]));
        Assert.Equal(3, gloves!.StatCount);
        Assert.Equal((2, 3, 3), (bind1.Count, bind2.Count, bind3.Count));
        Assert.Equal(bind1.Select(s => s.Type), bind2.Take(2).Select(s => s.Type));
        Assert.Equal(bind2.Select(s => s.Type), bind3.Select(s => s.Type));
        Assert.All(bind3.Zip(bind2), p => Assert.True(p.First.Value >= p.Second.Value));
        Assert.True(Inside(bind1) && Inside(bind2) && Inside(bind3));
    }

    [Fact]
    public void Soul_stats_replace_the_creation_rolls_in_the_stat_sum()
    {
        var tables = ItemStatTables.Embedded;
        var rolled = JsonDocument.Parse("""{"HPMax":50}""").RootElement;
        var unbound = JsonDocument.Parse("""{"soul_bind":1}""").RootElement;
        var bound = JsonDocument.Parse("""{"soul_bind":2,"soul_stats":[[193,120],[194,16]]}""").RootElement;
        Assert.Equal(50, tables.SumInstance(rolled, unbound, "HPMax"));
        Assert.Equal(120, tables.SumInstance(rolled, bound, "HPMax"));
        Assert.Equal(16, tables.SumInstance(rolled, bound, "HPRegen"));
    }
}
