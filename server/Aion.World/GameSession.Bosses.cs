using System.Globalization;
using System.Text.RegularExpressions;
using Aion.Protocol.Native;

namespace Aion.World;

// Scripted waves and boss adds in a dungeon copy (docs/BOSSES.md; ported from ckrealm/A2Core c299686, AGPL-3.0). A MapEvent
// StartWave names a wave; its monsters are the spawn entries "{wave}_{n}" of the map (every wave of the Ludra raid:
// ReforgedAbyss_1PartyWave_03 is ReforgedAbyss_1PartyWave_03_1 and _2, client data), which wait dormant from the map load and
// come in order: part n+1 when part n is dead or Aion:Bosses:WavePartSeconds after it came (OURS), each monster sent at the
// nearest player. The wave is clear (ClearWave) once every part came and died. A wave the spawn data does not name may have a
// hand-made one in boss-phases.json (SyntheticWave, OURS). Boss adds are new targets the AI asks for when a phase calls them.
public sealed partial class GameSession
{
    static BossOptions bossRules = BossOptions.Default;

    /// <summary>Aion:Bosses for every session of this host (Program.cs; tests set it back with <see cref="BossOptions.Default"/>).</summary>
    public static void ConfigureBosses(BossOptions options) => bossRules = options.Validated();

    public static BossOptions BossRules => bossRules;

    long WavePartMs => bossRules.WavePartSeconds * 1000L;

    sealed class WaveRun(string name)
    {
        public string Name { get; } = name;
        /// <summary>The spawn entries of a named wave, in order (empty for a hand-made wave).</summary>
        public List<string> Parts { get; init; } = [];
        public int Part { get; set; } = -1;
        public long NextPartMs { get; set; }
        public SyntheticWave? Synthetic { get; init; }
        public int Pulses { get; set; }
        public long NextPulseMs { get; set; }
        public List<int> Summoned { get; } = [];
        public long StartedMs { get; init; }
        public bool Cleared { get; set; }
    }

    readonly Dictionary<string, WaveRun> waveRuns = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The map's named waves and their spawn entries in order ("{wave}_{n}" by n), found at the map load.</summary>
    readonly Dictionary<string, List<string>> waveParts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waves started on this copy and cleared (tests and diagnostics).</summary>
    public IReadOnlyCollection<string> ClearedWaves => waveRuns.Values.Where(w => w.Cleared).Select(w => w.Name).ToList();
    /// <summary>Waves started on this copy and still fought (tests and diagnostics).</summary>
    public IReadOnlyCollection<string> RunningWaves => waveRuns.Values.Where(w => !w.Cleared).Select(w => w.Name).ToList();
    /// <summary>The map's named waves the server tracks (tests and diagnostics).</summary>
    public IReadOnlyDictionary<string, List<string>> KnownWaves => waveParts;

    static BossPhases Bosses => BossPhases.Embedded;

    /// <summary>At the map's preparation (with the deferred spawns): the named waves' entries go dormant until StartWave.</summary>
    void PrepareWaves()
    {
        waveRuns.Clear();
        waveParts.Clear();
        if (!bossRules.Waves || !RunsMapEvents || targets is null) return;
        var names = targets.Values.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var wave in MapEventData.Waves(worldMap))
        {
            var part = new Regex("^" + Regex.Escape(wave) + @"_(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var parts = names.Select(n => (Name: n, Match: part.Match(n))).Where(x => x.Match.Success)
                .OrderBy(x => int.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture)).Select(x => x.Name).ToList();
            if (parts.Count == 0) continue;
            waveParts[wave] = parts;
            dormantSpawners.UnionWith(parts);
        }
    }

    bool IMapEventHost.WaveKnown(string wave) => bossRules.Waves && (waveParts.ContainsKey(wave) || Bosses.Wave(worldMap, wave) is not null);
    bool IMapEventHost.WaveDone(string wave) => waveRuns.TryGetValue(wave, out var run) && run.Cleared;

    int? IMapEventHost.HpPercent(string spawner) =>
        SpawnerNpcs(spawner).FirstOrDefault(t => !t.Dead) is { HpMax: > 0 } t ? (int)Math.Ceiling(t.Hp * 100.0 / t.HpMax) : null;

    void IMapEventHost.StartWave(string wave)
    {
        if (!bossRules.Waves) return;
        if (waveParts.TryGetValue(wave, out var parts))
        {
            // A wave started again (a failed stage runs it anew) begins from its first part.
            var run = waveRuns[wave] = new WaveRun(wave) { Parts = parts, StartedMs = mapEventNow };
            foreach (var part in parts) DoDespawn(part, false);
            NextWavePart(run);
            return;
        }
        if (Bosses.Wave(worldMap, wave) is { } synthetic)
        {
            EndWaveRun(wave);
            waveRuns[wave] = new WaveRun(wave) { Synthetic = synthetic, NextPulseMs = mapEventNow, StartedMs = mapEventNow };
            return;
        }
        Note($"StartWave {wave}: no spawns of it on map {worldMap}; its ClearWave finishes by timeout");
    }

    void IMapEventHost.EndWave(string wave) => EndWaveRun(wave);

    void EndWaveRun(string wave)
    {
        if (!waveRuns.Remove(wave, out var run)) return;
        foreach (var part in run.Parts) DoDespawn(part, false);
        foreach (var key in run.Summoned) Vanish(key);
    }

    /// <summary>A summoned monster goes (0x3642 flag 7), as an EndSpawner does.</summary>
    void Vanish(int key)
    {
        if (targets?.GetValueOrDefault(key) is not { } t || t.Dead && t.CorpseRemoved) return;
        t.Hp = 0;
        t.CorpseRemoved = true;
        respawnAt.Remove(key);
        mapEventOut.AddRange(ai.Killed(t));
        mapEventOut.Add(GamePackets.Death(key, 7));
    }

    void NextWavePart(WaveRun run)
    {
        run.Part++;
        run.NextPartMs = mapEventNow + WavePartMs;
        var name = run.Parts[run.Part];
        DoSpawn(name);
        foreach (var t in SpawnerNpcs(name).Where(t => !t.Dead)) SendAtNearestPlayer(t);
    }

    /// <summary>The monster attacks the nearest living player of the copy (a wave or an add comes for the group).</summary>
    void SendAtNearestPlayer(Target t)
    {
        (int Key, double D)? best = null;
        if (worldPlayer?.Map is { } map)
        {
            foreach (var p in map.Players.Values)
            {
                if (p.Session.dead || p.Hidden) continue;
                var d = p.Position.DistanceSquared(t.Position);
                if (best is null || d < best.Value.D) best = (p.Key, d);
            }
        }
        else if (position is { } at && !dead) best = (playerKey, at.DistanceSquared(t.Position));
        if (best is { } victim) mapEventOut.AddRange(ai.ForceAggro(t, victim.Key, mapEventNow));
    }

    /// <summary>The host's tick: waves bring their next part, hand-made waves pulse, and a wave whose monsters are all dead is clear.</summary>
    void WaveTick(long now)
    {
        if (waveRuns.Count == 0) return;
        foreach (var run in waveRuns.Values.Where(r => !r.Cleared).ToList())
        {
            bool done;
            if (run.Synthetic is { } wave)
            {
                var anchor = SpawnerNpcs(wave.Around).FirstOrDefault(t => !t.Dead);
                if (anchor is not null && run.Pulses < wave.Pulses && now >= run.NextPulseMs && targets is not null)
                {
                    run.Pulses++;
                    run.NextPulseMs = now + wave.EveryMs;
                    foreach (var add in BossSummons.Spawn(data, targets, wave.Npc, anchor.Position, wave.Count, wave.Radius))
                    {
                        run.Summoned.Add(add.Key);
                        seenNpcs.Add(add.Key);
                        mapEventOut.Add(GamePackets.NpcAppear(add.Appearance() with { Reason = NpcAppearance.NpcSpawnReason }));
                        SendAtNearestPlayer(add);
                    }
                }
                // Clear once the anchor is dead and so are its adds; an anchor that never showed ends the wave after WavePartMs.
                done = anchor is null && (run.Pulses > 0 || now - run.StartedMs >= WavePartMs)
                    && run.Summoned.All(k => targets?.GetValueOrDefault(k) is not { Dead: false });
            }
            else
            {
                var current = SpawnerNpcs(run.Parts[run.Part]).All(t => t.Dead);
                if (run.Part < run.Parts.Count - 1 && (current || now >= run.NextPartMs)) { NextWavePart(run); continue; }
                done = run.Part == run.Parts.Count - 1 && run.Parts.All(p => SpawnerNpcs(p).All(t => t.Dead));
            }
            if (!done) continue;
            run.Cleared = true;
            Note($"wave {run.Name} cleared");
            foreach (var r in mapEventRuns.ToList()) r.WaveCleared(run.Name, now);
        }
    }

    /// <summary>
    /// The adds the bosses' phases called since the last tick: copies of the dungeon's monster around the boss, sent at the
    /// boss's victim; they belong to the fight (they go when the boss dies or resets) and never respawn.
    /// </summary>
    List<byte[]> BossAdds(NpcAi fights, Dictionary<int, Target> table, long now)
    {
        var requests = fights.TakeSummons();
        if (requests.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var request in requests)
        {
            var made = BossSummons.Spawn(data, table, request.Adds.Npc, request.Boss.Position, request.Adds.Count, request.Adds.Radius);
            if (made.Count == 0) { Note($"boss {request.Boss.Name}: add NPC {request.Adds.Npc} has no template"); continue; }
            foreach (var add in made)
            {
                request.Fight.Adds.Add(add.Key);
                seenNpcs.Add(add.Key);
                replies.Add(GamePackets.NpcAppear(add.Appearance() with { Reason = NpcAppearance.NpcSpawnReason }));
                replies.AddRange(fights.ForceAggro(add, request.VictimKey, now));
            }
        }
        return replies;
    }
}
