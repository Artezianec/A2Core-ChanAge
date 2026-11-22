using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Config and helpers for the five Expedition finals' scripted mechanics (tools/data/expedition-boss-mechanics.json).</summary>
public static class ExpeditionBossMechanics
{
    public static readonly HashSet<int> BerkBossNpcIds = [2300171, 2310171, 2320171];
    public static readonly HashSet<int> AuldorBossNpcIds = [2300218, 2310218, 2320218];
    public static readonly HashSet<int> BakarmaBossNpcIds = [2300471, 2310471, 2320471];
    public static readonly HashSet<int> NuakumBossNpcIds = [2300910, 2310910, 2320910];

    static readonly Lazy<Doc> Configured = new(Load);

    sealed record Doc(
        [property: JsonPropertyName("berk_pillar_cover")] BerkCover? Berk,
        [property: JsonPropertyName("auldor_tempest_soak")] AuldorSoak? Auldor,
        [property: JsonPropertyName("bakarma_jump_rings")] BakarmaRings? Bakarma,
        [property: JsonPropertyName("nuakum_blue_light")] NuakumOrb? NuakumOrb,
        [property: JsonPropertyName("nuakum_hunt_clone_npc")] int NuakumCloneNpc,
        [property: JsonPropertyName("kromede_clone_line")] KromedeCloneLine? KromedeClone);

    public sealed record BerkCover(
        [property: JsonPropertyName("skill")] int Skill,
        [property: JsonPropertyName("cover_depth")] double CoverDepth,
        [property: JsonPropertyName("cover_half_width")] double CoverHalfWidth,
        [property: JsonPropertyName("pillar_offset_from_boss")] double PillarOffsetFromBoss,
        [property: JsonPropertyName("pillar_env_obj_id")] int? PillarEnvObjId);
    public sealed record AuldorSoak(
        [property: JsonPropertyName("stack_radius")] double StackRadius,
        [property: JsonPropertyName("outer_arena_inner_radius")] double OuterArenaInnerRadius,
        [property: JsonPropertyName("outer_dot_interval_ms")] int OuterDotIntervalMs,
        [property: JsonPropertyName("outer_dot_damage_min")] int OuterDotDamageMin,
        [property: JsonPropertyName("outer_dot_damage_max")] int OuterDotDamageMax);
    public sealed record BakarmaRings(
        [property: JsonPropertyName("skill")] int Skill,
        [property: JsonPropertyName("pulse_ms")] int PulseMs,
        [property: JsonPropertyName("rings")] RingBand[] Rings,
        [property: JsonPropertyName("dot_damage_min")] int DotDamageMin,
        [property: JsonPropertyName("dot_damage_max")] int DotDamageMax,
        [property: JsonPropertyName("arena_radius")] double ArenaRadius,
        [property: JsonPropertyName("edge_snap_radius")] double EdgeSnapRadius,
        [property: JsonPropertyName("whirlpool_radius")] double WhirlpoolRadius,
        [property: JsonPropertyName("whirlpool_interval_ms")] int WhirlpoolIntervalMs,
        [property: JsonPropertyName("whirlpool_damage_min")] int WhirlpoolDamageMin,
        [property: JsonPropertyName("whirlpool_damage_max")] int WhirlpoolDamageMax);
    public sealed record RingBand(
        [property: JsonPropertyName("safe_inner")] double SafeInner,
        [property: JsonPropertyName("safe_outer")] double SafeOuter);
    public sealed record NuakumOrb(
        [property: JsonPropertyName("orb_radius")] double OrbRadius,
        [property: JsonPropertyName("hits_required")] int HitsRequired,
        [property: JsonPropertyName("window_ms")] int WindowMs);
    public sealed record KromedeCloneLine(
        [property: JsonPropertyName("skill")] int Skill,
        [property: JsonPropertyName("every_ms")] int EveryMs);

    static Doc Load()
    {
        using var stream = typeof(ExpeditionBossMechanics).Assembly.GetManifestResourceStream("expedition-boss-mechanics.json")
            ?? throw new InvalidOperationException("Missing expedition-boss-mechanics.json");
        return JsonSerializer.Deserialize<Doc>(stream) ?? throw new InvalidOperationException("expedition-boss-mechanics.json invalid");
    }

    public static string? MechanicForPhase(string phaseName) => phaseName switch
    {
        "wave_of_fury" => "berk_pillar_cover",
        "tempest_soak" => "auldor_tempest_soak",
        "wave_of_death" => "bakarma_jump_rings",
        "hunt" => "nuakum_hunt",
        "blue_light" => "nuakum_blue_light",
        "clones" => "kromede_clones",
        _ => null,
    };

    public static Vector3d PillarAt(Target boss, Vector3d victim)
    {
        var cfg = Configured.Value.Berk ?? throw new InvalidOperationException("berk_pillar_cover missing");
        var flat = victim with { Z = boss.Position.Z };
        var dx = flat.X - boss.Position.X;
        var dy = flat.Y - boss.Position.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) { dx = 1; dy = 0; len = 1; }
        var o = cfg.PillarOffsetFromBoss;
        return new Vector3d(boss.Position.X + dx / len * o, boss.Position.Y + dy / len * o, boss.Position.Z);
    }

    public static bool InPillarCover(Target boss, Vector3d pillar, Vector3d player)
    {
        var cfg = Configured.Value.Berk!;
        var toBossX = boss.Position.X - pillar.X;
        var toBossY = boss.Position.Y - pillar.Y;
        var toPlayerX = player.X - pillar.X;
        var toPlayerY = player.Y - pillar.Y;
        var behind = toPlayerX * toBossX + toPlayerY * toBossY < 0;
        if (!behind) return false;
        var perp = Math.Abs(toPlayerX * toBossY - toPlayerY * toBossX) / Math.Max(1, Math.Sqrt(toBossX * toBossX + toBossY * toBossY));
        var depth = Math.Sqrt(toPlayerX * toPlayerX + toPlayerY * toPlayerY);
        return perp <= cfg.CoverHalfWidth && depth <= cfg.CoverDepth;
    }

    public static bool InOuterArenaDanger(Vector3d centre, Vector3d player) =>
        player.DistanceSquared(centre) > Math.Pow(Configured.Value.Auldor!.OuterArenaInnerRadius, 2);

    public static BossMechanicTelegraphEventArgs Telegraph(
        Target boss, string mechanicId, int skill, Vector3d centre, long startMs, long endMs, int[]? envIds = null) =>
        new(boss.NpcId, boss.Key, mechanicId, skill, centre, startMs, endMs, envIds);

    public static int NuakumCloneNpc() => Configured.Value.NuakumCloneNpc;
    public static KromedeCloneLine KromedeCloneConfig() => Configured.Value.KromedeClone ?? new KromedeCloneLine(1801050, 8000);
    public static BakarmaRings BakarmaConfig() => Configured.Value.Bakarma ?? throw new InvalidOperationException("bakarma_jump_rings missing");
    public static AuldorSoak AuldorConfig() => Configured.Value.Auldor ?? throw new InvalidOperationException("auldor_tempest_soak missing");
    public static NuakumOrb NuakumOrbConfig() => Configured.Value.NuakumOrb ?? throw new InvalidOperationException("nuakum_blue_light missing");
    public static int? PillarEnvObjId() => Configured.Value.Berk?.PillarEnvObjId;

    public static Vector3d ArenaEdge(Vector3d centre, Vector3d player, double radius)
    {
        var dx = player.X - centre.X;
        var dy = player.Y - centre.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) { dx = 1; dy = 0; len = 1; }
        var scale = radius / len;
        return new Vector3d(centre.X + dx * scale, centre.Y + dy * scale, player.Z);
    }
}