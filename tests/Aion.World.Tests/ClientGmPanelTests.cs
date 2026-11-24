using System.Text;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ClientGmPanelTests
{
    const long Now = 1_760_400_000_000;
    static GmPolicy StaffPolicy => new() { StaffRole = (_, _) => Task.FromResult<string?>("gamemaster") };

    [Fact]
    public async Task Builder_console_disabled_when_ClientGmPanel_off()
    {
        var store = new FakeCharacterStore();
        var builder = new BuilderCommands(new WorldPresence(), StaffPolicy, (_, _, _) => { }, GameData.Embedded);
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { WorldId = store.WorldId, StaffRole = StaffPolicy.StaffRole, Builder = builder, ClientGmPanel = false },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        store.States[0] = store.States[0] with { Staff = true };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var body = new PacketWriter().U16(0x362e).Varint(1).String("SetInvisible true").ToArray();
        var replies = await session.HandleAsync(body, Now);
        var response = Assert.Single(replies);
        Assert.Contains("AION_CLIENT_GM_PANEL", Encoding.UTF8.GetString(response));
    }
}