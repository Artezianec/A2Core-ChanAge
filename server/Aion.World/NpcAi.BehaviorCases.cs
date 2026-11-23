using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// NpcBehaviorCase hooks: client talk lines on combat start, skill cast/use, death (Caption / PlayNpcTalk).
/// HP-threshold phases remain in boss-phases.json; overlays and behavior cases stack.
/// </summary>
public sealed partial class NpcAi
{
    public NpcBehaviorCasesIndex BehaviorCases { get; set; } = NpcBehaviorCases.Embedded;

    readonly Dictionary<int, HashSet<string>> behaviorOnce = new();

    static string FireKey(string caseKind, int? skill, int? abnormal) =>
        $"{caseKind}:{skill ?? 0}:{abnormal ?? 0}";

    bool MarkOnce(Target target, string key)
    {
        if (!behaviorOnce.TryGetValue(target.Key, out var set))
            behaviorOnce[target.Key] = set = [];
        if (!set.Add(key)) return false;
        return true;
    }

    void ClearBehavior(Target target) => behaviorOnce.Remove(target.Key);

    static NpcBehaviorCases.CaseEntry? Pick(Random rng, IReadOnlyList<NpcBehaviorCases.CaseEntry> candidates)
    {
        if (candidates.Count == 0) return null;
        var total = candidates.Sum(c => Math.Max(0, c.Prob));
        if (total <= 0) return candidates[0];
        var roll = rng.Next(total);
        foreach (var c in candidates)
        {
            var w = Math.Max(0, c.Prob);
            if (roll < w) return c;
            roll -= w;
        }
        return candidates[^1];
    }

    List<byte[]> BehaviorTalk(Target target, string caseKind, long now, bool once, int? skill = null, int? abnormal = null)
    {
        var matches = BehaviorCases.Matching(target, caseKind, skill, abnormal);
        if (matches.Count == 0) return [];
        var key = FireKey(caseKind, skill, abnormal);
        if (once && !MarkOnce(target, key)) return [];
        var entry = Pick(rng, matches);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Talk)) return [];
        return [GamePackets.Caption(entry.Talk)];
    }

    internal List<byte[]> BehaviorOnCombatStart(Target target, long now)
    {
        InitMonsterParts(target);
        return BehaviorTalk(target, "combat_start", now, once: true);
    }

    internal List<byte[]> BehaviorOnCasting(Target target, int skillId, long now) =>
        BehaviorTalk(target, "casting", now, once: false, skill: skillId);

    internal List<byte[]> BehaviorOnSkillUse(Target target, int skillId, long now) =>
        BehaviorTalk(target, "skill_use", now, once: false, skill: skillId);

    internal List<byte[]> BehaviorOnDie(Target target, long now)
    {
        ClearBehavior(target);
        return BehaviorTalk(target, "die", now, once: true);
    }

    internal List<byte[]> BehaviorOnAbnormal(Target target, int abnormalId, long now) =>
        BehaviorTalk(target, "abnormal", now, once: false, abnormal: abnormalId);

    void InitMonsterParts(Target target)
    {
        var parts = BehaviorCases.PartsFor(target.NpcId);
        if (parts.Count == 0) return;
        // Breakable part HP budget (client MonsterPartsData); wire to UI when part breaks land.
        var total = parts.Sum(p => p.MaxHp);
        if (total > 0 && target.StaggerGaugeMax <= 0)
            target.StaggerGaugeMax = Math.Max(target.StaggerGaugeMax, total);
    }
}