namespace Aion.World;

// NPC skills through the skill effect engine (docs/SKILL-EFFECT-ENGINE.md): a Damage row whose calc type is MaxHPRatio /
// CurrentHPRatio (tools/data/skill-effect-engine.json ratio_damage) hits for that share of the victim's HP instead of the
// level curve (the 0-damage world-npcs rows: Bakarma's 1800655 is 500% of the maximum). Defense does not reduce it (OURS).
public sealed partial class NpcAi
{
    /// <summary>A player victim's (HP, max HP) by entity key, for HP-ratio NPC skills (null: those skills use the level curve).</summary>
    public Func<int, (int Hp, int HpMax)>? PlayerHpOf { get; set; }

    /// <summary>The HP-ratio damage of an NPC skill against a victim, or null when the skill has none (or the victim is unknown).</summary>
    internal int? RatioDamage(int skillId, int victimKey, bool victimIsNpc)
    {
        if (SkillEffectEngine.RatioOf(skillId) is null) return null;
        if (victimIsNpc)
            return current?.GetValueOrDefault(victimKey) is { } npc ? SkillEffectEngine.RatioDamageAgainst(skillId, npc.Hp, npc.HpMax) : null;
        return PlayerHpOf?.Invoke(victimKey) is { } hp ? SkillEffectEngine.RatioDamageAgainst(skillId, hp.Hp, hp.HpMax) : null;
    }
}
