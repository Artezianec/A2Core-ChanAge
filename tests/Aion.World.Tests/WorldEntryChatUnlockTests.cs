using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class WorldEntryChatUnlockTests
{
    const long Now = 1_760_500_000_000;

    [Fact]
    public async Task World_load_withholds_chat_unlock_until_legion_entry_query()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var enter = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        Assert.All(enter, b =>
        {
            if (b.Length >= 2 && b[0] == 0x58 && b[1] == 0xE2)
                throw new Xunit.Sdk.XunitException("ContentsUnlockUpdate (0xe258) must follow the legion entry query, not the load burst.");
        });
    }

    [Fact]
    public async Task Legion_entry_query_carries_chat_contents_unlock_once()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var replies = await session.HandleAsync(PacketWriter.Opcode(GuildOpcodes.SendJoinRequestsRq).ToArray(), Now + 1);
        Assert.Equal(2, replies.Count);
        Assert.Equal(GuildOpcodes.SendJoinRequestsRs, GameSessionTestHelpers.Op(replies[0]));
        Assert.Equal(0xE258, GameSessionTestHelpers.Op(replies[1]));
        var again = await session.HandleAsync(PacketWriter.Opcode(GuildOpcodes.SendJoinRequestsRq).ToArray(), Now + 2);
        Assert.Single(again);
    }

    [Fact]
    public async Task CharCustomizingStatusCheck_accepts_non_empty_request_body()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var padded = PacketWriter.Opcode(GameSession.CharCustomizingStatusCheckRq).U8(0).ToArray();
        var replies = await session.HandleAsync(padded, Now + 1);
        Assert.Single(replies);
        Assert.Equal(GameSession.CharCustomizingStatusCheckRs, GameSessionTestHelpers.Op(replies[0]));
    }
}