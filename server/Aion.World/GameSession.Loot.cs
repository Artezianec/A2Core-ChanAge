using Aion.Protocol.Native;

namespace Aion.World;

// Monster loot. A kill rolls the monster's reward tables (LootTables); the drops lie on the ground around the
// corpse for the killer (0x561e DropItem_NT, owner Player), who picks them up with 0x5620 ItemLooting_RQ:
// the items are granted (stored), answered 0x5621 ItemLooting_RS per drop, removed with 0x561f
// DropItemRemove_NT, and auto-use items (pet souls) are used at once. Unlooted drops vanish after
// DropLifetimeMs. Opcodes: catalogue 340-343 + 0x54ca (the 0x56 run, OBSERVED around 0x5610/0x5612/0x5646);
// layouts are the usmap structs, no official sample (CANDIDATE).
// OURS on top (ckrealm/A2Core): monsters without a reward table roll the generic level table (Aion:Rules:FallbackLoot),
// every monster pays Kinah (MonsterRewards), and a party member's kill is shared with the members nearby
// (Aion:Rules:PartyLoot): the killer's part lies on the ground as before, the others' part is granted straight to their
// bags on their own loops; Kinah is split evenly.
public sealed partial class GameSession
{
    static WorldRules hostWorldRules = WorldRules.Default;
    public static void ConfigureWorldRules(WorldRules rules) => hostWorldRules = rules.Validated();
    WorldRules WorldRules => options.Rules ?? hostWorldRules;
    /// <summary>
    /// A drop's first field is its source, the entity it dropped from (u32, OBSERVED: one chest's nine drops share it). GroundDrop.Key holds that source.
    /// </summary>
    internal sealed record GroundDrop(int Key, long Uid, int ItemId, long Count, Vector3d Center, Vector3d At, long ExpiresAt,
        byte OwnerType = 2, long Owner = 0);

    readonly Dictionary<long, GroundDrop> drops = [];
    int dropCounter;
    /// <summary>Official drop ownership expiry: 120 s after the kill (capture 20261124-130131 #3188: exp - packet time 120047 ms).</summary>
    public const int DropLifetimeMs = 120_000;
    /// <summary>A drop is picked up within this distance (cm), with room for latency.</summary>
    public const double LootRange = 1500;
    const byte IncreaseDropItemGet = 5;   // EIncreaseReason kDropItemGet
    public IReadOnlyCollection<long> GroundDrops => drops.Keys;

    internal void RegisterGroundDrop(GroundDrop drop) => drops[drop.Uid] = drop;
    internal void RemoveGroundDrop(long uid) => drops.Remove(uid);

    /// <summary>The loot of a killed monster: the drops for the killer (0x561e), or nothing; party members' shares go to them.</summary>
    List<byte[]> DropLoot(Target target, long now)
    {
        // The base drop rate (Aion:Rates:Drop) times drop events (SharedWorld.DropRate) rolls the table more often.
        // Kinah lines in reward tables and optional monster Kinah use Aion:Rates:Kinah.
        var rolls = options.Rates.LootRolls(options.World?.DropRate ?? 1, rng);
        var rules = WorldRules;
        var generic = rules.FallbackLoot && !LootTables.Embedded.HasLoot(target.NpcId);
        var rolled = new List<(int, long)>();
        for (var i = 0; i < rolls; i++)
            rolled.AddRange(generic ? MonsterRewards.Embedded.Fallback(target.SubType, target.Level, rng) : LootTables.Embedded.Roll(target.NpcId, profileRace, rng));
        rolled = options.Rates.WithKinah(rolled);
        var kinah = rules.MonsterKinahDrops
            ? options.Rates.KinahAmount(MonsterRewards.Embedded.Kinah(target.SubType, target.Level, rng))
            : 0L;
        var mine = rules.SharesPartyLoot
            ? ShareLootWithParty(rolled, kinah, target.Position)
            : AppendKinah(rolled, kinah);
        if (mine.Count == 0) return [];
        if (Social is { } social && worldPlayer is not null && social.PartyOf(worldPlayer) is { } party)
        {
            var force = social.ForceOf(worldPlayer);
            if (force is not null && force.Parties.Sum(p => p.Members.Count) > 1)
                return DropLootParty(party, target, now, mine, []);
            if (party.Members.Count > 1)
                return DropLootParty(party, target, now, mine, []);
        }
        return DropLootSolo(target, now, mine, []);
    }

    static List<(int ItemId, long Count)> AppendKinah(List<(int, long)> rolled, long kinah)
    {
        var mine = new List<(int ItemId, long Count)>(rolled.Count + (kinah > 0 ? 1 : 0));
        foreach (var (item, count) in rolled) mine.Add((item, count));
        if (kinah > 0) mine.Add((KinaItemId, kinah));
        return mine;
    }

    /// <summary>
    /// Aion:Rules:PartyLoot over a kill's items and Kinah among the killer and its party members nearby (NearbyPartyMembers,
    /// as the experience share). The other members' parts are granted on their loops (ShareLoot); the killer's part is returned.
    /// </summary>
    internal List<(int ItemId, long Count)> ShareLootWithParty(List<(int ItemId, long Count)> items, long kinah, Vector3d? killSite = null)
    {
        var rule = WorldRules.LootRule!.Value;
        var members = rule == PartyLootRule.Killer || worldPlayer is null ? [] : NearbyPartyMembers();
        var mine = new List<(int ItemId, long Count)>();
        if (members.Count == 0)
        {
            mine.AddRange(items);
            if (kinah > 0) mine.Add((KinaItemId, kinah));
            return mine;
        }
        var everyone = new List<WorldPlayer>(members.Count + 1) { worldPlayer! };
        everyone.AddRange(members);
        var parts = everyone.ToDictionary(p => p, _ => new List<(int ItemId, long Count)>());
        var party = Social?.PartyOf(worldPlayer!);
        foreach (var item in items)
        {
            var to = rule == PartyLootRule.RoundRobin && party is not null ? party.NextLooter(everyone) : everyone[rng.Next(everyone.Count)];
            parts[to].Add(item);
        }
        var shares = MonsterRewards.Split(kinah, everyone.Count);
        for (var i = 0; i < everyone.Count; i++)
            if (shares[i] > 0) parts[everyone[i]].Add((KinaItemId, shares[i]));
        foreach (var member in members)
            if (parts[member].Count > 0) member.Session.ShareLoot(parts[member], killSite ?? position ?? Vector3d.Zero);
        return parts[worldPlayer!];
    }

    /// <summary>
    /// Loot a party member's kill gave this player: granted on this player's own loop; if the bag cannot take it, it lies on
    /// the ground at <paramref name="killSite"/> for this player.
    /// </summary>
    public void ShareLoot(IReadOnlyList<(int ItemId, long Count)> items, Vector3d killSite, CancellationToken ct = default) =>
        _ = InvokeAsync(async now => (true, await GrantSharedLootAsync(items, killSite, now, ct)));

    async Task<List<byte[]>> GrantSharedLootAsync(IReadOnlyList<(int ItemId, long Count)> items, Vector3d killSite, long now, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        if (dead) return DropSharedLoot(items, killSite, now);
        var replies = new List<byte[]>();
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
            if (granted.Count == 0) return DropSharedLoot(items, killSite, now);
            replies.AddRange(await InventoryChangesAsync(ct, IncreaseDropItemGet, 0));
            replies.AddRange(await AutoUseAcquiredItemsAsync(items.Select(i => i.ItemId), now, ct));
        }
        return replies;
    }

    /// <summary>A share the bag could not take: on the ground at the kill site for this player.</summary>
    List<byte[]> DropSharedLoot(IReadOnlyList<(int ItemId, long Count)> items, Vector3d killSite, long now) =>
        DropLootSolo(new Target { Key = 0, Name = "", HpMax = 1, Position = killSite, NpcId = 0, Level = 1 }, now, [.. items], []);

    /// <summary>0x5620 ItemLooting_RQ {array of drop uids (i64), auto looting bool}.</summary>
    async Task<List<byte[]>> LootAsync(byte[] plain, long now, CancellationToken ct)
    {
        var uids = new List<long>();
        bool auto;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var count = reader.Varint();
            for (var i = 0; i < count && i < 64; i++) uids.Add((long)reader.U64());
            auto = reader.U8() != 0;
        }
        catch (ProtocolException) { return []; }
        var replies = new List<byte[]>();
        var taken = new List<GroundDrop>();
        foreach (var uid in uids.Distinct())
        {
            var ok = drops.TryGetValue(uid, out var drop) && now < drop.ExpiresAt && !dead
                && (position is not { } p || (p.X - drop.At.X) * (p.X - drop.At.X) + (p.Y - drop.At.Y) * (p.Y - drop.At.Y) <= LootRange * LootRange)
                && CanPickupDrop(drop);
            if (ok) taken.Add(drop!);
            else replies.Add(LootPackets.LootReply(1, uid, auto));
        }
        if (taken.Count == 0) return replies;
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, taken.Select(d => (d.ItemId, d.Count)).ToList(), ct) ? [[]] : []);
            if (granted.Count == 0) return [.. replies, .. taken.Select(d => LootPackets.LootReply(1, d.Uid, auto))];
        }
        foreach (var d in taken)
        {
            drops.Remove(d.Uid);
            RemoveSharedDrop(d.Uid);
        }
        replies.AddRange(taken.Select(d => LootPackets.LootReply(0, d.Uid, auto)));
        var removed = taken.Select(d => (d.Key, d.Uid, (long)(selected?.WireId ?? playerKey), auto)).ToList();
        replies.Add(LootPackets.DropsRemoved(removed));
        BroadcastDropRemoved(removed);
        var inventory = await InventoryChangesAsync(ct, IncreaseDropItemGet, 0);
        var autoUse = await AutoUseAcquiredItemsAsync(taken.Select(d => d.ItemId), now, ct);
        // Official solo pickup order after 0x5621: 0x561f, 0x561b, then 0x5616 when auto-use applies.
        replies.AddRange(inventory);
        replies.AddRange(autoUse);
        return replies;
    }

    /// <summary>Drops nobody took vanish (0x561f).</summary>
    List<byte[]> DropTick(long now)
    {
        if (drops.Count == 0) return [];
        var gone = drops.Values.Where(d => now >= d.ExpiresAt).ToList();
        if (gone.Count == 0) return [];
        foreach (var d in gone) drops.Remove(d.Uid);
        var expired = gone.Select(d => (d.Key, d.Uid, 0L, false)).ToList();
        BroadcastDropRemoved(expired);
        return [LootPackets.DropsRemoved(expired)];
    }
}
