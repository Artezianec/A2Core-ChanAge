using Aion.Application.Gameplay;
using Aion.Protocol;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Every quest journal change pairs 0x8d10 QuestUpdateProgress with 0x8d11 QuestAdd so tracker counters and HUD bars update
/// (district quests have no Quest.dat Part; hero episodes also need _roll_count on the add).
/// </summary>
public sealed class QuestJournalHudSyncTests
{
    const long Now = 1_760_006_500_000;
    const int EteziaNpcId = 2930003;
    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    [Fact]
    public async Task DQ1202013_second_AskNpc_on_step_6_pairs_QuestAdd_with_goals_and_step_advance()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("DQ1202013", 1202013, 600, QuestLog.Incomplete, 6, [1, 0, 0, 0, 0, 0]));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 16);
        var log = session.Quests!;
        var changed = log.Talked(EteziaNpcId, 16);
        Assert.Single(changed);
        var packets = await session.EmitQuestChangesForTests(changed, default);

        Assert.True(packets.Count(p => Op(p) == 0x8d10) >= 2, "step finish should emit milestone and saved progress");
        var adds = packets.Where(p => Op(p) == 0x8d11).ToList();
        Assert.True(adds.Count >= 2, "each progress sync should re-merge Common_Quest on QuestAdd");

        Assert.Contains(adds, p =>
        {
            var body = ReadQuestBody(p);
            return body.QuestId == 1202013 && body.Step == 6 && body.Goals[0] == 1 && body.Goals[1] == 1;
        });
        Assert.Contains(adds, p =>
        {
            var body = ReadQuestBody(p);
            return body.QuestId == 1202013 && body.Step == 7 && body.Goals.All(g => g == 0);
        });

        var saved = log.Get("DQ1202013")!;
        Assert.Equal(7, saved.Step);
        Assert.Equal([0, 0, 0, 0, 0, 0], saved.Goals);
    }

    [Fact]
    public async Task DQ1202011_accept_sends_add_and_progress_with_step_one_on_wire()
    {
        var store = new FakeCharacterStore { Progress = (16, 0) };
        store.Quests.Add(new QuestRecord("HQ1102130", 1102130, 50, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 16);
        Assert.Null(session.QuestAcceptBlockReason("DQ1202011"));

        var (_, packets) = await session.QuestCommandAsync("QuestAdd", "DQ1202011", default);

        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x8d11));
        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x8d10));
        var add = ReadQuestBody(packets.First(p => Op(p) == 0x8d11));
        Assert.Equal(1202011, add.QuestId);
        Assert.Equal(1, add.Step);
        Assert.Equal(0, add.RollCount);
        Assert.Equal(QuestLog.Incomplete, add.Status);
    }

    [Fact]
    public async Task HQ1102140_kill_credit_pairs_QuestAdd_with_objective_counter()
    {
        const int hungryBraxNpc = 2100303;
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1102140", 1102140, 700, QuestLog.Incomplete, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 18);
        var packets = await session.ApplyPartyQuestKillCreditAsync([hungryBraxNpc], default);
        Assert.NotEmpty(packets);

        var progress = packets.First(p => Op(p) == 0x8d10);
        var add = packets.First(p => Op(p) == 0x8d11);
        var progGoals = ReadProgressGoals(progress);
        var addBody = ReadQuestBody(add);
        Assert.Equal(1, progGoals[0]);
        Assert.Equal(1, addBody.Goals[0]);
        Assert.True(addBody.RollCount > 0, "hero episode quest should carry part ordinal on QuestAdd");
        Assert.Equal(QuestBook.Embedded.PartRollIndex("HQ1102140"), addBody.RollCount);
    }

    static (int QuestId, byte Status, int Step, int[] Goals, int RollCount) ReadQuestBody(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var questId = r.I32();
        var status = r.U8();
        var step = (int)r.Varint();
        var goals = new int[6];
        for (var i = 0; i < 6; i++) goals[i] = (int)r.Varint();
        r.I32();
        var roll = (int)r.Varint();
        return (questId, status, step, goals, roll);
    }

    static int[] ReadProgressGoals(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        r.I32();
        r.U8();
        r.Varint();
        var goals = new int[6];
        for (var i = 0; i < 6; i++) goals[i] = (int)r.Varint();
        return goals;
    }
}
