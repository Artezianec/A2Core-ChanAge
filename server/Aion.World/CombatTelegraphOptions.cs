using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Combat:GroundMarkers (docs/GROUND-TELEGRAPHS.md): client ground AoE decals during NPC and player area casts.
/// </summary>
public sealed record CombatTelegraphOptions
{
    /// <summary>Send SkillRangeNoticeFilterLocation_NT (wire 0x3848, CANDIDATE) for area and ground-targeted skills.</summary>
    public bool GroundMarkers { get; init; } = true;

    public static CombatTelegraphOptions Default { get; } = new();

    public CombatTelegraphOptions Validated() => this;

    public override string ToString() => GroundMarkers ? "ground AoE telegraphs on" : "ground AoE telegraphs off";
}