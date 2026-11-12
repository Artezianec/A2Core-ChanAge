using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Official field boss respawn timers (docs/FIELD-BOSSES.md): their own respawn range, stored, kept across a restart. Ported from KiskEmu (AGPL-3.0, commit d54d69472d).</summary>
[Collection(FieldBossCollection.Name)]
public sealed class FieldBossTimerTests : IDisposable
{
    const long Now = 1_793_000_000_000;
    const int Dartan = 101022;   // M_L1_FieldBoss_Dragon_01_1, map 1010: a grade-6 field boss
    const int Neikel = 101001;   // M_L1_CV_Keneir_Named_02_1, map 1010: a named monster
    const int Kashapa = 101023;  // M_L1_FieldBoss_Kashapa_01_1
    const long Minute = 60_000;

    public FieldBossTimerTests()
    {
        FieldBosses.Configure(FieldBossOptions.Default);
        FieldBosses.Clear();
    }

    public void Dispose()
    {
        FieldBosses.Configure(FieldBossOptions.Default);
        FieldBosses.Clear();
    }

    sealed class MemoryStore : IFieldBossStore
    {
        public Dictionary<int, FieldBossRecord> Rows { get; } = [];

        public Task<IReadOnlyList<FieldBossRecord>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FieldBossRecord>>([.. Rows.Values]);

        public Task SaveStateAsync(int code, DateTimeOffset? killedAt, DateTimeOffset? respawnAt, CancellationToken ct = default)
        {
            Rows[code] = (Rows.GetValueOrDefault(code) ?? new FieldBossRecord(code, null, null, null, null)) with { KilledAt = killedAt, RespawnAt = respawnAt };
            return Task.CompletedTask;
        }

        public Task SaveTimerAsync(int code, int? minMinutes, int? maxMinutes, CancellationToken ct = default)
        {
            Rows[code] = (Rows.GetValueOrDefault(code) ?? new FieldBossRecord(code, null, null, null, null)) with { MinMinutes = minMinutes, MaxMinutes = maxMinutes };
            return Task.CompletedTask;
        }
    }

    static SharedWorld World(MemoryStore store, long now)
    {
        var world = new SharedWorld(GameData.Embedded) { Clock = () => now };
        world.FieldBossTimers.Attach(store, null);
        return world;
    }

    /// <summary>Kills the boss as staff's "clear area" does (the same respawn path as a player's kill).</summary>
    static Target Kill(MapInstance map, int code, long now)
    {
        var boss = map.FieldBossTarget(code)!;
        Assert.False(boss.Dead);
        Assert.True(map.ClearArea(boss.Position.X, boss.Position.Y, 100, now, GameSession.RespawnMs) >= 1);
        Assert.True(boss.Dead);
        return boss;
    }

    [Fact]
    public void Field_bosses_are_the_client_WorldMapFieldNamed_rows_the_server_spawns()
    {
        var timers = new FieldBossTimers(GameData.Embedded);
        Assert.Equal(85, timers.Definitions.Count);
        Assert.Equal(("boss", 1010, 2101120, "M_L1_FieldBoss_Dragon_01_1"), (timers.ByCode[Dartan].Kind, timers.ByCode[Dartan].MapId, timers.ByCode[Dartan].NpcId, timers.ByCode[Dartan].Name));
        Assert.Equal("named", timers.ByCode[Neikel].Kind);
        Assert.Equal((FieldBossTimers.BossMinMinutes, FieldBossTimers.BossMaxMinutes), timers.Range(Dartan));
        Assert.Equal((FieldBossTimers.NamedMinMinutes, FieldBossTimers.NamedMaxMinutes), timers.Range(Neikel));
        Assert.Equal(Kashapa, timers.ByNpcAndMap[(1010, 2101122)].Code);
        var map = new SharedWorld(GameData.Embedded) { Clock = () => Now }.Map(1010);
        Assert.Equal(24, timers.Definitions.Count(d => d.MapId == 1010 && map.FieldBossTarget(d.Code) is not null));
    }

    [Fact]
    public async Task A_killed_boss_stays_dead_across_a_restart_until_its_time()
    {
        var store = new MemoryStore();
        var world = World(store, Now);
        var boss = Kill(world.Map(1010), Dartan, Now);
        var respawn = world.Map(1010).RespawnAt[boss.Key];
        Assert.InRange(respawn, Now + FieldBossTimers.BossMinMinutes * Minute, Now + FieldBossTimers.BossMaxMinutes * Minute);
        await world.FieldBossTimers.FlushAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Now), store.Rows[Dartan].KilledAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(respawn), store.Rows[Dartan].RespawnAt);
        var clock = Assert.Single(FieldBosses.StatesOnMap(1010), s => s.EntityKey == boss.Key);
        Assert.False(clock.Spawned);
        Assert.Equal(respawn, clock.RespawnAtMs);

        // The restart, an hour later: the timers are read before the map is loaded.
        var later = Now + 60 * Minute;
        FieldBosses.Clear();
        var restarted = World(store, later);
        await FieldBossWorld.LoadAsync(store, restarted);
        var map = restarted.Map(1010);
        var again = map.FieldBossTarget(Dartan)!;
        Assert.True(again.Dead);
        Assert.True(again.CorpseRemoved);
        Assert.Equal(respawn, map.RespawnAt[again.Key]);
        var restored = Assert.Single(FieldBosses.StatesOnMap(1010), s => s.EntityKey == again.Key);
        Assert.False(restored.Spawned);
        Assert.Equal(respawn, restored.RespawnAtMs);
        // Other official bosses are up.
        Assert.False(map.FieldBossTarget(Neikel)!.Dead);

        // The timers arrive after the map was loaded (a slow database): the boss leaves then.
        FieldBosses.Clear();
        var late = World(store, later);
        var lateMap = late.Map(1010);
        Assert.False(lateMap.FieldBossTarget(Dartan)!.Dead);
        await FieldBossWorld.LoadAsync(store, late);
        Assert.True(lateMap.FieldBossTarget(Dartan)!.Dead);
        Assert.Equal(respawn, lateMap.RespawnAt[lateMap.FieldBossTarget(Dartan)!.Key]);

        // Restarted after its time: up.
        FieldBosses.Clear();
        var afterwards = World(store, respawn + 1);
        await FieldBossWorld.LoadAsync(store, afterwards);
        Assert.False(afterwards.Map(1010).FieldBossTarget(Dartan)!.Dead);
        Assert.DoesNotContain(FieldBosses.StatesOnMap(1010), s => s.EntityKey == afterwards.Map(1010).FieldBossTarget(Dartan)!.Key && !s.Spawned);
    }

    [Fact]
    public async Task A_player_on_the_map_sees_the_corpse_go_and_the_boss_return_only_at_its_time()
    {
        var store = new FakeCharacterStore();
        var world = new SharedWorld(GameData.Embedded) { Clock = () => Now };
        world.FieldBossTimers.SetRange(Dartan, 120, 120);
        var map = world.Map(1010);
        var boss = map.FieldBossTarget(Dartan)!;
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { World = world, CharacterLevel = 55, WorldId = store.WorldId }, new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.Teleport(1010, boss.Position with { X = boss.Position.X + 600 }, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await session.TickAsync(Now + 1000);

        Kill(map, Dartan, Now + 2000);
        Assert.Equal(Now + 2000 + 120 * Minute, map.RespawnAt[boss.Key]);
        var corpse = await session.TickAsync(Now + 2000 + GameSession.CorpseMs + 100);
        Assert.True(boss.CorpseRemoved);
        Assert.Contains(corpse, p => GameSessionTestHelpers.HasOpcode(p, 0x3642));
        // An ordinary monster would be back after RespawnMs; the boss is not.
        await session.TickAsync(Now + 2000 + GameSession.RespawnMs + 1000);
        Assert.True(boss.Dead);
        await session.TickAsync(Now + 2000 + 120 * Minute);
        Assert.False(boss.Dead);
    }

    [Fact]
    public async Task Stored_range_changes_are_kept_and_used_on_the_next_kill()
    {
        var store = new MemoryStore();
        var world = World(store, Now);
        var map = world.Map(1010);
        var boss = Kill(map, Neikel, Now);
        Assert.True(world.FieldBossTimers.SetRange(Neikel, 30, 45));
        Assert.False(world.FieldBossTimers.SetRange(Neikel, 50, 40));
        Assert.False(world.FieldBossTimers.SetRange(Neikel, 0, 40));
        Assert.False(world.FieldBossTimers.SetRange(Neikel, 30, FieldBossTimers.MaxMinutes + 1));

        world.FieldBossTimers.RespawnNow(Neikel, Now + Minute);
        Assert.True(map.RespawnFieldBoss(Neikel, Now + Minute));
        Assert.Equal(Now + Minute, map.RespawnAt[boss.Key]);
        await world.FieldBossTimers.FlushAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Now + Minute), store.Rows[Neikel].RespawnAt);
        Assert.Equal((30, 45), (store.Rows[Neikel].MinMinutes, store.Rows[Neikel].MaxMinutes));

        // The next kill uses the stored range; after a restart the range is still there; reset brings back the default.
        var restarted = World(store, Now + 2 * Minute);
        await FieldBossWorld.LoadAsync(store, restarted);
        Assert.False(restarted.Map(1010).FieldBossTarget(Neikel)!.Dead);
        Assert.Equal((30, 45), restarted.FieldBossTimers.Range(Neikel));
        var respawn = restarted.FieldBossTimers.Killed(Neikel, Now + 2 * Minute);
        Assert.InRange(respawn, Now + 32 * Minute, Now + 47 * Minute);
        Assert.True(restarted.FieldBossTimers.SetRange(Neikel, null, null));
        Assert.Equal((FieldBossTimers.NamedMinMinutes, FieldBossTimers.NamedMaxMinutes), restarted.FieldBossTimers.Range(Neikel));
        await restarted.FieldBossTimers.FlushAsync();
        Assert.Null(store.Rows[Neikel].MinMinutes);
    }

    [Fact]
    public void Instance_layers_and_ordinary_monsters_keep_the_usual_respawn()
    {
        var world = new SharedWorld(GameData.Embedded) { Clock = () => Now };
        var map = world.Map(1010);
        var ordinary = map.Targets.Values.First(t => t.Hostile && t.OwnerKey == 0 && map.FieldBossTarget(Dartan)?.Key != t.Key && t.NpcId != 2101120
            && !world.FieldBossTimers.Definitions.Any(d => d.NpcId == t.NpcId));
        Assert.Null(map.RecordFieldBossKill(ordinary, Now));
        var layer = world.Map(1010, instance: 123);
        Assert.Null(layer.FieldBossTarget(Dartan));
    }

    [Fact]
    public void Configured_name_overrides_pin_a_fixed_wait()
    {
        FieldBosses.Configure(new FieldBossOptions { RespawnMinutesByName = new() { ["Dragon"] = 45 } });
        var timers = new FieldBossTimers(GameData.Embedded);
        Assert.Equal((45, 45), timers.Range(Dartan));
        Assert.Equal((FieldBossTimers.BossMinMinutes, FieldBossTimers.BossMaxMinutes), timers.Range(Kashapa));
    }
}
