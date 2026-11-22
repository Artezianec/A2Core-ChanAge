using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Combat helpers for QA quest bots (skill pick, scheduled hit landing).</summary>
public static class QaBotCombat
{
    /// <summary>Official FixSlot basic-attack chain roots (class-skills.json), not mid-combo steps.</summary>
    static readonly int[] BasicChainRootFallbacks =
    [
        11020000, 12010000, 13010000, 14020000, 15210000, 16010000, 17010000, 18010000,
    ];

    static readonly Dictionary<string, int> BasicChainRootByClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gladiator"] = 11020000,
        ["Templar"] = 12010000,
        ["Ranger"] = 14020000,
        ["Assassin"] = 13010000,
        ["Sorcerer"] = 15210000,
        ["Elementalist"] = 16010000,
        ["Spiritmaster"] = 16010000,
        ["Cleric"] = 17010000,
        ["Chanter"] = 18010000,
    };

    public static int BasicAttackSkill(GameData data, GameSession session, long now = 0)
    {
        var className = session.ClassName ?? "Gladiator";
        var weaponType = session.EquippedWeapon is { } w ? data.WeaponType(w) : null;
        bool Fits(int skillId)
        {
            var sk = data.Skill(skillId);
            if (sk?.Damage.Length is not > 0 || sk.Cost.Mp != 0 || sk.Cost.Sp != 0) return false;
            var weapons = (sk.Weapons ?? []).Where(w => w != "None").ToList();
            return weapons.Count == 0 || weaponType is not null && weapons.Contains(weaponType);
        }
        bool Ready(int skillId) => now <= 0 || session.CooldownUntil(skillId) <= now;

        var root = ResolveBasicChainRoot(data, session, className, Fits, Ready);
        if (root > 0)
            return PickBasicChainStep(data, session, now, root, Fits, Ready);

        var kit = data.LearnedSkills(className, session.Level).ToList();
        int Score((int SkillId, int Level) s)
        {
            var sk = data.Skill(s.SkillId);
            if (sk is null) return 100;
            return SkillCooldown.IsBasicAutoAttack(sk) ? 0 : 50;
        }
        foreach (var s in kit.OrderBy(Score).ThenBy(s => s.SkillId))
        {
            if (!Fits(s.SkillId) || !Ready(s.SkillId)) continue;
            if (data.Skill(s.SkillId) is { } sk && sk.Cost.Mp == 0 && sk.Cost.Sp == 0 && sk.Damage.Length > 0)
                return s.SkillId;
        }
        foreach (var skill in BasicChainRootFallbacks)
        {
            if (session.Knows(skill) && Fits(skill) && Ready(skill)) return skill;
        }
        return BasicChainRootFallbacks[0];
    }

    /// <summary>Root id of the class basic left-click chain from learned skills (chain_prev 0 auto-attack root).</summary>
    public static int BasicChainRoot(GameData data, GameSession session)
    {
        var className = session.ClassName ?? "Gladiator";
        bool Fits(int skillId)
        {
            var sk = data.Skill(skillId);
            if (sk?.Damage.Length is not > 0) return false;
            var weaponType = session.EquippedWeapon is { } w ? data.WeaponType(w) : null;
            var weapons = (sk.Weapons ?? []).Where(w => w != "None").ToList();
            return weapons.Count == 0 || weaponType is not null && weapons.Contains(weaponType);
        }
        return ResolveBasicChainRoot(data, session, className, Fits, _ => true);
    }

    static int ResolveBasicChainRoot(GameData data, GameSession session, string className, Func<int, bool> fits, Func<int, bool> ready)
    {
        if (BasicChainRootByClass.TryGetValue(className, out var classRoot)
            && data.Skill(classRoot) is { } classRootDef
            && SkillCooldown.IsBasicAutoAttack(classRootDef)
            && session.Knows(classRoot) && fits(classRoot)
            && (ready(classRoot) || HasOpenBasicFollowUp(data, session, classRoot)))
            return classRoot;

        foreach (var (skillId, _) in data.LearnedSkills(className, session.Level).OrderBy(s => s.SkillId))
        {
            var root = ChainRootId(data, skillId);
            if (data.Skill(root) is not { } def || !SkillCooldown.IsBasicAutoAttack(def)) continue;
            if (!session.Knows(root) || !fits(root)) continue;
            if (ready(root) || HasOpenBasicFollowUp(data, session, root)) return root;
        }
        foreach (var skill in BasicChainRootFallbacks)
        {
            if (data.Skill(skill) is not { } def || !SkillCooldown.IsBasicAutoAttack(def)) continue;
            if (session.Knows(skill) && fits(skill) && (ready(skill) || HasOpenBasicFollowUp(data, session, skill))) return skill;
        }
        return 0;
    }

    static bool HasOpenBasicFollowUp(GameData data, GameSession session, int root)
    {
        var current = root;
        for (var i = 0; i < 8; i++)
        {
            var follow = data.ChainFollowUpIds(current);
            if (follow.Count == 0) break;
            var next = follow[0];
            if (!data.IsBasicAttackChainSkill(next) || !session.Knows(next)) break;
            if (session.ActiveChainSkills.ContainsKey(next)) return true;
            current = next;
        }
        return false;
    }

    /// <summary>Next skill in the basic attack combo: deepest open chain follow-up (0x3818), else root when ready.</summary>
    public static int PickBasicChainStep(GameData data, GameSession session, long now, int root, Func<int, bool> fits, Func<int, bool> ready)
    {
        if (!fits(root) || !session.Knows(root)) return root;
        var chain = data.BasicAttackChainSkillIds(root, session.SkillLevel(root)).ToList();
        for (var i = chain.Count - 1; i >= 1; i--)
        {
            var id = chain[i];
            if (!session.Knows(id) || !fits(id) || !data.IsBasicAttackChainSkill(id)) continue;
            if (session.ActiveChainSkills.TryGetValue(id, out var until) && now < until && ready(id))
                return id;
        }
        if (ready(root) || !HasOpenBasicFollowUp(data, session, root))
            return ready(root) ? root : chain.FirstOrDefault(id => session.ActiveChainSkills.ContainsKey(id));
        return root;
    }

    static int ChainRootId(GameData data, int skillId)
    {
        var root = skillId;
        for (var i = 0; i < 8 && data.Skill(root) is { ChainPrev: > 0 } step; i++)
            root = step.ChainPrev;
        return root;
    }

    public static int HitLandMs(GameData data, int skillId)
    {
        var skill = data.Skill(skillId);
        if (skill is null || skill.Damage.Length == 0) return NpcAi.HitDelayMs + 80;
        return SkillTiming.HitLandMs(skill, skill.Damage.Length - 1) + 80;
    }

    /// <summary>Horizontal reach for QA bot positioning (Skill.dat UseRange; not the melee <see cref="QuestStepDriver.KillReach"/>).</summary>
    public static double CastReach(GameData data, int skillId) =>
        Math.Clamp(data.SkillUseRangeCm(skillId) + GameSession.CastRangeToleranceCm + 50, QuestStepDriver.KillReach * 0.5, 2500);

    public static int SwingBusyMs(GameData data, GameSession session, int skillId)
    {
        var skill = data.Skill(skillId);
        var cd = skill is null ? 0 : SkillCooldown.EffectiveMs(skill, session.SkillLevel(skillId));
        return Math.Max(HitLandMs(data, skillId), cd) + 80;
    }

    public static void ScheduleSwing(QuestDriverState state, GameData data, GameSession session, int skillId, long now)
    {
        state.CombatHitLandUntil = now + HitLandMs(data, skillId);
        state.CombatBusyUntil = now + SwingBusyMs(data, session, skillId);
    }

    public static bool IsCastPacket(byte[] packet) =>
        packet.Length >= 2 && packet[0] == 0x00 && packet[1] == 0x38 && packet.Length >= 8;

    public static int SkillFromCastPacket(byte[] packet) =>
        BitConverter.ToInt32(packet, 4);
}