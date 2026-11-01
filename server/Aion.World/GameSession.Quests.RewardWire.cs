using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    const string QuestRewardMapTeleportGuide = "Tutorial_UI_MapAndTeleport";
    const byte SkillPointStatWire = 104; // 0x68 official skill points remaining on 0x364a

    /// <summary>Splits achievement replies into 0x5684 updates and everything else.</summary>
    static (List<byte[]> Counters, List<byte[]> Other) SplitAchievementCounters(IEnumerable<byte[]> packets)
    {
        var counters = new List<byte[]>();
        var other = new List<byte[]>();
        foreach (var p in packets)
            (p.Length >= 2 && p[0] == (AchievementCountUpdateNt & 0xff) && p[1] == (AchievementCountUpdateNt >> 8) ? counters : other).Add(p);
        return (counters, other);
    }

    byte[] SkillPointStatSheetUpdate()
    {
        if (resources is null) return [];
        var (_, hpMax) = StatSheet();
        return GamePackets.CharStatsUpdate(playerKey, [(SkillPointStatWire, SkillPointsEarned)], hpMax);
    }

    /// <summary>
    /// Experience and level-up packets for quest turn-in, in official sub-order (without 0x364a refresh or 0x5105; those come later in the batch).
    /// </summary>
    async Task<(List<byte[]> BeforeTakeReward, List<byte[]> AfterTakeReward, int LevelBefore, int LevelAfter)> QuestRewardExperienceAsync(
        long total, long gained, CancellationToken ct)
    {
        var before = level;
        if (gained > 0)
        {
            var scaled = (long)Math.Round(gained * (options.World?.ExpRate ?? 1));
            total += scaled - gained;
            gained = scaled;
        }
        var newLevel = level;
        while (newLevel < data.MaxLevel && data.NeedExp(newLevel) is > 0 and var need && total >= need)
        {
            total -= need;
            newLevel++;
        }
        if (newLevel >= data.MaxLevel) total = Math.Min(total, Math.Max(0, data.NeedExp(data.MaxLevel) - 1));
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SaveProgressAsync(character, newLevel, total, ct) ? [[]] : []);
            if (saved.Count == 0) return ([], [], before, before);
            if (selected is { } sel && sel.Summary.CharacterId == character)
                selected = sel with { Progress = sel.Progress with { Level = newLevel, Experience = total } };
        }
        experience = total;
        var beforeTake = new List<byte[]>();
        var afterTake = new List<byte[]>();
        if (newLevel == before)
        {
            beforeTake.Add(GamePackets.ExpUpdate(total, gained, ExpReasonQuest));
            return (beforeTake, afterTake, before, newLevel);
        }
        var unlockedBefore = GamePackets.UnlockedContents(before).ToHashSet();
        var skillsBefore = learned.ToHashSet();
        level = newLevel;
        LogLevelUp(before, newLevel);
        var daevanionNodesBefore = daevanionNodes.Count;
        EnsureStartNodes();
        RefreshKitSkillLevels();
        beforeTake.Add(GamePackets.ExpUpdate(total, gained, ExpReasonQuest));
        beforeTake.Add(GamePackets.LevelUpdate(level, level, total));
        if (className is { } cls && Profile() is { } profile)
        {
            var kit = data.SkillListForClient(cls, level, profile);
            learned = kit.Select(k => k.SkillId).ToHashSet();
            var added = kit.Where(k => !skillsBefore.Contains(k.SkillId)).ToList();
            if (added.Count > 0)
            {
                var originals = KitOriginalLevels(added);
                var daevanion = SkillWireDaevanionBonuses(added);
                var levels = added.Select(s => (s.SkillId, SkillWireDisplayLevel(s.SkillId))).ToList();
                afterTake.Add(GamePackets.AddSkillList(playerKey, levels, originalLevels: originals, additionalDaevanion: daevanion));
            }
            afterTake.AddRange(PassiveAbnormals());
        }
        var broadcast = PlayerPackets.LevelBroadcast(playerKey, level);
        worldPlayer?.Map?.Broadcast(worldPlayer, [broadcast]);
        if (daevanionNodes.Count > daevanionNodesBefore)
        {
            afterTake.Add(DaevanionBoardInfo());
            afterTake.Add(DaevanionPointUpdate());
        }
        return (beforeTake, afterTake, before, newLevel);
    }

    /// <summary>Official quest turn-in reward batch (capture 20261101-234302).</summary>
    List<byte[]> QuestRewardWireBatch(
        int uid, IReadOnlyList<byte[]> inventoryReplies,
        List<byte[]> expBeforeTake, List<byte[]> expAfterTake, int levelBefore, int levelAfter,
        List<byte[]> achieve5684, List<byte[]> achieveOther)
    {
        var replies = new List<byte[]>();
        var leveled = levelAfter > levelBefore;
        if (leveled)
        {
            replies.Add(GamePackets.Guide(QuestRewardMapTeleportGuide));
            var unlockedBefore = QuestUnlockTypes().ToHashSet();
            var unlocked = GamePackets.UnlockedContents(levelAfter).Where(t => !GamePackets.UnlockedContents(levelBefore).Contains(t)).ToArray();
            var questUnlock = QuestUnlockTypes().Where(t => !unlockedBefore.Contains(t)).ToArray();
            var unlockWire = unlocked.Concat(questUnlock).Distinct().Order().ToArray();
            if (unlockWire.Length > 0) replies.Add(GamePackets.ContentsUnlockUpdate(unlockWire));
            RecomputeCombatPower();
            replies.AddRange(RefreshStats());
            replies.Add(CharCombatPowerUpdate());
        }
        if (achieve5684.Count > 0) replies.Add(achieve5684[0]);
        replies.AddRange(expBeforeTake);
        replies.AddRange(inventoryReplies);
        replies.Add(GamePackets.QuestTakeRewardReply(uid));
        replies.AddRange(expAfterTake);
        for (var i = 1; i < achieve5684.Count; i++) replies.Add(achieve5684[i]);
        replies.AddRange(achieveOther);
        return replies;
    }
}

