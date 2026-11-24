using System.Globalization;

namespace Aion.World;

/// <summary>
/// Side tables (skill-areas, pc-zones, ported abnormals, …) key mostly on mastery skill ids; with specialization parts
/// equipped the client casts child ids from specialized-skills.json.
/// </summary>
public static class SkillSideData
{
    public static int Family(int skillId) => skillId / 10_000 * 10_000;

    /// <summary>Mastery parent from SpecializedSkill.dat, else the family id when this is a variant child.</summary>
    public static int? DataParent(int skillId)
    {
        if (SpecializedSkillTables.Embedded.ParentOf(skillId) is { } parent) return parent;
        var family = Family(skillId);
        return family != skillId ? family : null;
    }

    /// <summary>Keys to try when a side table has no row for the cast id (exact, specialization parent, family).</summary>
    public static IEnumerable<int> LookupKeys(int skillId)
    {
        yield return skillId;
        if (DataParent(skillId) is { } parent && parent != skillId)
            yield return parent;
        var family = Family(skillId);
        if (family != skillId && family != DataParent(skillId))
            yield return family;
    }

    static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);

    public static T? FirstHit<T>(int skillId, Func<string, T?> tryGet) where T : class
    {
        foreach (var key in LookupKeys(skillId))
        {
            var hit = tryGet(Key(key));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// CANDIDATE: child SkillEffect ids follow castSkill*100 + parentEffect%100 (OBSERVED 15060010 -> 1506001011).
    /// </summary>
    public static int WireEffectId(int effectId, int castSkillId)
    {
        var parent = DataParent(castSkillId) ?? Family(castSkillId);
        if (parent == castSkillId || parent == 0) return effectId;
        return castSkillId * 100 + effectId % 100;
    }
}