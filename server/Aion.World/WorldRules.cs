using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Rules (OURS): optional host switches for loot sharing and fallback tables. All are off by default so production
/// behaviour matches the pre-port server (client party loot modes only). Ported from ckrealm/A2Core.
/// Kinah scaling uses <see cref="ServerRates"/> (Aion:Rates:Kinah), not this section.
/// </summary>
public sealed record WorldRules
{
    /// <summary>off (default) | freeforall | roundrobin | killer. Off: no server-side kill sharing among party members.</summary>
    public string PartyLoot { get; init; } = "off";
    public bool FallbackLoot { get; init; }
    /// <summary>When true, every kill may grant Kinah from kinah-drops.json (scaled by Aion:Rates:Kinah).</summary>
    public bool MonsterKinahDrops { get; init; }

    public static WorldRules Default { get; } = new();

    /// <summary>The PartyLoot setting read (case and spaces ignored), or null when off/disabled.</summary>
    public static PartyLootRule? ParseLootRule(string? text) => (text ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "") switch
    {
        "" or "off" or "none" or "disabled" => null,
        "freeforall" => PartyLootRule.FreeForAll,
        "roundrobin" => PartyLootRule.RoundRobin,
        "killer" => PartyLootRule.Killer,
        _ => null,
    };

    /// <summary>Active party loot rule, or null when server-side sharing is disabled.</summary>
    public PartyLootRule? LootRule => ParseLootRule(PartyLoot);

    public bool SharesPartyLoot => LootRule is not null;

    public WorldRules Validated()
    {
        var key = (PartyLoot ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        if (key is not ("" or "off" or "none" or "disabled") && LootRule is null)
            throw new InvalidOperationException(
                $"Aion:Rules:PartyLoot must be \"off\", \"freeforall\", \"roundrobin\" or \"killer\" (got \"{PartyLoot}\").");
        return this;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"party loot {(LootRule?.ToString().ToLowerInvariant() ?? "off")}, fallback loot {(FallbackLoot ? "on" : "off")}, monster Kinah {(MonsterKinahDrops ? "on" : "off")}");
}