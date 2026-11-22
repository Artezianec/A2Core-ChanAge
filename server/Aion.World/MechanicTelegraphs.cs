using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Ground AoE marker packets for SkillEffectFilter shapes (tools/data/skill-areas.json).
/// Wire: SkillRangeNoticeFilterLocation_NT, CANDIDATE live opcode 0x3848 (dev catalogue 0x36F1 + 0x157, same run as 0x3847 cooltime).
/// </summary>
public static class MechanicTelegraphs
{
  public const ushort RangeNoticeFilterLocationNt = 0x3848;

  /// <summary>Arguments when a marker is placed (boss scripts and map events can react or append packets).</summary>
  public sealed record Started(
      Target Caster, int SkillId, byte ActionUid, int EffectSeq, Vector3d Centre, double YawDegrees, int DurationMs, long AtMs);

  /// <summary>Whether this cast should show a ground decal from client skill area data.</summary>
  public static bool WantsMarker(SkillAreas.Area? area) =>
      area is { IsArea: true } or { IsGround: true };

  /// <summary>Milliseconds from marker send until the first damage row (NPC slam / monster hit).</summary>
  public static int NpcLandMs(NpcSkill skill) => Math.Max(0, skill.Cast) + NpcAi.HitDelayMs;

  /// <summary>Milliseconds from marker send until the first player damage row.</summary>
  public static int PlayerLandMs(SkillDefinition definition) =>
      definition.Damage.Length > 0 ? SkillTiming.HitLandMs(definition, 0) : definition.CastingMs + NpcAi.HitDelayMs;

  /// <summary>Ground placement for the filter: on-target and GroundType Location use <paramref name="aim"/>, else the caster.</summary>
  public static Vector3d EffectCentre(SkillAreas.Area area, Vector3d caster, Vector3d aim) =>
      area.OnTarget || area.Ground == "Location" ? aim : caster;

  public static byte[] RangeNotice(int casterKey, int skillId, byte actionUid, int effectSeq, Vector3d centre, double yawDegrees,
      int durationMs, int processUid = 0) =>
      PlayerPackets.SkillRangeNoticeFilterLocation(casterKey, skillId, actionUid, effectSeq, processUid, centre, (float)yawDegrees, durationMs);

  /// <summary>Build the marker packet when enabled; optionally invoke <paramref name="started"/> for hooks.</summary>
  public static List<byte[]> Place(CombatTelegraphOptions rules, Started args, Func<Started, IReadOnlyList<byte[]>>? started = null)
  {
      if (!rules.GroundMarkers) return [];
      var extra = started?.Invoke(args) ?? [];
      return [RangeNotice(args.Caster.Key, args.SkillId, args.ActionUid, args.EffectSeq, args.Centre, args.YawDegrees, args.DurationMs), ..extra];
  }

  /// <summary>CANDIDATE: zero duration may clear a marker early when a cast is broken (no official capture).</summary>
  public static byte[]? Clear(CombatTelegraphOptions rules, int casterKey, int skillId, byte actionUid, Vector3d centre, double yawDegrees)
  {
      if (!rules.GroundMarkers) return null;
      return RangeNotice(casterKey, skillId, actionUid, effectSeq: 0, centre, yawDegrees, durationMs: 0);
  }

  internal static List<byte[]> ForNpcArea(
      CombatTelegraphOptions rules, Target caster, int skillId, byte actionUid, SkillAreas.Area? area,
      Vector3d aim, double yawDegrees, int durationMs, long atMs, Func<Started, IReadOnlyList<byte[]>>? hook)
  {
      if (area is null || !WantsMarker(area)) return [];
      var centre = EffectCentre(area, caster.Position, aim);
      return Place(rules, new Started(caster, skillId, actionUid, 1, centre, yawDegrees, durationMs, atMs), hook);
  }
}