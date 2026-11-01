using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// EnvObj overlap recovery orbs (Basic_RecoveryOrb_01–03): ids 1000277 red (HP), 1000278 blue (MP),
/// 1000279 green (SP/stamina, best guess). Pickup ratios in tools/data/recovery-orbs.json.
/// </summary>
public static class RecoveryOrbs
{
    public const int EnvObjMonsterEnemy = 28;
    public const int WaveSkillCooldownMs = 8_000;

    /// <summary>Wave order: red, blue, green (matches official three-orb spawn).</summary>
    public static readonly int[] WaveEnvIds = [1_000_277, 1_000_278, 1_000_279];

    /// <summary>Boss skills that drop a three-orb wave in official stagger-boss capture (1603820 AoE telegraph family).</summary>
    public static readonly HashSet<int> WaveSkills =
    [
        1_603_820, 1_603_860, 1_603_861, 1_603_851,
    ];

    public sealed record PickupEffect(
        [property: JsonPropertyName("hp_ratio_permille")] int HpRatioPermille = 0,
        [property: JsonPropertyName("mp_ratio_permille")] int MpRatioPermille = 0,
        [property: JsonPropertyName("sp_ratio_permille")] int SpRatioPermille = 0);

    sealed record OrbDef(
        [property: JsonPropertyName("interact_range")] int InteractRange,
        [property: JsonPropertyName("collision_radius")] int CollisionRadius,
        [property: JsonPropertyName("lifetime_ms")] int LifetimeMs,
        [property: JsonPropertyName("pickup")] PickupEffect? Pickup = null);

    sealed record Doc([property: JsonPropertyName("orbs")] Dictionary<string, OrbDef> Orbs);

    static readonly Lazy<IReadOnlyDictionary<int, OrbDef>> Catalog = new(Load);

    static IReadOnlyDictionary<int, OrbDef> Load()
    {
        using var stream = typeof(RecoveryOrbs).Assembly.GetManifestResourceStream("recovery-orbs.json")
            ?? throw new InvalidOperationException("recovery-orbs.json embedded resource missing");
        var doc = JsonSerializer.Deserialize<Doc>(stream) ?? throw new InvalidOperationException("recovery-orbs.json invalid");
        return doc.Orbs.ToDictionary(kv => int.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
    }

    public static PickupEffect Pickup(int envObjId) =>
        Catalog.Value.TryGetValue(envObjId, out var def) && def.Pickup is { } p ? p : new PickupEffect(1200, 0, 0);

    public static int PickupRadius(int envObjId)
    {
        if (!Catalog.Value.TryGetValue(envObjId, out var def)) return 450;
        return Math.Max(def.InteractRange, def.CollisionRadius) * 10;
    }

    public static int LifetimeMs(int envObjId) =>
        Catalog.Value.TryGetValue(envObjId, out var def) ? def.LifetimeMs : 30_000;

    public static byte[] SpawnPacket(int key, int envObjId, Vector3d position, int originBossKey, long createTime, float yaw = 0) =>
        GamePackets.BossRecoveryOrbSpawn(key, envObjId, position, originBossKey, yaw, createTime);
}
