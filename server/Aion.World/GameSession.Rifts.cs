using Aion.Protocol.Native;

namespace Aion.World;

// Hidden cubes and Spacetime Rifts. Data: WorldPools (tools/data/world-pools.json).
// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md), adapted to our object use path.
//
// HIDDEN CUBES (and the Altgard treasure boxes E_D1_TreasureBoxRare_002, EnvObjData category HiddenCubeDark): 206 MapData pool records
// keep two cubes each at random positions of the record (WorldPools, PoolState). Rules from EnvObjData/RewardData: bIsKeyOnly cubes open
// only with one of RequireValues (any key, RequireCount 1, consumed); the others open without a key (bIsOptionalRequirement) and give
// RewardName, with a key also BasicKeyRewards (CANDIDATE: both). Cubes without a RewardData row use their faction's tables (CANDIDATE).
// An opened cube's slot comes back after CubeRespawnMinutes at a random free position of its pool (CANDIDATE 15 min).
//
// SPACETIME RIFTS: 10 rift spawners (WorldPools.Rifts): every 180 min from 02:00 KST, open RiftData LifeTime 600 s at one of 5
// positions, 400 uses, level 45-100, use time 2.5 s. Only the map's own faction may use one (L10N MSG_RIFT_USE_FAIL_NOT_PROPER_TRIBE).
// The move: TeleportEffect 30 to an exit point of Teleport.dat RiftPortal_X_to_Y_Teleport_n on the other faction's map (the exit with
// the opening's position number, CANDIDATE pairing). The visit lasts rift_contentsticket_default_time (GlobalSetting 3600000 ms), then
// the player goes back to the rift they came through. Announcement: "A Spacetime Rift has appeared." (MSG_RIFT_SPAWN text). Any move
// off the other faction's field maps ends the visit. No rift was ever recorded: their spawn, use and move bytes are CANDIDATE.
// Not ported: their per-world population choice (we serve one world per faction pair).
public sealed partial class GameSession
{
    /// <summary>How often a player on a field map rechecks the pools and rifts around them (rifts open and close, cubes come and go; OURS).</summary>
    const long RiftTickMs = 2000;
    /// <summary>How long a rift visit lasts (GlobalSetting rift_contentsticket_default_time 3600000 ms).</summary>
    public const int RiftVisitMinutes = 60;
    /// <summary>An opened hidden cube's slot comes back after this long (CANDIDATE: no respawn time in the client data).</summary>
    public const int CubeRespawnMinutes = 15;
    /// <summary>L10N Message_MSG_RIFT_SPAWN (en-US): the text of the official rift notice.</summary>
    public const string RiftSpawnText = "A Spacetime Rift has appeared.";

    /// <summary>Where the visit ends (the rift used, on the player's own map) and when.</summary>
    (int Map, Vector3d At, long Until)? riftVisit;
    /// <summary>Set while the rift itself moves the player (the move keeps the visit).</summary>
    bool riftMoving;
    long riftAnnounced, riftTickAt, riftVisibilityAt;
    PoolState? ownPools;

    public (int Map, Vector3d At, long Until)? RiftVisit => riftVisit;

    /// <summary>The pools and rift uses of the player's copy of the map (the shared map's, else this session's own).</summary>
    PoolState Pools => worldPlayer?.Map is { } map && map.MapId == worldMap ? map.Pools
        : ownPools is { } own && own.MapId == worldMap ? own : ownPools = new PoolState(worldMap);

    internal PoolState PoolsForTests => Pools;

    // ---------- Visibility ----------

    /// <summary>Whether a placed object stands now: a pool placement only while a slot of its pool stands there, a rift only while open
    /// at this position; every other object always.</summary>
    bool PoolOrRiftShown(int index, WorldObject o, long now) =>
        o.Pool is not null ? instanceKey == 0 && Pools.Standing(index, rng, now) is not null
        : o.Rift is null || instanceKey == 0 && (WorldPools.RiftShown(worldMap, index, o, now, out _) || QuestRiftShown(index, o));

    /// <summary>
    /// OURS: a closed rift still stands at its spawner's first position for a player whose open quest step wants it used (UseEnvObj,
    /// e.g. DQ1202026), as our other quest gates never block an open objective; the quest is not held to the 3-hour window.
    /// </summary>
    bool QuestRiftShown(int index, WorldObject o) =>
        questLog?.OpenUseEnvObjIds().Contains(o.Env) == true && WorldPools.RiftOf(o.Rift) is { } rift
        && WorldPools.RiftPoints(worldMap, rift.Spawner) is { Count: > 0 } points && points[0] == index;

    // ---------- Use ----------

    static List<byte[]> PoolOrRiftRefused(int key) => [GamePackets.InteractEnvObjReply(key, 1), GamePackets.InteractEnvObjAvailableState(key)];

    /// <summary>A cube or rift that may not be used now: the refusal (before the use starts), else null.</summary>
    List<byte[]>? PoolOrRiftRefusal(int index, WorldObject o, int key, long now)
    {
        if (o.Pool is not null)
        {
            if (instanceKey != 0 || Pools.Standing(index, rng, now) is null)
            {
                Note($"InteractEnvObj refused on map {worldMap}: cube {o.Name} (key {key}) is not there (opened)");
                return PoolOrRiftRefused(key);
            }
            if (WorldPools.CubeOf(o.Env) is { KeyOnly: true } cube && CubeKey(cube) is null)
            {
                Note($"InteractEnvObj refused on map {worldMap}: cube {o.Name} (key {key}) opens only with a key");
                return PoolOrRiftRefused(key);
            }
            return null;
        }
        if (WorldPools.RiftOf(o.Rift) is not { } rift) return null;
        if (RiftRefusal(rift, index, o, now) is { } why)
        {
            Note($"InteractEnvObj refused on map {worldMap}: rift {rift.Spawner} (key {key}): {why}");
            return PoolOrRiftRefused(key);
        }
        return null;
    }

    /// <summary>Why a rift may not be used, or null: closed or not at this position, the other faction's, the level, the use limit.</summary>
    string? RiftRefusal(WorldPools.RiftInfo rift, int index, WorldObject o, long now)
    {
        var open = WorldPools.RiftShown(worldMap, index, o, now, out var opened);
        if (instanceKey != 0 || !open && !QuestRiftShown(index, o)) return "closed";
        if (WorldPools.FieldRace(rift.Map) is var own and not 0 && own != profileRace) return "other faction";
        var quest = QuestRiftShown(index, o);   // an open UseEnvObj objective is never blocked (OURS)
        if (!quest && (level < rift.MinLevel || level > rift.MaxLevel)) return "level";
        if (rift.Exits.Count == 0) return "no exits";
        if (rift.Limit > 0 && Pools.RiftUses(rift.Spawner, opened) >= rift.Limit) return "use limit";
        return null;
    }

    /// <summary>The key a cube takes from the bag: the first of its RequireValues the character holds, else null.</summary>
    Aion.Application.Gameplay.ItemState? CubeKey(WorldPools.CubeInfo cube)
    {
        foreach (var id in cube.Keys)
            if (storedItems.Values.FirstOrDefault(i => i.TemplateId == id && i.Location == "bag" && i.Quantity >= cube.KeyCount) is { } row)
                return row;
        return null;
    }

    /// <summary>
    /// A completed cube use (CompleteUseAsync hands pool objects here): the slot is taken (another player may have been first), the
    /// key consumed when the cube needs or takes one, the RewardData tables rolled, the cube despawned.
    /// </summary>
    async Task<List<byte[]>> OpenCubeAsync(int index, WorldObject o, int key, long now, CancellationToken ct)
    {
        var cube = WorldPools.CubeOf(o.Env);
        var keyRow = cube is null ? null : CubeKey(cube);
        if (cube is { KeyOnly: true } && keyRow is null) return PoolOrRiftRefused(key);
        if (!Pools.Take(index, rng, now, CubeRespawnMinutes * 60_000L))
        {
            Note($"cube {o.Name} (key {key}) was opened by someone else first");
            return PoolOrRiftRefused(key);
        }
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjReply(key),
        };
        shownObjects.Remove(index);
        replies.Add(GamePackets.EnvObjDespawn(key, 4, 3));
        if (cube is not null)
        {
            var tables = new List<string>();
            if (LootTables.Embedded.HasTable(cube.Normal)) tables.Add(cube.Normal);
            if (keyRow is not null && LootTables.Embedded.HasTable(cube.Key)) tables.Add(cube.Key);
            var items = LootTables.Embedded.RollNamed(tables, profileRace, rng);
            if (Store is { } store && CharacterId is { } character && (items.Count > 0 || keyRow is not null))
            {
                var granted = await Stored(async () => (keyRow is not null
                    ? await store.ConsumeAndGrantAsync(character, keyRow.InstanceId, keyRow.Revision, cube.KeyCount, items, ct)
                    : await store.GrantLootAsync(character, items, ct)) ? [[]] : []);
                if (granted.Count > 0) replies.AddRange(await InventoryChangesAsync(ct, IncreaseEnvObjReward, keyRow is null ? (byte)0 : DecreaseItemUse));
            }
            Note($"cube {o.Name} opened on map {worldMap}{(keyRow is null ? "" : $" with key {keyRow.TemplateId}")}: "
                 + string.Join(", ", items.Select(i => $"{i.ItemId} x{i.Count}")));
        }
        replies.AddRange(await CreditUseEnvObjQuestAsync(o.Env, ct));
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(o.Name), now));
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, now));
        return replies;
    }

    /// <summary>
    /// Through a rift: counted against its limit, the ordinary end of a use, quest and achievement progress, then the move to an exit
    /// point on the other faction's map and the visit's timer.
    /// </summary>
    async Task<List<byte[]>> UseRiftAsync(int index, WorldObject o, int key, long now, CancellationToken ct)
    {
        if (WorldPools.RiftOf(o.Rift) is not { } rift) return [];
        var refusal = RiftRefusal(rift, index, o, now);
        if (refusal is null && WorldPools.RiftShown(worldMap, index, o, now, out var opened) && !Pools.UseRift(rift.Spawner, opened, rift.Limit))
            refusal = "use limit";
        if (refusal is not null) return PoolOrRiftRefused(key);
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key),
            GamePackets.InteractEnvObjAvailableState(key), GamePackets.InteractEnvObjReply(key),
        };
        replies.AddRange(await CreditUseEnvObjQuestAsync(o.Env, ct));
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(o.Name), now));
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, now));
        WorldPools.RiftShown(worldMap, index, o, now, out var openedAt);
        var points = WorldPools.RiftPoints(worldMap, rift.Spawner);
        var number = WorldPools.PointOf(rift, openedAt, points.Count);
        var exit = rift.Exits[number < rift.Exits.Count ? number : 0];
        Note($"rift {rift.Spawner} ({rift.Rift}) on map {worldMap} -> map {rift.ArrivalMap} at {rift.Teleport}#{number}");
        var home = position ?? o.At;
        riftMoving = true;
        try { replies.AddRange(Teleport(rift.ArrivalMap, exit, now, 0, (byte)Math.Clamp(rift.Effect, 0, 255))); }
        finally { riftMoving = false; }
        riftVisit = (rift.Map, home, now + RiftVisitMinutes * 60_000L);
        return replies;
    }

    /// <summary>Teleport: a move off the other faction's field maps (home, a dungeon, the Abyss) ends a rift visit.</summary>
    void RiftTeleporting(int mapId)
    {
        if (riftMoving || riftVisit is null) return;
        var race = WorldPools.FieldRace(mapId);
        if (race != 0 && race != profileRace) return;   // still in the other faction's land (a resurrection there)
        riftVisit = null;
    }

    // ---------- Tick: announcement, the visit's end, the field maps' pools and rifts ----------

    List<byte[]> RiftTick(long now)
    {
        if (resources is null || reloading || now < riftTickAt) return [];
        riftTickAt = now + 1000;
        var replies = new List<byte[]>();
        // The announcement: once per opening.
        if (WorldPools.Rifts.Count > 0)
        {
            var latest = WorldPools.Rifts.Values.Select(r => WorldPools.OpenedAt(r, now) ?? 0).Max();
            if (latest > riftAnnounced)
            {
                if (riftAnnounced != 0 || now - latest < RiftTickMs * 2) replies.Add(Notice(RiftSpawnText));
                riftAnnounced = latest;
            }
        }
        var here = WorldPools.FieldRace(worldMap);
        if (riftVisit is { } visit && now >= visit.Until && !dead)
        {
            Note($"rift visit over: back to map {visit.Map}");
            riftVisit = null;
            replies.AddRange(Teleport(visit.Map, visit.At, now, 0, (byte)(WorldPools.Rifts.Values.FirstOrDefault()?.Effect ?? 0)));
            return replies;
        }
        if (here != 0 && instanceKey == 0 && position is { } at && now >= riftVisibilityAt)
        {
            riftVisibilityAt = now + RiftTickMs;
            replies.AddRange(ObjectVisibility(at, now));
        }
        return replies;
    }
}
