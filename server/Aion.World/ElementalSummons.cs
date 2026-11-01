using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// ElementalSummon.dat rows (tools/data/elemental-summons.json): NpcList per elemental group drives
/// 0xe239 _elemental_group_type (EElementalSummonGroupType).
/// </summary>
public static class ElementalSummons
{
    sealed record Row(
        [property: JsonPropertyName("group")] string Group,
        [property: JsonPropertyName("deactivate_skill")] int DeactivateSkill,
        [property: JsonPropertyName("npcs")] int[] Npcs);

    sealed record File(
        [property: JsonPropertyName("enum")] Dictionary<string, int> Enum,
        [property: JsonPropertyName("by_npc")] Dictionary<string, int> ByNpc,
        [property: JsonPropertyName("rows")] Row[] Rows);

    static readonly Lazy<File> file = new(() =>
    {
        using var stream = typeof(ElementalSummons).Assembly.GetManifestResourceStream("elemental-summons.json")
            ?? throw new InvalidOperationException("Missing embedded game data elemental-summons.json");
        return JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty game data elemental-summons.json");
    });

    static string Id(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>EElementalSummonGroupType for a spirit NpcData id; 0 when unknown.</summary>
    public static byte GroupTypeForNpc(int npcId) =>
        file.Value.ByNpc.GetValueOrDefault(Id(npcId)) is int value and > 0 and <= byte.MaxValue ? (byte)value : (byte)0;

    /// <summary>Named group -> enum byte (Skill.dat CanUseWhenElementalSummonSpawned strings).</summary>
    public static byte GroupType(string group) =>
        group.Length > 0 && file.Value.Enum.TryGetValue(group, out var value) && value is > 0 and <= byte.MaxValue ? (byte)value : (byte)0;

    public static byte GroupTypeForDismissSkill(int skillId)
    {
        foreach (var row in file.Value.Rows)
            if (row.DeactivateSkill == skillId) return GroupType(row.Group);
        return 0;
    }

    /// <summary>Permanent player link abnormal while a spirit is out (capture 000220: 162900011 for Water / dismiss 16290001).</summary>
    public static int PlayerSpiritLinkAbnormal(byte group)
    {
        foreach (var row in file.Value.Rows)
            if (GroupType(row.Group) == group) return row.DeactivateSkill * 10 + 1;
        return 0;
    }

    public static bool IsPlayerSpiritLinkAbnormal(int abnormalId) =>
        file.Value.Rows.Any(r => r.DeactivateSkill * 10 + 1 == abnormalId);

    /// <summary>Permanent spirit abnormal on 0x3641 (official 2026-11-01 capture).</summary>
    public static int SpiritAbnormalId(int npcId) => npcId switch
    {
        2920101 => 0x0998_aa41,
        2920121 => 0x099a_30e1,
        2920141 => 0x099c_b681,
        2920161 => 0x099e_5c21,
        _ => 0x0998_aa41,
    };

    /// <summary>Spawn tag u32 on summon 0x3641 tail (official Fire/Water captures).</summary>
    public static uint SpiritSpawnTag(int npcId) => npcId switch
    {
        2920101 => 0x0003_1dd7,
        2920121 => 0x0003_1ddf,
        2920141 => 0x0003_1de7,
        2920161 => 0x0003_1def,
        _ => 0x0003_1dd7,
    };
}
