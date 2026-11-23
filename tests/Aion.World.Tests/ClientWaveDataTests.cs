using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>WaveStepData parts in client-waves.json drive ClearWave instead of the 30 s fallback.</summary>
public sealed class ClientWaveDataTests
{
    [Fact]
    public void Embedded_client_waves_loads_wavestep_maps()
    {
        var waves = ClientWaves.Embedded;
        Assert.True(waves.TryGetParts(900001, "DungeonWave_0001_Wave_L_B_03", out var daily));
        Assert.Equal(10, daily.Length);
        Assert.StartsWith("S1_DungeonWave_0001_Multi_Monster_Wave_L_21", daily[0]);
    }

    [Fact]
    public void Beritra_defence_wave_has_client_parts()
    {
        var waves = ClientWaves.Embedded;
        Assert.True(waves.TryGetParts(111005, "FE_World_D_A_Defence_01", out var parts));
        Assert.Equal(28, parts.Length);
    }

    [Fact]
    public void Ludra_raid_map_still_uses_spawn_pattern_at_runtime()
    {
        // client-waves intentionally omits Ludra {wave}_{n} waves; GameSession discovers them from spawn names.
        var waves = ClientWaves.Embedded;
        Assert.DoesNotContain("ReforgedAbyss_01_Wave_01", waves.WavesOnMap(600082).Keys);
    }

    [Fact]
    public void Abyss_rift_phases_embed_the_client_export()
    {
        var phases = AbyssRiftPhases.Embedded.PhasesOf(1);
        Assert.NotNull(phases);
        Assert.Equal(8, phases!.Length);
        Assert.Equal(1, phases[0].Index);
        Assert.Contains(phases[1].Ends, e => e.Type == "KillNpcCount");
    }
}