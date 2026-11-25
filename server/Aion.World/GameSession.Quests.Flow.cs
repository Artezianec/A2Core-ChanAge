using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    // ---------- Quest flow (talk, accept, reward, GM commands) ----------

    /// <summary>QuestStepNpcConversation_RQ: finds the quest by uid (after an optional kind byte) and counts the talk.</summary>
    /// <summary>The first i32 in the body (any offset after the opcode) that satisfies the test; the canonical offset 2 wins when it does.</summary>
    static int? FirstInt32(byte[] plain, Func<int, bool> fits)
    {
        for (var offset = 2; offset + 4 <= plain.Length; offset++)
            if (BitConverter.ToInt32(plain, offset) is var value && fits(value)) return value;
        return null;
    }

    /// <summary>
    /// 0x8D0A QuestAccept_RQ (2.0.11+): u8 flag (bit0 npc key present), i32 quest id, i32 npc key
    /// when set. The 2.0.6 form {quest id i32, npc key i32} is 10 bytes including the opcode.
    /// </summary>
    internal static (int QuestId, int NpcKey) ParseQuestAcceptRequest(byte[] plain)
    {
        if (plain.Length < 7) return (0, 0);
        if (plain.Length == 10) return (BitConverter.ToInt32(plain, 2), BitConverter.ToInt32(plain, 6));
        var hasNpc = (plain[2] & 1) != 0;
        return (BitConverter.ToInt32(plain, 3), hasNpc && plain.Length >= 11 ? BitConverter.ToInt32(plain, 7) : 0);
    }

    async Task<List<byte[]>> QuestStepTalkAsync(byte[] plain, long now, CancellationToken ct)
    {
        // Kind 2: a map event's talk objective {kind, map event id, npc key, objective index} (reply OBSERVED: 02 0000 id key index).
        // The request is unrecorded: when the fixed offsets do not name a running event and an NPC here, the first i32s that do are taken.
        if (plain.Length >= 15 && (plain[2] == 2 || plain[3] == 2))
        {
            int eventId = BitConverter.ToInt32(plain, 3), talkKey = BitConverter.ToInt32(plain, 7), index = BitConverter.ToInt32(plain, 11);
            var runs = (CopyOwner ?? this).mapEventRuns;
            if (!runs.Any(r => r.Id == eventId) || targets?.ContainsKey(talkKey) != true)
            {
                eventId = FirstInt32(plain, id => runs.Any(r => r.Id == id)) ?? eventId;
                talkKey = FirstInt32(plain, key => key > 0 && targets?.ContainsKey(key) == true) ?? talkKey;
                index = plain[^4..] is var tail && BitConverter.ToInt32(tail, 0) is >= 0 and < 6 and var last ? last : 0;
            }
            var eventReplies = new List<byte[]> { GamePackets.QuestStepTalkReply(2, eventId, talkKey, index) };
            eventReplies.AddRange(MapEventTalked(talkKey, index, close: false, now));
            return await WithMapEventClearsFlushedAsync(eventReplies, ct);
        }
        if (questLog is null) return [];
        foreach (var offset in new[] { 2, 3 })
        {
            if (plain.Length < offset + 8 || questLog.ByUid(BitConverter.ToInt32(plain, offset)) is not { } record) continue;
            var npcKey = BitConverter.ToInt32(plain, offset + 4);
            var npcId = targets?.GetValueOrDefault(npcKey)?.NpcId ?? 0;
            var replies = new List<byte[]> { GamePackets.QuestStepTalkReply(1, (int)record.Uid, npcKey) };
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Talked(npcId, level).Where(r => r.Code == record.Code), ct)));
            return replies;
        }
        return [];
    }

    /// <summary>
    /// The client finished an objective by itself (cutscene talk, note, fake item): kind 1 names a quest uid, kind 2 a
    /// map event. The official reply (0x9231) came before the quest's progress (0x8d10).
    /// </summary>
    async Task<List<byte[]>> ClientObjectiveAsync(byte kind, int id, int index, long now, CancellationToken ct)
    {
        if (kind == 2) return await WithMapEventClearsFlushedAsync(MapEventClientObjective(id, index, now), ct);
        if (questLog?.ClientCompleted(id, index, level) is not { } record) return [GamePackets.ClientObjectiveReply(kind, id, index, 1)];
        var replies = new List<byte[]> { GamePackets.ClientObjectiveReply(kind, id, index) };
        replies.AddRange(await Stored(() => QuestChangesAsync([record], ct)));
        return replies;
    }

    /// <summary>Last answer time per quest id to a repeated accept of a quest the character already has.</summary>
    readonly Dictionary<int, long> repeatedAcceptAnswered = [];

    /// <summary>Quiet window for an identical repeated accept: the client re-sent it every frame (live 2026-10-30: 742x).</summary>
    const long RepeatedAcceptQuietMs = 5_000;

    async Task<List<byte[]>> QuestAcceptAsync(int questId, int npcKey, CancellationToken ct)
    {
        // QuestAccept_RQ for a quest the character already has (open or rewarded). Live 2026-10-30: after a map reload the
        // client asked for AQ2602000 (the ClearQuest sub-quest of HQ2101060) every frame, 742 times, because each answer was
        // a refusal (0x8d0b result 1) it retried. Answer once with the quest as it stands (0x8d0b result 0 + 0x8d11 with its
        // status, the accept answer shape), then stay quiet on identical repeats for a while.
        if (questLog is not null && questLog.Book.ById.TryGetValue(questId, out var takenCode) && questLog.Get(takenCode) is { } taken
            && !(questLog.Book.Find(takenCode) is { } takenQuest && QuestLog.Reacceptable(takenQuest, taken)))
        {
            if (repeatedAcceptAnswered.TryGetValue(questId, out var at) && lastNow - at < RepeatedAcceptQuietMs) return [];
            repeatedAcceptAnswered[questId] = lastNow;
            Note($"QuestAccept {takenCode}: already taken (status {taken.Status}), answered with its state");
            return
            [
                GamePackets.QuestAcceptReply(questId, (int)taken.Uid, 0),
                GamePackets.QuestAdd(Wire(taken, npcKey)),
                GamePackets.QuestProgress(Wire(taken, npcKey)),
            ];
        }

        // Duty quests come from the mission board and the scrolls only (GameSession.Duty.cs).
        if (questLog is null || !questLog.Book.ById.TryGetValue(questId, out var code) || !questLog.CanAccept(code, level, QuestConditionsHold)
            || DutyTables.IsDuty(questLog.Book.Find(code)?.Type))
            return [GamePackets.QuestAcceptReply(questId, 0, 1)];
        return await Stored(async () =>
        {
            // A repeatable re-accept reuses the stored row (and its uid); New starts it over at step 1.
            var saved = await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!) with { Uid = questLog.Get(code)?.Uid ?? 0 }, ct);
            repeatedAcceptAnswered.Remove(questId);
            LogQuestEvent("quest_accept", code, saved.Step, QuestLog.Incomplete);
            var replies = QuestAcceptPackets(saved, npcKey);
            replies.AddRange(ApplyQuestWorldGating(lastNow));
            return replies;
        });
    }

    async Task<List<byte[]>> QuestGiveUpAsync(int uid, CancellationToken ct)
    {
        if (questLog?.ByUid(uid) is not { Status: QuestLog.Incomplete } record) return [GamePackets.QuestGiveUpReply(uid, 1)];
        return await Stored(async () =>
        {
            if (Store is { } store && CharacterId is { } character) await store.RemoveQuestAsync(character, record.Code, ct);
            questLog.Remove(record.Code);
            var replies = new List<byte[]> { GamePackets.QuestGiveUpReply(uid), GamePackets.QuestRemove([uid], 5) };
            replies.AddRange(await DutyGaveUpAsync(record, ct));
            replies.AddRange(ApplyQuestWorldGating(lastNow));
            return replies;
        });
    }

    /// <summary>
    /// Pays a Complete quest (official order): experience (0x8d07), the items and Kina (0x561b,
    /// reason 24), the reply (0x8d0f), contents it unlocks (0xe258), then its follow-up quest is
    /// added (0x8d11). Development logins store nothing and pay nothing.
    /// </summary>
    async Task<List<byte[]>> QuestRewardAsync(int uid, int selectIndex, long now, CancellationToken ct)
    {
        if (questLog?.ByUid(uid) is not { Status: QuestLog.Complete } record || questLog.Book.Find(record.Code) is not { } quest)
            return [GamePackets.QuestTakeRewardReply(uid, 1)];
        if (DutyRewardRefusal(quest) is var dutyRefused and > 0) return [GamePackets.QuestTakeRewardReply(uid, dutyRefused)];   // GameSession.Duty.cs
        return await Stored(async () =>
        {
            var unlockedBefore = QuestUnlockTypes().ToHashSet();
            var rewards = quest.Rewards ?? new QuestRewards();
            var classItems = className is { } cls && rewards.ClassItems?.GetValueOrDefault(cls) is { } own ? own : [];
            var items = (rewards.Items ?? []).Concat(classItems).Where(i => i.Length == 2).Select(i => ((int)i[0], i[1])).ToList();
            if (rewards.Select is { Length: > 0 } choices)
            {
                var idx = selectIndex >= 0 && selectIndex < choices.Length ? selectIndex : 0;
                if (choices[idx] is { Length: >= 2 } chosen)
                    items.Add(((int)chosen[0], chosen[1]));
            }
            // A duty quest's offered random rewards (QuestReward RandomReward): the items its chances pay (GameSession.Duty.cs).
            var (randomItems, randomPaid) = DutyRandomPaid(record);
            items.AddRange(randomItems);
            if (rewards.Kina > 0) items.Insert(0, (KinaItemId, rewards.Kina));
            items = options.Rates.WithKinah(items);
            var questExp = options.Rates.QuestReward(rewards.Exp);
            var replies = new List<byte[]>();
            var levelBeforeReward = level;
            List<byte[]> inventoryReplies = [];
            long totalExp = experience;
            if (Store is { } store && CharacterId is { } character)
            {
                if (await store.RewardQuestAsync(character, record.Code, questExp, items, ct) is not long total)
                    return [GamePackets.QuestTakeRewardReply(uid, 1)];
                totalExp = total;
                inventoryReplies = await InventoryChangesAsync(ct);
                replies.AddRange(await AutoUseAcquiredItemsAsync(items.Select(i => i.Item1), now, ct));
            }
            var (expBeforeTake, expAfterTake, levelBefore, levelAfter) =
                await QuestRewardExperienceAsync(totalExp, questExp, ct);
            var achieveAll = new List<byte[]>();
            if (levelAfter > levelBefore) achieveAll.AddRange(AchieveSet("PCLevel", levelAfter));
            achieveAll.AddRange(AchieveQuest(record.Code, quest.Type));
            var (achieve5684, achieveOther) = SplitAchievementCounters(achieveAll);
            replies.AddRange(QuestRewardWireBatch(uid, inventoryReplies,
                expBeforeTake, expAfterTake, levelBefore, levelAfter, achieve5684, achieveOther, randomPaid));
            if (AscensionQuests.Contains(record.Code) && (selected?.Ascension ?? 1) < 2
                && (Store is not { } ascending || CharacterId is not { } ascended || await ascending.AscendAsync(ascended, 2, ct)))
                replies.AddRange(Ascend(2));
            var rewarded = record with { Status = QuestLog.Rewarded };
            questLog.Set(rewarded);
            if (DutyTables.IsDuty(quest.Type))
                // A rewarded duty quest leaves the log (it can be drawn again) and a mission counts for the day (GameSession.Duty.cs).
                replies.AddRange(await DutyRewardedAsync(rewarded, quest, ct));
            else
            {
                // QuestAdd (0x8d11) before QuestProgress (0x8d10), the same order as accept and step changes.
                replies.Add(GamePackets.QuestAdd(Wire(rewarded)));
                replies.Add(GamePackets.QuestProgress(Wire(rewarded)));
            }
            var unlocked = QuestUnlockTypes().Where(t => !unlockedBefore.Contains(t)).ToArray();
            if (unlocked.Length > 0 && levelAfter <= levelBeforeReward)
                replies.Add(GamePackets.ContentsUnlockUpdate(unlocked));
            if (questLog.FollowUpOnReward(quest, level, QuestConditionsHold) is { } next)
            {
                var follow = await SaveQuestAsync(QuestLog.New(next, questLog.Book.Find(next)!), ct);
                replies.Add(GamePackets.QuestAdd(Wire(follow)));
                replies.Add(GamePackets.QuestProgress(Wire(follow)));
                LogQuestEvent("quest_accept", next, follow.Step, QuestLog.Incomplete);
            }
            else if (quest.Next is { } waiting && questLog.Book.Find(waiting) is { } follow)
                DebugMapEvent($"quest follow-up {waiting} after {record.Code} not auto-added: acquire={follow.Acquire} startNpc={follow.StartNpc} turnInNpc={quest.FinishNpc} block={questLog.AcceptBlockReason(follow, level, QuestConditionsHold) ?? "-"}");
            replies.AddRange(await QuestChangesAsync(questLog.Recheck(level), ct));
            if (levelAfter > levelBefore || rewards.Exp > 0)
                replies.Add(SkillPointStatSheetUpdate());
            replies.AddRange(LeaveQuestDungeonAfterReward(quest, now));
            return replies;
        });
    }

    /// <summary>
    /// The quest whose turn-in ascends the character. OBSERVED in the 2026-10-19 recording at HQ1101020's reward (Ashar): the
    /// flight gauge's maximum (0x8d00 type 13, 250,000), WingChange 0x5676 with the race's default wing, the stats, then the
    /// Subspace's event and quests; the gauge then filled from 40,000 in 40,000 steps (a self skill, 5002). OURS: filled at once,
    /// WingAcquire 0x5673 for the default wing right before the stats (upstream #35172); 0xe295 is not decoded. The Dark chain's counterpart is
    /// HQ2101050 (same Ashar turn-in, then "Spread Your Wings" HQ2101060 in the Subspace, as HQ1101021); only the Light one
    /// ascended, so an Asmodian never got wings.
    /// </summary>
    public static readonly IReadOnlySet<string> AscensionQuests = new HashSet<string> { "HQ1101020", "HQ2101050" };

    List<byte[]> Ascend(int grade)
    {
        if (selected is { } state) selected = state with { Ascension = grade };
        wingId = GamePackets.DefaultWing(profileRace);
        if (resources is null) return [];
        resources.FpMax = resources.Fp = FlightMax;
        return [GamePackets.CharPoints(playerKey, [(CharPoint.MaxFp, resources.FpMax), (CharPoint.Fp, resources.Fp)]),
                GamePackets.WingChangeNotice(playerKey, wingId, false), GamePackets.WingAcquire(playerKey, wingId), .. RefreshStats(), GamePackets.GlideCooltime(playerKey)];
    }

    /// <summary>Builder QuestAdd / QuestComplete / QuestCompleteStep / QuestSetStep / QuestDelete for a quest code or numeric id.</summary>
    public async Task<(string Message, List<byte[]> Packets)> QuestCommandAsync(string command, string quest, CancellationToken ct, int stepOrder = 0)
    {
        if (questLog is null) return ("Quests need a character in the world.", []);
        var code = questLog.Book.Find(quest) is not null ? quest
            : int.TryParse(quest, out var id) && questLog.Book.ById.TryGetValue(id, out var byId) ? byId : null;
        if (code is null) return ($"No quest {quest}.", []);
        var title = questLog.Book.Find(code)!.Title ?? code;
        switch (command)
        {
            case "QuestAdd":
                if (questLog.Get(code) is not null) return ($"{code} is already in the journal.", []);
                var added = await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!), ct);
                return ($"Added {code} \"{title}\".", QuestAcceptPackets(added, 0).TakeLast(2).ToList());
            case "QuestCompleteStep":
                return questLog.CompleteStep(code, level) is { } stepped
                    ? ($"{code}: step done.", await QuestChangesAsync([stepped], ct)) : ($"{code} is not in progress.", []);
            case "QuestSetStep" when stepOrder > 0:
                return questLog.SetStep(code, stepOrder) is { } set
                    ? ($"{code}: set to step {stepOrder}.", await QuestChangesAsync([set], ct)) : ($"{code}: not in journal or unknown step {stepOrder}.", []);
            case "QuestComplete":
                return questLog.CompleteAll(code) is { } done
                    ? ($"{code} complete; take the reward at the NPC or with QuestReward.", await QuestChangesAsync([done], ct)) : ($"{code} is not in progress.", []);
            case "QuestReward":
                return questLog.Get(code) is { Status: QuestLog.Complete } complete
                    ? ($"{code} rewarded.", await QuestRewardAsync((int)complete.Uid, -1, 0, ct)) : ($"{code} is not complete.", []);
            case "QuestDelete":
                if (questLog.Get(code) is not { } existing) return ($"{code} is not in the journal.", []);
                if (Store is { } store && CharacterId is { } character) await store.RemoveQuestAsync(character, code, ct);
                questLog.Remove(code);
                return ($"Removed {code}.", [GamePackets.QuestRemove([(int)existing.Uid], 1)]);
        }
        return ("Unknown quest command.", []);
    }
}
