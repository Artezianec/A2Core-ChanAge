namespace Aion.World;

/// <summary>
/// SkillCond.dat <c>ESkillCondType</c> values the cast path enforces. Types not listed are logged once
/// (<c>skill_cond_unknown</c>) and treated as satisfied so new client enums do not hard-block casts.
/// </summary>
public static class SkillCondRegistry
{
    public const string HasAbnormalGroupId = "HasAbnormalGroupId";
    public const string HasAbnormalEffectType = "HasAbnormalEffectType";

    static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        HasAbnormalGroupId,
        HasAbnormalEffectType,
    };

    static readonly HashSet<string> LoggedTypes = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    /// <summary>Whether <paramref name="condType"/> is implemented in <see cref="GameSession.SkillCondMet"/>.</summary>
    public static bool IsKnownType(string? condType) =>
        condType is not null && KnownTypes.Contains(condType);

    /// <summary>Log an unknown <c>CondType</c> once; returns false so the caller may allow the cast.</summary>
    public static bool NoteUnknownType(string condType, int skillId = 0)
    {
        if (IsKnownType(condType)) return true;
        lock (Gate)
        {
            if (!LoggedTypes.Add(condType)) return false;
        }
        PlaytestDebug.Action("skill_cond_unknown", ("condType", condType), ("skillId", skillId),
            ("skill", skillId > 0 ? SkillEffectEngine.SkillName(skillId) : null));
        return false;
    }

    /// <summary>
    /// SkillCond.dat uses <c>Groggy</c> on rampage skills; the stagger break window applies <c>Stagger</c> on NPCs (CANDIDATE alias).
    /// </summary>
    public static bool EffectTypesMatch(string? required, string actual) =>
        string.Equals(required, actual, StringComparison.OrdinalIgnoreCase)
        || required is "Groggy" && actual.Equals("Stagger", StringComparison.OrdinalIgnoreCase)
        || required is "Stagger" && actual.Equals("Groggy", StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalize SkillCond requirement text for stagger reactive checks.</summary>
    public static bool RequiresStaggerWindow(SkillCondNeed cond) =>
        cond.Type.Equals(HasAbnormalEffectType, StringComparison.OrdinalIgnoreCase)
        && cond.AbnormalEffect is { Length: > 0 } effect
        && (effect.Equals("Stagger", StringComparison.OrdinalIgnoreCase)
            || effect.Equals("Groggy", StringComparison.OrdinalIgnoreCase));

    internal static void ResetLogForTests()
    {
        lock (Gate) LoggedTypes.Clear();
    }
}
