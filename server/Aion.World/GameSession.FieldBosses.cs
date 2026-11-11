namespace Aion.World;

// Open-world field bosses (docs/FIELD-BOSSES.md; ported from ckrealm/A2Core 77ac9ed GameSession.Fun.cs, AGPL-3.0), on the
// killer's loop: a field boss or named elite (FieldBosses.Rank) respawns after its long window instead of RespawnMs; a boss's
// kill and its return are server-wide notices (FieldBosses queues them, the host's FieldBossNoticeService shows them); everyone
// else on its hate table who is still on the map rolls its loot on their own loop.
public sealed partial class GameSession
{
    /// <summary>The field rank of a target on this session's map (tests and diagnostics).</summary>
    public FieldRank FieldRankOf(Target target) => FieldBosses.Rank(target, instanceKey != 0 || RunsMapEvents);

    /// <summary>A killed monster's respawn delay: its field-boss time (Aion:FieldBosses) or <see cref="RespawnMs"/>.</summary>
    long KillRespawnMs(Target target) =>
        FieldBosses.Options.Enabled && FieldRankOf(target) is not FieldRank.None and var rank ? FieldBosses.RespawnMs(target, rank, rng) : RespawnMs;

    /// <summary>
    /// A field boss or named elite died to this player (call before ai.Killed: the hate table names who fought): the others
    /// who fought it roll its loot on their own loops (party members nearby already share the killer's); a boss's kill and
    /// its next appearance are announced.
    /// </summary>
    void FieldBossKilled(Target target, long now)
    {
        if (!FieldBosses.Options.Enabled) return;
        var rank = FieldRankOf(target);
        if (rank == FieldRank.None) return;
        var helpers = new List<WorldPlayer>();
        if (worldPlayer?.Map is { } map && map.Ai.Brains.TryGetValue(target.Key, out var brain))
        {
            var sharing = NearbyPartyMembers();
            foreach (var key in brain.Hate.Keys)
                if (key != playerKey && map.Players.TryGetValue(key, out var helper) && !helper.Session.dead && !sharing.Contains(helper))
                    helpers.Add(helper);
        }
        if (FieldBosses.Options.SharedLoot)
            foreach (var helper in helpers)
            {
                var session = helper.Session;
                _ = session.InvokeAsync(at => (true, session.FieldBossLoot(target, at)));
            }
        if (rank != FieldRank.Boss) return;
        var party = worldPlayer is not null && Social?.PartyOf(worldPlayer) is { } p ? p.Others(worldPlayer).Count() : 0;
        FieldBosses.Announce(FieldBosses.Defeated(StoredName, helpers.Count + party, target.Name, target.Level, worldMap));
        var due = respawnAt.GetValueOrDefault(target.Key);
        if (due > now) FieldBosses.Announce(FieldBosses.Appeared(target.Name, target.Level, worldMap), due - now);
    }

    /// <summary>
    /// One other fighter's own roll of a field boss's loot (their race): its client participant table ("*_participant_all")
    /// when it has one, dropped at the corpse for them (Aion:FieldBosses:SharedLoot).
    /// </summary>
    internal List<byte[]> FieldBossLoot(Target target, long now)
    {
        var rate = options.World?.DropRate ?? 1;
        var fraction = rate - Math.Floor(rate);
        var rolls = (int)Math.Floor(rate) + (fraction > 0 && rng.NextDouble() < fraction ? 1 : 0);
        var rolled = new List<(int, long)>();
        for (var i = 0; i < rolls; i++) rolled.AddRange(LootTables.Embedded.RollParticipant(target.NpcId, profileRace, rng));
        FieldBossShares++;
        FieldBossItemsRolled += rolled.Count;
        return rolled.Count == 0 ? [] : DropLootSolo(target, now, rolled, []);
    }

    /// <summary>Field boss kills by others this session rolled a share of, and the items those rolls gave (tests).</summary>
    public int FieldBossShares { get; private set; }
    public int FieldBossItemsRolled { get; private set; }
}
