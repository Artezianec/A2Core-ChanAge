using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class BuilderSpeedCommandTests
{
    const long Now = 1_760_400_000_000;
    static GmPolicy StaffPolicy => new() { StaffRole = (_, _) => Task.FromResult<string?>("gamemaster") };

    static BuilderCommands StaffBuilder() =>
        new(new WorldPresence(), StaffPolicy, (_, _, _) => { }, GameData.Embedded);

    static async Task<GameSession> EnterStaffSessionAsync()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId, StaffRole = StaffPolicy.StaffRole },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        store.States[0] = store.States[0] with { Staff = true };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        Assert.True(session.IsAdmin);
        return session;
    }

    static async Task<GameSession> EnterPlainSessionAsync() =>
        await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now);

    static byte[] MoveUpdate(Vector3d at) =>
        new PacketWriter().U16(0x3701).U8(2).Vec3(at).ToArray();

    [Fact]
    public async Task Speed_3_sends_move_speed_update_and_triples_allowed_speed()
    {
        var session = await EnterPlainSessionAsync();
        var baseline = session.AllowedSpeed();
        var packets = session.SetBuilderSpeedMultiplier(3, Now);
        Assert.Single(packets);
        Assert.Equal(0x364b, GameSessionTestHelpers.Op(packets[0]));
        Assert.Equal(20_000, session.WireMoveSpeedPermille());
        Assert.Equal(PlayerPackets.MoveSpeedUpdate(session.PlayerKey, [(GameSession.MoveSpeedStat, 20_000)]), packets[0]);
        Assert.InRange(session.AllowedSpeed(), baseline * 2.99, baseline * 3.01);
    }

    [Fact]
    public async Task Speed_1_resets_multiplier_and_wire()
    {
        var session = await EnterPlainSessionAsync();
        session.SetBuilderSpeedMultiplier(3, Now);
        var packets = session.SetBuilderSpeedMultiplier(1, Now);
        Assert.Equal(0, session.WireMoveSpeedPermille());
        Assert.Equal(PlayerPackets.MoveSpeedUpdate(session.PlayerKey, [(GameSession.MoveSpeedStat, 0)]), packets[0]);
        Assert.Equal(1, session.BuilderSpeedMultiplier);
    }

    [Fact]
    public async Task Builder_speed_command_is_staff_only_and_applies_multiplier()
    {
        var plain = await EnterPlainSessionAsync();
        var denied = await StaffBuilder().RunAsync(plain, "Speed 3", Now, default);
        Assert.Equal(1, denied.Result);

        var staff = await EnterStaffSessionAsync();
        var ok = await StaffBuilder().RunAsync(staff, "Speed 3", Now, default);
        Assert.Equal(0, ok.Result);
        Assert.Contains(ok.Packets, p => p.SequenceEqual(PlayerPackets.MoveSpeedUpdate(staff.PlayerKey, [(GameSession.MoveSpeedStat, 20_000)])));
    }

    [Fact]
    public void Speed_is_listed_in_builder_usage_help()
    {
        Assert.Contains("Speed", BuilderCommands.Supported);
        var field = typeof(BuilderCommands).GetField("Usage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var usage = (Dictionary<string, string>)field!.GetValue(null)!;
        Assert.True(usage.TryGetValue("Speed", out var line));
        Assert.Contains("multiplier", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x364b", line);
    }

    [Fact]
    public async Task Triple_speed_lets_long_run_step_pass_anti_cheat()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var at = session.Position!.Value;
        await session.HandleAsync(MoveUpdate(at with { X = at.X + 400 }), Now + 100);
        var far = at with { X = at.X + 5_000 };
        var reject = await session.HandleAsync(MoveUpdate(far), Now + 200);
        Assert.Single(reject);
        Assert.Equal(0x373f, GameSessionTestHelpers.Op(reject[0]));
        Assert.Equal(at with { X = (float)(at.X + 400) }, session.Position);
        session.SetBuilderSpeedMultiplier(3, Now + 300);
        var accept = await session.HandleAsync(MoveUpdate(far), Now + 400);
        Assert.DoesNotContain(accept, p => GameSessionTestHelpers.Op(p) == GameSession.MoveUpdateReject);
        Assert.Equal(far with { X = (float)(at.X + 5_000) }, session.Position);
    }
}
