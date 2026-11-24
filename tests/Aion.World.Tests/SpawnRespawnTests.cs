using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Per-spawn mob respawn from tools/data/spawn-respawn.json (gap audit item 14); 30 s OURS fallback.</summary>
public sealed class SpawnRespawnTests
{
    const long Now = 1_760_040_000_000;
    static readonly GameData Data = GameData.Embedded;

    [Fact]
    public void Embedded_export_applies_delay_to_placed_spawn()
    {
        var spawn = Data.Spawns(1010).First(s => s.Name == "M_L1_AC_A_ForestWisp_01_1");
        Assert.Equal(12_000, spawn.SpawnRespawnMs);
    }

    [Fact]
    public void Missing_export_keeps_zero_and_session_fallback()
    {
        var spawn = Data.Spawns(1010).First(s => s.Name == "M_L1_AC_A_ForestWisp_01_10");
        Assert.Equal(0, spawn.SpawnRespawnMs);
        var target = new Target
        {
            Key = 1, Name = spawn.Name, HpMax = 1, Hp = 1, Position = default, NpcId = spawn.NpcId,
            UniqueKey = spawn.UniqueKey, SpawnRespawnMs = spawn.SpawnRespawnMs,
        };
        Assert.Equal(GameSession.RespawnMs, MobRespawnMs(target));
    }

    [Fact]
    public async Task Kill_schedules_data_driven_respawn_before_fallback_window()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 10);
        var spawn = Data.Spawns(1010).First(s => s.Name == "M_L1_AC_A_ForestWisp_01_1");
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        session.Teleport(1010, at, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var target = session.Targets!.Values.First(t => t.Name == spawn.Name);
        Assert.Equal(12_000, target.SpawnRespawnMs);
        target.Hp = 1;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var castAt = Now + 1000;
        await MapEventScenarioHelpers.CombatCastAsync(session, skill, target.Key, at, castAt);
        var landAt = castAt + MapEventScenarioHelpers.SkillHitsLandMs(skill);
        Assert.True(target.Dead);
        await session.TickAsync(landAt + target.SpawnRespawnMs - 500);
        Assert.True(target.Dead);
        await session.TickAsync(landAt + target.SpawnRespawnMs + 50);
        Assert.Equal(target.HpMax, target.Hp);
        Assert.False(target.Dead);
    }

    [Fact]
    public async Task Kill_without_export_uses_thirty_second_fallback()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 10);
        var spawn = Data.Spawns(1010).First(s => s.Name == "M_L1_AC_A_ForestWisp_01_10");
        Assert.Equal(0, spawn.SpawnRespawnMs);
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        session.Teleport(1010, at, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var target = session.Targets!.Values.First(t => t.Name == spawn.Name);
        target.Hp = 1;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var castAt = Now + 1000;
        await MapEventScenarioHelpers.CombatCastAsync(session, skill, target.Key, at, castAt);
        var landAt = castAt + MapEventScenarioHelpers.SkillHitsLandMs(skill);
        await session.TickAsync(landAt + GameSession.RespawnMs - 500);
        Assert.True(target.Dead);
        await session.TickAsync(landAt + GameSession.RespawnMs + 50);
        Assert.Equal(target.HpMax, target.Hp);
    }

    static long MobRespawnMs(Target target) => target.SpawnRespawnMs > 0 ? target.SpawnRespawnMs : GameSession.RespawnMs;
}