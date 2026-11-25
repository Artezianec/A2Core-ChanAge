using Aion.Application.Chat;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Chat unlock and //gm on the first world entry after connect (not teleport/relog).
/// </summary>
public sealed class FirstWorldEntryGmChatTests
{
    const long Now = 1_760_600_000_000;
    static readonly ChatIdentity Chatter = new("1", "1204", "en-US");
    static GmPolicy StaffPolicy => new() { StaffRole = (_, _) => Task.FromResult<string?>("gamemaster") };

    [Fact]
    public async Task Comm_key_request_before_world_entry_does_not_consume_chat_unlock()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var comm = await session.HandleAsync(PacketWriter.Opcode(0x3659).ToArray(), Now + 1);
        Assert.DoesNotContain(comm, b => b.Length >= 2 && b[0] == 0x58 && b[1] == 0xE2);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now + 2);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 3);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 4);
        var legion = await session.HandleAsync(PacketWriter.Opcode(GuildOpcodes.SendJoinRequestsRq).ToArray(), Now + 5);
        Assert.Equal(2, legion.Count);
        Assert.Equal(0xE258, GameSessionTestHelpers.Op(legion[1]));
    }

    [Fact]
    public async Task Gm_menu_works_on_first_world_entry_with_native_presence_sync()
    {
        var presence = new WorldPresence();
        var builder = new BuilderCommands(presence, StaffPolicy, (_, _, _) => { }, GameData.Embedded);
        var store = new FakeCharacterStore();
        GameSession session = null!;
        session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions
            {
                WorldId = store.WorldId, StaffRole = StaffPolicy.StaffRole, Builder = builder,
                SyncWorldPresence = () =>
                {
                    if (session.CharacterName is { } n) presence.Enter(n, session, session.Store?.AccountId);
                },
            },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var comm = await session.HandleAsync(PacketWriter.Opcode(0x3659).ToArray(), Now + 1);
        Assert.DoesNotContain(comm, b => b.Length >= 2 && b[0] == 0x58 && b[1] == 0xE2);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now + 2);
        store.States[0] = store.States[0] with { Staff = true };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 3);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 4);
        await session.HandleAsync(PacketWriter.Opcode(GuildOpcodes.SendJoinRequestsRq).ToArray(), Now + 5);
        var gm = new GmCommands(presence, builder);
        var name = session.CharacterName!;
        Assert.Single(presence.All);
        var text = await gm.HandleAsync(Chatter, name, "//gm menu", default);
        Assert.Contains("Groups:", text);
    }
}