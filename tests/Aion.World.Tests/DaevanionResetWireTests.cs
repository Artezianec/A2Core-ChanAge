using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DaevanionResetWireTests
{
    static List<int> ParseBoardNodes(byte[] packet, int boardId)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        var count = reader.Varint();
        for (var i = 0; i < count; i++)
        {
            var id = reader.I32();
            var nodes = reader.Varint();
            if (id != boardId) { for (var j = 0; j < nodes; j++) reader.I32(); continue; }
            var list = new List<int>();
            for (var j = 0; j < nodes; j++) list.Add(reader.I32());
            return list;
        }
        return [];
    }

    static async Task<GameSession> RangerSessionAsync(FakeCharacterStore store, int level)
    {
        const long now = 1_760_030_000_000;
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = level, WorldId = 1204 }, new Random(12))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ResetArcher").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = level } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 1);
        return session;
    }

    [Fact]
    public void Board_wire_lists_start_node_before_activation_order()
    {
        const int boardId = 31;
        var tables = DaevanionTables.Embedded;
        var start = tables.StartNodeId(boardId);
        int[] route = [start, 310097, 310082];
        var session = new GameSession(GameData.Embedded, new GameSessionOptions(), Random.Shared);
        session.ReloadDaevanionForTests(new DaevanionProgress(route, new Dictionary<int, int> { [1] = 2 }, new Dictionary<int, int>()));

        var wire = session.DaevanionBoardNodeIdsForTests(boardId);
        Assert.Equal([start, 310097, 310082], wire);
        Assert.Equal(start, ParseBoardNodes(session.DaevanionBoardInfoWire(), boardId)[0]);
    }

    [Fact]
    public async Task Reset_board_emits_close_notifies_and_start_only_board_info()
    {
        var store = new FakeCharacterStore();
        var session = await RangerSessionAsync(store, 18);
        const int boardId = 31;
        var tables = DaevanionTables.Embedded;
        var start = tables.StartNodeId(boardId);
        int[] route = [start, 310097, 310082, 310067];
        session.ReloadDaevanionForTests(new DaevanionProgress(route, new Dictionary<int, int> { [1] = 3 }, new Dictionary<int, int>()));

        var replies = await session.HandleAsync(PacketWriter.Opcode(GameSession.DaevanionResetBoardRq).I32(boardId).ToArray(), 1_760_030_100_000);

        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionResetBoardRs);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(rs, 2));
        var closes = replies.Where(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionCloseNodeRs).Select(p => BitConverter.ToInt32(p, 4)).ToList();
        Assert.Equal([310067, 310082, 310097], closes);
        var boardNodes = ParseBoardNodes(replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt), boardId);
        Assert.Equal([start], boardNodes);
        Assert.Equal(0, store.Daevanion!.UsedPoints.GetValueOrDefault(1));
    }

    [Fact]
    public async Task World_entry_board_info_includes_persisted_nodes_with_start_first()
    {
        var store = new FakeCharacterStore();
        const int boardId = 31;
        var tables = DaevanionTables.Embedded;
        var start = tables.StartNodeId(boardId);
        store.Daevanion = new DaevanionProgress([start, 310097, 310082], new Dictionary<int, int> { [1] = 2 }, new Dictionary<int, int>());

        var session = await RangerSessionAsync(store, 18);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), 1_760_030_200_000);
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), 1_760_030_200_001));

        var nodes = ParseBoardNodes(entry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt), boardId);
        Assert.Equal([start, 310097, 310082], nodes);
    }
}
