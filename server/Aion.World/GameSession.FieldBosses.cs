using Aion.Protocol.Native;

namespace Aion.World;

// Open-world field bosses (docs/FIELD-BOSSES.md; ported from ckrealm/A2Core 77ac9ed GameSession.Fun.cs, AGPL-3.0).
// Official videos (2026-11): every contributor gets a personal bag reward from the client participant table (the bound
// chest already in loot.json); each sees "Defeated {name}." then "Reward collected." (Notice_NT 0x8d2f); the optional
// server/zone broadcast is off by default. The map Field Boss tab is 0x3A0C/0x3A0D (usmap); we answer it, but
// WorldMapFieldNamedTableItem is not exported, so the timer list stays empty (see docs/FIELD-BOSSES.md).
public sealed partial class GameSession
{
    public const ushort WorldMapContentsInfoRq = 0x3A0C, WorldMapContentsInfoRs = 0x3A0D;

    /// <summary>The field rank of a target on this session's map (tests and diagnostics).</summary>
    public FieldRank FieldRankOf(Target target) => FieldBosses.Rank(target, instanceKey != 0 || RunsMapEvents);

    /// <summary>A killed monster's respawn delay: its field-boss time (Aion:FieldBosses) or <see cref="RespawnMs"/>.</summary>
    long KillRespawnMs(Target target) =>
        FieldBosses.Options.Enabled && FieldRankOf(target) is not FieldRank.None and var rank ? FieldBosses.RespawnMs(target, rank, rng) : RespawnMs;

    /// <summary>True when this kill should skip the usual ground loot and give every contributor a personal bag reward instead.</summary>
    bool FieldBossPersonalRewards(Target target) =>
        FieldBosses.Options is { Enabled: true, SharedLoot: true } && FieldRankOf(target) is not FieldRank.None;

    /// <summary>
    /// A field boss or named elite died to this player (call before ai.Killed: the hate table and healer set name who
    /// fought): every contributor still on the map, including the killer, rolls the participant table into their bag;
    /// a boss's kill and its next appearance may be broadcast when configured.
    /// </summary>
    void FieldBossKilled(Target target, long now)
    {
        if (!FieldBosses.Options.Enabled) return;
        var rank = FieldRankOf(target);
        if (rank == FieldRank.None) return;
        var contributors = new List<WorldPlayer>();
        if (worldPlayer?.Map is { } map)
        {
            var keys = new HashSet<int> { playerKey };
            if (map.Ai.Brains.TryGetValue(target.Key, out var brain))
            {
                keys.UnionWith(brain.Hate.Keys);
                keys.UnionWith(brain.Healers);
            }
            foreach (var key in keys)
                if (map.Players.TryGetValue(key, out var helper)) contributors.Add(helper);
        }
        if (FieldBosses.Options.SharedLoot)
            foreach (var helper in contributors)
            {
                var session = helper.Session;
                _ = session.InvokeAsync(async at => (true, await session.FieldBossRewardAsync(target, at)));
            }
        var due = respawnAt.GetValueOrDefault(target.Key);
        FieldBosses.NoteKilled(new FieldBossState(worldMap, target.Key, target.NpcId, target.Name, target.Level, rank,
            target.Position.X, target.Position.Y, target.Position.Z, Spawned: false, RespawnAtMs: due));
        if (rank != FieldRank.Boss) return;
        var others = Math.Max(0, contributors.Count - 1);
        FieldBosses.Announce(FieldBosses.Defeated(StoredName, others, target.Name, target.Level, worldMap));
        if (due > now) FieldBosses.Announce(FieldBosses.Appeared(target.Name, target.Level, worldMap), due - now);
    }

    /// <summary>
    /// One contributor's own roll of a field boss's loot (their race): its client participant table ("*_participant_all")
    /// when it has one, granted to their bag (Aion:FieldBosses:SharedLoot). Official: a personal bound chest such as
    /// 533700375 (Gartua / Island of Eternity, Box_FieldBoss_A_48_b) or 533700373 (Kashapa, Box_FieldBoss_A_46_b).
    /// </summary>
    internal async Task<List<byte[]>> FieldBossRewardAsync(Target target, long now)
    {
        _ = now;
        var rate = options.World?.DropRate ?? 1;
        var fraction = rate - Math.Floor(rate);
        var rolls = (int)Math.Floor(rate) + (fraction > 0 && rng.NextDouble() < fraction ? 1 : 0);
        var rolled = new List<(int, long)>();
        for (var i = 0; i < rolls; i++) rolled.AddRange(LootTables.Embedded.RollParticipant(target.NpcId, profileRace, rng));
        FieldBossShares++;
        FieldBossItemsRolled += rolled.Count;
        var replies = new List<byte[]>();
        if (FieldBosses.Options.ParticipantNotices)
        {
            var defeated = FieldBosses.DefeatedPersonal(target.Name);
            replies.Add(Notice(defeated.English, defeated.Russian, 20, "FFFFFF"));
        }
        if (rolled.Count > 0 && Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, rolled, default) ? [[]] : []);
            if (granted.Count > 0)
            {
                replies.AddRange(await InventoryChangesAsync(default, IncreaseDropItemGet, 0));
                if (FieldBosses.Options.ParticipantNotices)
                    replies.Add(Notice(FieldBosses.RewardCollected.English, FieldBosses.RewardCollected.Russian, 20, "FFFFFF"));
            }
        }
        else if (rolled.Count > 0 && FieldBosses.Options.ParticipantNotices)
            replies.Add(Notice(FieldBosses.RewardCollected.English, FieldBosses.RewardCollected.Russian, 20, "FFFFFF"));
        return replies;
    }

    /// <summary>
    /// A heal from this player towards <paramref name="friends"/> counts as a field-boss contribution when the healer
    /// or a healed ally is already on that boss's hate table (official: healers also get the personal chest).
    /// </summary>
    void CreditFieldBossHeal(IEnumerable<GameSession> friends)
    {
        if (!FieldBosses.Options.Enabled || worldPlayer?.Map is not { } map) return;
        var inInstance = instanceKey != 0 || RunsMapEvents;
        var healed = friends.Select(f => f.PlayerKey).ToHashSet();
        foreach (var (key, brain) in map.Ai.Brains)
        {
            if (!map.Targets.TryGetValue(key, out var target) || target.Dead) continue;
            if (FieldBosses.Rank(target, inInstance) == FieldRank.None) continue;
            if (brain.Hate.ContainsKey(playerKey) || healed.Any(brain.Hate.ContainsKey))
                brain.Healers.Add(playerKey);
        }
    }

    /// <summary>
    /// 0x3A0C WorldMapContentsInfo_RQ {i32 map id}: usmap 0x3A0D (_result i32, _map_id i32, field-named list,
    /// boss list, rift list, env list). The Field Boss tab timers live in WorldMapFieldNamedInfo (_data_id,
    /// _pos, _is_spawned, _respawn_time), but WorldMapFieldNamedTableItem is not in our client-data export, so
    /// that list is empty (GAP). Spawned bosses are listed as WorldMapBossInfo (_uid, _monster_id, _pos) with no timer
    /// (that struct has none). Rifts and env objects are empty here.
    /// </summary>
    internal List<byte[]> WorldMapContentsInfo(byte[] plain)
    {
        int mapId;
        try { mapId = new PacketReader(plain) { Offset = 2 }.I32(); }
        catch (ProtocolException) { return []; }
        var writer = PacketWriter.Opcode(WorldMapContentsInfoRs).I32(0).I32(mapId);
        writer.Varint(0);   // _field_named_info_list: data_id mapping unknown
        var bosses = new List<Target>();
        if (FieldBosses.Options.Enabled && options.World is { } world)
        {
            var map = world.LoadedMap(mapId) ?? (mapId == 0 ? worldPlayer?.Map : null);
            if (map is not null)
            {
                foreach (var target in map.Targets.Values.OrderBy(t => t.Key))
                    if (!target.Dead && FieldBosses.Rank(target, map.Instance != 0) is not FieldRank.None)
                        bosses.Add(target);
            }
        }
        writer.Varint(bosses.Count);
        foreach (var boss in bosses)
            writer.I32(boss.Key).I32(boss.NpcId).F32(boss.Position.X).F32(boss.Position.Y).F32(boss.Position.Z);
        writer.Varint(0);   // _rift_info_list
        writer.Varint(0);   // _env_obj_list
        return [writer.ToArray()];
    }

    /// <summary>Field boss kills this session rolled a personal share of, and the items those rolls gave (tests).</summary>
    public int FieldBossShares { get; private set; }
    public int FieldBossItemsRolled { get; private set; }
}
