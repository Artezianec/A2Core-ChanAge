using Aion.Application.Chat;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class GmMenuAndPresenceTests
{
    const long Now = 1_760_400_000_000;
    static readonly ChatIdentity Chatter = new("1", "1204", "en-US");
    static GmPolicy StaffPolicy => new() { StaffRole = (_, _) => Task.FromResult<string?>("gamemaster") };

    static async Task<(GameSession Session, WorldPresence Presence, GmCommands Gm)> StaffInWorldAsync()
    {
        var presence = new WorldPresence();
        var builder = new BuilderCommands(presence, StaffPolicy, (_, _, _) => { }, GameData.Embedded);
        var store = new FakeCharacterStore();
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions
            {
                WorldId = store.WorldId, StaffRole = StaffPolicy.StaffRole, Builder = builder,
            },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        store.States[0] = store.States[0] with { Staff = true };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return (session, presence, new GmCommands(presence, builder));
    }

    [Fact]
    public async Task Gm_menu_lists_groups_for_staff()
    {
        var (session, presence, gm) = await StaffInWorldAsync();
        var name = session.CharacterName!;
        presence.Enter(name, session, session.Store!.AccountId);
        var text = await gm.HandleAsync(Chatter, name, "//gm menu", default);
        Assert.Contains("Groups:", text);
        Assert.Contains("combat", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("//gm menu <group>", text);
    }

    [Fact]
    public async Task Gm_menu_group_includes_usage_lines()
    {
        var (session, presence, gm) = await StaffInWorldAsync();
        var name = session.CharacterName!;
        presence.Enter(name, session, session.Store!.AccountId);
        var text = await gm.HandleAsync(Chatter, name, "//gm menu movement", default);
        Assert.StartsWith("[movement]", text);
        Assert.Contains("//Teleport", text);
        Assert.Contains("Teleport [MapID]", text);
    }

    [Fact]
    public async Task Gm_chat_needs_world_presence_after_load_complete()
    {
        var (session, presence, gm) = await StaffInWorldAsync();
        var name = session.CharacterName!;
        Assert.Equal("Enter the world first.", await gm.HandleAsync(Chatter, name, "//gm menu", default));
        presence.Enter(name, session, session.Store!.AccountId);
        Assert.Contains("Groups:", await gm.HandleAsync(Chatter, name, "//gm menu", default));
    }

    [Fact]
    public async Task Gm_menu_works_when_world_presence_syncs_after_load_complete()
    {
        var (session, presence, gm) = await StaffInWorldAsync();
        var name = session.CharacterName!;
        presence.Enter(name, session, session.Store!.AccountId);
        Assert.Single(presence.All);
        Assert.Contains("Groups:", await gm.HandleAsync(Chatter, name, "//gm menu", default));
    }

    [Fact]
    public async Task Gm_invoke_runs_on_next_tick_without_client_packet()
    {
        var (session, presence, gm) = await StaffInWorldAsync();
        var name = session.CharacterName!;
        presence.Enter(name, session, session.Store!.AccountId);
        var command = gm.HandleAsync(Chatter, name, "//SetInvisible true", default);
        var tick = session.TickAsync(Now + 50);
        var reply = await command;
        await tick;
        Assert.Equal("Invisible.", reply);
    }
}