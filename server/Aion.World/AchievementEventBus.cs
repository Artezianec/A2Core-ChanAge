using System.Globalization;

namespace Aion.World;

/// <summary>A single achievement progress signal from gameplay code into <see cref="GameSession.Emit(AchievementEvent, long)"/>.</summary>
public readonly record struct AchievementEvent(string Type, int Add = 1, IReadOnlyList<string>? Keys = null, int? SetValue = null, IReadOnlyList<int>? TargetIds = null);

/// <summary>Typed matchers and the set of objective types that have server emitters (tools/achievement_coverage.py).</summary>
public static class AchievementEmitters
{
    /// <summary>Objective types whose count is a level or snapshot (max on login and when it changes).</summary>
    public static readonly HashSet<string> StateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PCLevel", "EquipItemLevel", "CraftMasteryLevel", "GatherMasteryLevel", "VehicleCollect", "VehicleCreatureBoardLevel",
        "VehicleTypeLevel", "SoulbindMax", "MonolithLevel", "SkinFirstCollectAny",
    };

    /// <summary>Types reached from domain code (Achieve/AchieveSet/Emit); maintained for coverage tests.</summary>
    public static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);

    internal static void Register(string type)
    {
        lock (Registered) Registered.Add(type);
    }

    /// <summary>Build-time list of objective types that have emitters (updated with hooks; used by coverage tests).</summary>
    public static IReadOnlySet<string> RegisteredTypes => Registered;

    /// <summary>Builds the per-entry filter for an event (v/n/i/e; default: any key in v or v empty).</summary>
    public static Func<AchievementTables.Entry, bool> Matcher(AchievementEvent evt)
    {
        var keys = evt.Keys;
        return evt.Type switch
        {
            "KillNpc" or "KillNpcByCritical" or "CloseNPC" => e => MatchIntField(e.Npcs, keys) && MatchValues(e, keys),
            "UseItem" or "GetItem" or "AcquireItem" => e => MatchIntField(e.Items, keys) && MatchValues(e, keys),
            "UseEnvObj" or "CloseEnvObj" or "UseEnvObjWithKey" => e => MatchIntField(e.Envs, keys) && MatchValues(e, keys),
            "ClearQuestType" => e => e.Values.Length == 0 || MatchValues(e, keys),
            "ClearDungeonType" => e => e.Values.Length > 0 && MatchValues(e, keys),
            "KillPCAbyssGrade" => e => e.Values.Length == 0 || keys is null || keys.Any(k => e.Values.Contains(k)),
            "EnchantLevelItem" or "EnchantLevelItemGrade" => e => e.Values.Length >= 1 && int.TryParse(e.Values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var need)
                && (evt.SetValue ?? evt.Add) >= need,
            "EnchantLevelEquipCategory" => e => e.Values.Length >= 2 && int.TryParse(e.Values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var need)
                && (evt.SetValue ?? evt.Add) >= need,
            "DecomposeItem" => e => e.Values.Length == 0 || keys is not null && keys.Any(e.Has),
            "CraftCategorySuccess" or "CraftSuccess" or "CraftComboSuccess" => e => e.Values.Length == 0 || MatchValues(e, keys),
            "ClearBossChallenge" => e => e.Values.Length >= 2 && keys is not null && keys.Count >= 2 && e.Has(keys[0])
                && int.TryParse(keys[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var need)
                && (evt.SetValue ?? evt.Add) >= need,
            "VehicleCreatureBoardLevel" => e => e.Values.Length == 0 || keys is not null && keys.Any(e.Has),
            "VehicleTypeLevel" => e => (e.Values.Length == 0 || keys is not null && keys.Any(e.Has))
                && (e.Values.Length < 2 || !int.TryParse(e.Values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var need)
                    || (evt.SetValue ?? evt.Add) >= need),
            "SoulbindMax" => e => e.Values.Length == 0,
            "SkinFirstCollectAny" => e => e.Values.Length == 0,
            "SkinSetFirstCollect" => e => MatchValues(e, keys),
            "PeriodCollectionTask" or "PeriodCollectionSupply" or "PeriodCollectionMisc" => e => MatchValues(e, keys),
            "GodstoneCombineResultGrade" or "EnchantGodStoneGrade" => e => e.Values.Length == 0 || MatchValues(e, keys),
            "ArcanaCreate" or "ArcanaExtract" => e => e.Values.Length == 0 || MatchValues(e, keys),
            "AbyssArtifactEventComplete" or "AbyssArtifactEventWin" => e => MatchValues(e, keys),
            "FieldEventRanking" => e => MatchValues(e, keys),
            "CompleteFieldEvent" => e => MatchValues(e, keys),
            "ArenaKill" or "ArenaAssist" or "ArenaDie" or "ArenaWin" => e => e.Values.Length == 0 || MatchValues(e, keys),
            "GetDungeonReward" => e => MatchValues(e, keys),
            "ClearAchievement" => e => keys is not null && keys.Any(k => e.Has(k)),
            "BattlePassExp" => e => evt.TargetIds is { Count: > 0 } ids && ids.Contains(e.Id),
            "KillNpcByCritical" or "KillNpcSubtype" => e => MatchValues(e, keys),
            "KillNpcDungeonType" or "KillNpcCreatureType" => e => MatchValues(e, keys),
            _ => e => MatchValues(e, keys),
        };
    }

    static bool MatchValues(AchievementTables.Entry entry, IReadOnlyList<string>? keys) =>
        entry.Values.Length == 0 || keys is null || keys.Count == 0 || keys.Any(entry.Has);

    static bool MatchIntField(int[]? ids, IReadOnlyList<string>? keys)
    {
        if (ids is not { Length: > 0 } || keys is null || keys.Count == 0) return true;
        foreach (var key in keys)
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && ids.Contains(id))
                return true;
        return false;
    }
}