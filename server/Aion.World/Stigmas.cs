using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Stigmas (tools/data/stigmas.json, tools/stigmas.py, docs/STIGMAS.md): each class's stigma skills, the slots per character level,
/// the Stigma Shards a character has had at its level and what each stigma level costs. Ported from ckrealm/A2Core (friends-server,
/// AGPL-3.0). The skill lists, 13-per-class count, slot levels 22/27/32/37, cap 20 and the 1/2/4/8 cost ladder are CONFIRMED
/// (global). Levelling shard totals are CANDIDATE until Exp.dat BonusStigmaPoint / StigmaSkillContextSlotMax are exported.
/// </summary>
public sealed class StigmaTable
{
    public sealed record CostBand([property: JsonPropertyName("from")] int From, [property: JsonPropertyName("to")] int To,
        [property: JsonPropertyName("shards")] int Shards);

    sealed record Params(
        [property: JsonPropertyName("unlock_level")] int UnlockLevel,
        [property: JsonPropertyName("slot_levels")] int[] SlotLevels,
        [property: JsonPropertyName("max_skill_level")] int MaxSkillLevel,
        [property: JsonPropertyName("costs")] CostBand[] Costs,
        [property: JsonPropertyName("ascension_shards")] int AscensionShards,
        [property: JsonPropertyName("shards_per_level")] CostBand[] ShardsPerLevel);

    sealed record Generated(
        [property: JsonPropertyName("classes")] Dictionary<string, int[]> Classes,
        [property: JsonPropertyName("skills")] Dictionary<string, SkillDefinition>? Skills);

    sealed record File([property: JsonPropertyName("params")] Params Params, [property: JsonPropertyName("generated")] Generated Generated);

    readonly File file;
    readonly Dictionary<int, string> owner;

    StigmaTable(File file)
    {
        this.file = file;
        owner = file.Generated.Classes.SelectMany(c => c.Value.Select(id => (id, c.Key))).ToDictionary(p => p.id, p => p.Key);
    }

    /// <summary>The level the stigma tab opens at (22: every Stigma row's NeedCharacterLevel).</summary>
    public int UnlockLevel => file.Params.UnlockLevel;

    /// <summary>The highest stigma level shards can buy (20: the last Stigma row).</summary>
    public int MaxSkillLevel => file.Params.MaxSkillLevel;

    /// <summary>The most slots any setting can use (the length of slot_levels: 6, Korea's count).</summary>
    public int MaxSlots => file.Params.SlotLevels.Length;

    public IReadOnlyCollection<string> Classes => file.Generated.Classes.Keys;

    /// <summary>The class's stigma skills (empty for a class without any).</summary>
    public IReadOnlyList<int> ClassStigmas(string? className) =>
        className is not null && file.Generated.Classes.TryGetValue(className, out var ids) ? ids : [];

    /// <summary>A stigma's cast data: recovered name, cooldown, cost and weapons; OURS damage (none for self stigmas).</summary>
    public SkillDefinition? Skill(int skillId) =>
        file.Generated.Skills?.GetValueOrDefault(skillId.ToString(CultureInfo.InvariantCulture));

    public bool IsStigma(int skillId) => owner.ContainsKey(skillId);

    public bool IsStigmaOf(string? className, int skillId) =>
        className is not null && owner.TryGetValue(skillId, out var cls) && string.Equals(cls, className, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stigma slots open at a character level, of at most <paramref name="slots"/>.</summary>
    public int SlotsAt(int level, int slots) => file.Params.SlotLevels.Take(Math.Clamp(slots, 0, MaxSlots)).Count(l => level >= l);

    /// <summary>The Stigma Shards a character has had in all by its level from levelling (before the rate): the Ascension shard at the
    /// unlock level, then shards_per_level for every level of a band reached.</summary>
    public int ShardsAt(int level)
    {
        if (level < UnlockLevel) return 0;
        var total = file.Params.AscensionShards;
        foreach (var band in file.Params.ShardsPerLevel)
            if (level >= band.From) total += (Math.Min(level, band.To) - band.From + 1) * band.Shards;
        return total;
    }

    /// <summary>The shards one stigma level costs (level 1: learning it); null above the ceiling.</summary>
    public int? Cost(int stigmaLevel) =>
        file.Params.Costs.FirstOrDefault(b => stigmaLevel >= b.From && stigmaLevel <= b.To)?.Shards;

    /// <summary>The shards a stigma at this level has cost in all (0 when not learned).</summary>
    public int Spent(int stigmaLevel) => Enumerable.Range(1, Math.Max(0, stigmaLevel)).Sum(l => Cost(l) ?? 0);

    public static StigmaTable Parse(string json) =>
        new(JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty stigmas.json"));

    static readonly Lazy<StigmaTable> embedded = new(() =>
    {
        using var stream = typeof(StigmaTable).Assembly.GetManifestResourceStream("stigmas.json")
            ?? throw new InvalidOperationException("Missing embedded game data stigmas.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static StigmaTable Embedded => embedded.Value;
}

/// <summary>
/// Aion:Stigmas (docs/STIGMAS.md). Off by default: the stigma messages are CANDIDATE (no official stigma purchase has been
/// captured), so a server turns them on to test. Enabled: a character from level 22 buys its class's stigma skills with Stigma
/// Shards through the skill window's messages and casts them only from a stigma slot. A reset costs ResetKinah (default 100,000;
/// 0 is free) and refunds the spent shards. Off: the stigma skills stay unlearnable, exactly as before.
/// </summary>
public sealed record StigmaOptions
{
    public bool Enabled { get; init; }
    /// <summary>Stigma slots (0 to 6): 4 matches global, 6 Korea.</summary>
    public int Slots { get; init; } = 4;
    /// <summary>Multiplies the shards from levelling (0 to 100). Shop and staff bonus shards are stored separately and are not multiplied.</summary>
    public double ShardRate { get; init; } = 1;
    /// <summary>Highest stigma level (1 to 20).</summary>
    public int MaxLevel { get; init; } = 20;
    /// <summary>
    /// Kinah charged for one stigma reset (one skill or all). Official KR launch dialog (oHzyEGdzZso, Nov 2025) showed 100,000
    /// and a full shard refund. A later KR/TW video (8HgwN6H758A, Sep 2026) claimed reset is free: set this to 0 for that. The
    /// same amount is charged for a single-skill reset and a reset-all (no separate official all-cost captured).
    /// </summary>
    public long ResetKinah { get; init; } = 100_000;
    /// <summary>A stigma casts only while it is on a quick slot; turn off if the client turns out to register stigmas another way.</summary>
    public bool RequireSlot { get; init; } = true;
    /// <summary>
    /// What 0x5105 carries for type StigmaShard: true (default) the shards spent, like the OBSERVED skill-point update (the client
    /// adds its own Exp.dat total); false the shards left. CANDIDATE either way.
    /// </summary>
    public bool ShardUpdateSendsSpent { get; init; } = true;

    public static StigmaOptions Default { get; } = new();

    public StigmaOptions Validated(StigmaTable? table = null)
    {
        table ??= StigmaTable.Embedded;
        if (Slots < 0 || Slots > table.MaxSlots) throw new InvalidOperationException($"Aion:Stigmas:Slots must be between 0 and {table.MaxSlots} (got {Slots}).");
        if (ShardRate is < 0 or > 100 || double.IsNaN(ShardRate)) throw new InvalidOperationException($"Aion:Stigmas:ShardRate must be between 0 and 100 (got {ShardRate}).");
        if (MaxLevel < 1 || MaxLevel > table.MaxSkillLevel) throw new InvalidOperationException($"Aion:Stigmas:MaxLevel must be between 1 and {table.MaxSkillLevel} (got {MaxLevel}).");
        if (ResetKinah < 0) throw new InvalidOperationException($"Aion:Stigmas:ResetKinah must be 0 or more (got {ResetKinah}).");
        return this;
    }

    public override string ToString() => Enabled
        ? string.Create(CultureInfo.InvariantCulture, $"stigmas on: {Slots} slots, shards x{ShardRate}, levels to {MaxLevel}, reset {ResetKinah} kinah, slot needed to cast {(RequireSlot ? "yes" : "no")}")
        : "stigmas off";
}
