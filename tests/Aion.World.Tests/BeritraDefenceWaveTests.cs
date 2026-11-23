using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The Beritra defence wave (FE_World_D_A_Defence_01, 28 WaveStepData parts) names its three supply boxes
/// (World_D_A_Defence_01_SupplyBox_2..4, environment-object spawners, not NPC spawns): every part is tracked, 25 NPC and 3 env.
/// </summary>
public sealed class BeritraDefenceWaveTests
{
    const long Now = 1_760_200_000_000;
    const string Wave = "FE_World_D_A_Defence_01";

    [Fact]
    public async Task The_defence_wave_keeps_all_28_client_parts_including_the_supply_box_spawners()
    {
        var store = new FakeCharacterStore { Progress = (60, 0) };
        var session = new GameSession(GameData.Embedded, new GameSessionOptions
        {
            World = new SharedWorld(GameData.Embedded), CharacterLevel = 60, WorldId = store.WorldId, TrainingDummy = false,
            StaffRole = (_, _) => Task.FromResult<string?>("gamemaster"),
        }, new Random(19)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        store.States[0] = store.States[0] with { Staff = true };
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, MapEventTables.Embedded.DungeonIdOfMap(111005), Now + 100);
        Assert.Equal(111005, session.MapId);
        for (var i = 0; i < 4; i++) await session.TickAsync(Now + 200 + i * 50);

        Assert.True(session.KnownWaves.TryGetValue(Wave, out var parts), string.Join(" | ", session.MapEventNotes.TakeLast(5)));
        Assert.Equal(28, parts!.Count);
        Assert.Equal(["World_D_A_Defence_01_SupplyBox_2", "World_D_A_Defence_01_SupplyBox_3", "World_D_A_Defence_01_SupplyBox_4"],
            parts.Where(p => p.Contains("SupplyBox", StringComparison.Ordinal)));
    }
}
