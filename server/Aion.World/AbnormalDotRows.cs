using System.Globalization;

namespace Aion.World;

/// <summary>Damage-over-time rows from SkillAbnormalEffect exports (skill-areas and ported tables).</summary>
public static class AbnormalDotRows
{
    public static IReadOnlyList<PortedSkillAbnormals.ScaledDot> ScaledDots(int abnormalId)
    {
        var list = new List<PortedSkillAbnormals.ScaledDot>(PortedSkillAbnormals.ScaledDots(abnormalId));
        foreach (var (id, type, values) in AbnormalSpecs.Info(abnormalId)?.Effects ?? [])
        {
            if (!type.Equals("Dot_NormalCalc", StringComparison.OrdinalIgnoreCase)) continue;
            int V(int i) => i < values.Count && int.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (V(0) <= 0) continue;
            if (list.Any(d => d.EffectId == id)) continue;
            list.Add(new PortedSkillAbnormals.ScaledDot(id, V(0), V(3), V(4), V(5), V(6)));
        }
        return list;
    }
}