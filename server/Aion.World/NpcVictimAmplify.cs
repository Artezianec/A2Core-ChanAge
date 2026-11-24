namespace Aion.World;

/// <summary>
/// Damage taken by an NPC from a player hit: PvEAmplifyDamage / PvPAmplifyDamage StatChange rows on its debuffs
/// (Debilitating Mark 170800001: -500 PvE, -250 PvP on the client export).
/// </summary>
public static class NpcVictimAmplify
{
    public static int DebuffPermille(int abnormalId, bool pvp)
    {
        var stat = pvp ? "PvPAmplifyDamage" : "PvEAmplifyDamage";
        var sum = 0;
        foreach (var (_, type, values) in AbnormalSpecs.Info(abnormalId)?.Effects ?? [])
        {
            if (!type.Equals("StatChange", StringComparison.OrdinalIgnoreCase) || values.Count < 2) continue;
            if (!values[0].Equals(stat, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(values[1], out var amount)) continue;
            if (amount < 0) sum += -amount;
        }
        return sum;
    }
}