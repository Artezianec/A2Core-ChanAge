using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Chained node acquires in one session: the client maps grid slots from 0xe226 wire order; OpenNode RS alone is not enough
/// when the new node extends a parent opened this session (playtest: skill chain nodes until relog).
/// </summary>
public sealed class DaevanionChainOpenWireTests
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

    static byte[] LiveOpenRoute(int boardId, params int[] nodeIds)
    {
        var w = PacketWriter.Opcode(GameSession.DaevanionOpenNodeRqLive).I32(boardId).Varint((uint)nodeIds.Length);
        foreach (var id in nodeIds) w.I32(id);
        return w.ToArray();
    }

    static byte[] LegacyOpen(int nodeId) => PacketWriter.Opcode(GameSession.DaevanionOpenNodeRq).I32(nodeId).ToArray();

    [Fact]
    public async Task Second_node_in_chain_includes_board_info_with_activation_order()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 12, WorldId = 1204 }, new Random(21))
        {
            Store = store,
        };
        const long now = 1_760_040_000_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 12 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        var tables = DaevanionTables.Embedded;
        var boardId = tables.BoardsForCreation(1).First();
        var start = tables.StartNodeId(boardId);
        session.ReloadDaevanionForTests(new DaevanionProgress([start], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 10 }));

        var startNode = tables.NodeOf(start)!;
        var step1 = tables.NodesOfBoard(boardId)
            .First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, startNode));
        var step1Node = tables.NodeOf(step1)!;
        var step2 = tables.NodesOfBoard(boardId)
            .First(id => id != step1 && tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, step1Node));

        var first = await session.HandleAsync(LegacyOpen(step1), now + 1);
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeRs && BitConverter.ToUInt16(p, 2) == 0);
        var firstBoard = first.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt);
        Assert.Equal([start, step1], ParseBoardNodes(firstBoard, boardId));

        var second = await session.HandleAsync(LegacyOpen(step2), now + 2);
        Assert.Contains(second, p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeRs && BitConverter.ToUInt16(p, 2) == 0);
        var secondBoard = second.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt);
        Assert.Equal([start, step1, step2], ParseBoardNodes(secondBoard, boardId));
        Assert.Equal([start, step1, step2], store.Daevanion!.NodeIds);
    }

    [Fact]
    public async Task Live_route_batch_includes_board_info_with_full_chain()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 12, WorldId = 1204 }, new Random(22))
        {
            Store = store,
        };
        const long now = 1_760_040_100_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 12 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        var tables = DaevanionTables.Embedded;
        var boardId = tables.BoardsForCreation(1).First();
        var start = tables.StartNodeId(boardId);
        session.ReloadDaevanionForTests(new DaevanionProgress([start], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 10 }));

        var startNode = tables.NodeOf(start)!;
        var step1 = tables.NodesOfBoard(boardId)
            .First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, startNode));
        var step1Node = tables.NodeOf(step1)!;
        var step2 = tables.NodesOfBoard(boardId)
            .First(id => id != step1 && tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, step1Node));

        var replies = await session.HandleAsync(LiveOpenRoute(boardId, step1, step2), now + 1);
        var board = replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt);
        Assert.Equal([start, step1, step2], ParseBoardNodes(board, boardId));
    }
}
