using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>
    /// A monster Dot_NormalCalc tick as a share of the hit that applied it (one recorded pair: Drana Fairy 1223050
    /// hit 581, ticks 241). The row's flat and ratio are the player-on-NPC formula, not this one.
    /// </summary>
    public const double NpcDotShareOfHit = 0.41;

    /// <summary>
    /// Abnormal rows of a monster skill on the player. The first hit of a volley only (effect seq 1).
    /// OBSERVED (Addiboii/A2Core 90af6b09, 5754e6fb, 58f13c69): 0x382a mask 0x13, then 0x3816 for KnockdownInstant.
    /// </summary>
    List<byte[]> NpcSkillAbnormals(int casterKey, int skillId, Vector3d? casterAt, long now, int hitDamage, int hitSeq)
    {
        if (hitSeq > 1 || dead || resources is null) return [];
        var rows = PortedSkillAbnormals.NpcRows(skillId);
        if (rows.Count == 0) return [];
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
        var npcMove = false;
        var applied = false;
        foreach (var row in rows)
        {
            if (row.Chance < 10_000 && rng.Next(10_000) >= row.Chance) continue;
            if (AbnormalSpecs.Info(row.Abnormal) is { } info && AbnormalSpecs.Debuff(row.Abnormal))
            {
                if (DodgeInvulnerable(now))
                {
                    replies.Add(PlayerPackets.ImmuneAbnormal(playerKey, [info.Effects.Count > 0 ? info.Effects[0].Id : row.Abnormal]));
                    continue;
                }
                var immunities = buffs.Select(b => b.Combat).Where(c => c is not null).Cast<AbnormalCombat.BuffCombat>().ToList();
                if (AbnormalCombat.BlocksDebuffEffects(immunities, info, out var effectId))
                {
                    replies.Add(PlayerPackets.ImmuneAbnormal(playerKey, [effectId > 0 ? effectId : row.Abnormal]));
                    continue;
                }
            }
            var duration = Math.Max(1, rng.Next(Math.Min(row.MinMs, row.MaxMs), Math.Max(row.MinMs, row.MaxMs) + 1));
            var from = new BuffCaster(casterKey, (byte)row.Level, casterAt ?? Vector3d.Zero, hitDamage);
            if (AddBuff(row.Abnormal, duration, skillId, now, replies, path: SelfAbnormalPath.PlayerDebuff, appliedBy: from) is { } buff)
            {
                applied = true;
                if (buff.FromNpc && buff.Stats.ContainsKey("MoveSpeed")) npcMove = true;
            }
        }
        if (applied) replies.AddRange(BuffStatsChanged(speedBefore, npcMove));
        return replies;
    }

    /// <summary>
    /// Class self-buffs on cast accept (Dash, Defiance). Attack skills are skipped: their Damage rows must not self-buff.
    /// A self skill whose only ported row was filed as a target row (Light of Regeneration) is included.
    /// 0x3804 then 0x382a. Dodge's abnormal 200 stays on its own path.
    /// </summary>
    internal List<byte[]> ClassSelfBuffsForTests(int skill, int actionUid, long now) =>
        data.Skill(skill) is { } def ? ClassSelfBuffs(skill, def, actionUid, now) : [];

    List<byte[]> ClassSelfBuffs(int skill, SkillDefinition def, int actionUid, long now)
    {
        if (def.Damage.Length > 0 || resources is null) return [];
        var rows = PortedSkillAbnormals.SelfRows(skill);
        if (rows.Count == 0) rows = PortedSkillAbnormals.TargetRows(skill);
        if (rows.Count == 0 && (StigmaData.IsStigma(skill) || StigmaData.StigmaOfVariant(skill) is not null))
            rows = StigmaData.SelfRows(skill);
        if (rows.Count == 0) return [];
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
        var applied = false;
        foreach (var row in rows)
        {
            if (row.Abnormal == DodgeAbnormal) continue;
            if (row.Chance < 10_000 && rng.Next(10_000) >= row.Chance) continue;
            if (row.EffectRow > 0)
                replies.Add(GamePackets.SkillEffect(playerKey, playerKey, skill, actionUid, row.EffectRow));
            var duration = Math.Max(1, rng.Next(Math.Min(row.MinMs, row.MaxMs), Math.Max(row.MinMs, row.MaxMs) + 1));
            if (AddBuff(row.Abnormal, duration, skill, now, replies, path: SelfAbnormalPath.ClassSelfBuff) is not null)
                applied = true;
        }
        if (applied) replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }
}
