using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Duty missions and scrolls (ported from zaraza87/A2Core dell-merge-2026-11): data, the board wire, accept, give up.</summary>
public sealed class DutyMissionTests
{
    static readonly long Now = 1_793_600_000_000;
    const int DutyScrollItem = 532330036;
    const long ScrollWire = 88001;
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);

    [Fact]
    public void Duty_data_holds_the_board_pool_the_scroll_groups_and_the_limits()
    {
        var data = DutyTables.Embedded;
        var book = QuestBook.Embedded;
        var scrollQuests = book.Quests.Where(q => q.Value.Type == "DutyScroll").Select(q => q.Key).ToHashSet();
        var grouped = data.Groups.Values.SelectMany(g => g.Quests).ToHashSet();
        Assert.Equal(206, data.Missions.Count);
        Assert.Equal(44, data.Groups.Count);
        Assert.True(scrollQuests.SetEquals(grouped));
        Assert.Equal(15, data.BoardSize);
        Assert.True(data.Rule("DutyMission") is { GetLimit: 20, CompleteLimit: 5, Reroll: false });
        Assert.True(data.Rule("DutyScroll") is { GetLimit: 20, CompleteLimit: 0, Reroll: true });
        Assert.Equal(1000, data.RedrawOf("DutyMission")!.Costs[0][1]);
        Assert.Equal("DutyScroll_L1_Abyss_C_Rare_03", data.ScrollGroup(532330036));
        Assert.Equal("PKG_DUTYQUEST_D_01A", book.Find("UQ2302021")!.Rewards?.Random);
        var cycle = data.CycleOf(Now);
        Assert.Equal(7, DateTimeOffset.FromUnixTimeMilliseconds(cycle).Hour);
        Assert.True(cycle <= Now && Now < cycle + 86_400_000);
    }

    [Fact]
    public void A_reward_package_rolls_one_item_per_slot_with_the_slot_chance()
    {
        var rolled = DutyTables.Embedded.Roll("PKG_DUTYQUEST_D_01A", new Random(7));
        Assert.Equal([100_000_000L, 50_000_000, 33_000_000, 25_000_000, 10_000_000], rolled.Select(r => r.Prob));
        Assert.Contains(0, DutyTables.Paid(rolled, new Random(1)));
    }

    [Fact]
    public void Board_notice_layout_matches_the_usmap_struct()
    {
        var reward = new QuestRandomReward(930100003, 1000, 0, 100_000_000);
        var bytes = GamePackets.DutyBoard(3, 1, [new DutyBoardWire(91, 1302010, 0, 1, [reward])], 0x0102030405060708, [52], 0x1112131415161718);
        var r = new PacketReader(bytes) { Offset = 2 };
        Assert.Equal(0x8d1c, Op(bytes));
        Assert.Equal(3, r.I32());
        Assert.Equal(1, r.I32());
        Assert.Equal(1, (int)r.Varint());
        Assert.Equal(91, r.I32());
        Assert.Equal(1302010, r.I32());
        Assert.Equal(0, r.I32());
        Assert.Equal(0, r.I32()); // publisher
        Assert.Equal(1, r.U8());
        Assert.Equal(1, (int)r.Varint());
        Assert.Equal(930100003, r.I32());
        Assert.Equal(1000, (long)r.Varint());
        Assert.Equal(0, r.U8());
        Assert.Equal(100_000_000, r.I64());
        Assert.Equal(0x0102030405060708, r.I64());
        Assert.Equal(1, (int)r.Varint());
        Assert.Equal(52, r.I32());
        Assert.Equal(0x1112131415161718, r.I64());
        Assert.True(r.End);
    }

    [Fact]
    public async Task Opening_the_board_draws_missions_and_an_accepted_one_joins_the_quest_log()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 45);
        var open = await session.HandleAsync(new PacketWriter().U16(GameSession.DutyJournalRq).ToArray(), Now);
        Assert.Equal([0x8d1b, 0x8d1c], open.Select(Op));
        Assert.Equal(new byte[] { 0x1b, 0x8d, 0, 0 }, open[0]);
        var board = session.Duty.Board;
        Assert.True(board.Count > 0, $"level {session.Level} eligible {DutyState.Eligible(DutyTables.Embedded, QuestBook.Embedded, 1, session.Level).Count()} missions {DutyTables.Embedded.Missions.Count(m => QuestBook.Embedded.Find(m.Code) is not null)}");
        Assert.InRange(board.Count, 1, 15);
        Assert.All(board, e => Assert.Equal(1, e.Status));

        // A duty quest is not accepted through QuestAccept_RQ.
        var direct = await session.HandleAsync(new PacketWriter().U16(0x8d0a).I32(board[0].QuestId).ToArray(), Now);
        Assert.DoesNotContain(direct, p => Op(p) == 0x8d11);

        var entry = board[0];
        var accept = await session.HandleAsync(new PacketWriter().U16(GameSession.DutyAcceptRq).I32(entry.Uid).I32(entry.QuestId).ToArray(), Now);
        var rs = Assert.Single(accept, p => Op(p) == GameSession.DutyAcceptRs);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Contains(accept, p => Op(p) == 0x8d11);
        Assert.Equal(2, session.Duty.Board[0].Status);
        var record = session.Quests!.ByUid(session.Duty.Board[0].QuestUid);
        Assert.NotNull(record);

        // Given up: the mission is offered again.
        var giveUp = await session.HandleAsync(new PacketWriter().U16(0x8d0c).I32((int)record!.Uid).ToArray(), Now);
        Assert.Contains(giveUp, p => Op(p) == 0x8d1c);
        Assert.Equal(1, session.Duty.Board[0].Status);
        Assert.Null(session.Quests.Get(record.Code));
    }

    [Fact]
    public async Task The_board_is_stored_in_the_character_settings_and_sent_at_the_next_entry()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 45);
        await session.HandleAsync(new PacketWriter().U16(GameSession.DutyJournalRq).ToArray(), Now);
        var drawn = session.Duty.Board.Select(e => e.QuestId).ToList();
        var again = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 1000, level: 45);
        Assert.Equal(drawn, again.Duty.Board.Select(e => e.QuestId));
    }

    [Fact]
    public async Task Using_a_duty_scroll_item_accepts_a_scroll_quest_and_consumes_the_item()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 45);
        store.Items.Add(FakeCharacterStore.BagItem(DutyScrollItem, 1, wire: ScrollWire));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 1);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(ScrollWire).ToArray(), Now + 2);
        var rs = Assert.Single(replies, p => Op(p) == RidePackets.ItemUseRs);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Contains(replies, p => Op(p) == 0x8d11);
        var added = session.Quests!.All.Single(q => q.Status == QuestLog.Incomplete && session.Quests.Book.Find(q.Code)?.Type == "DutyScroll");
        Assert.True(session.Duty.Quests.ContainsKey(added.Code));
        Assert.Null(store.Items.FirstOrDefault(i => i.WireId == ScrollWire));
    }

    [Fact]
    public async Task ItemUseByItemId_on_a_duty_scroll_accepts_a_quest()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 45);
        store.Items.Add(FakeCharacterStore.BagItem(DutyScrollItem, 1, wire: ScrollWire));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 1);

        var replies = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.ItemUseByItemIdRq).I64(ScrollWire).I32(DutyScrollItem).I32(1).I32(0).ToArray(), Now + 2);
        Assert.Equal(0, BitConverter.ToInt32(Assert.Single(replies, p => Op(p) == RidePackets.ItemUseByItemIdRs), 2));
        Assert.Contains(replies, p => Op(p) == 0x8d11);
    }

    [Fact]
    public async Task Twenty_active_duty_quests_block_another_scroll_use()
    {
        var book = QuestBook.Embedded;
        var codes = book.Quests.Where(q => q.Value.Type == "DutyScroll" && q.Value.Level <= 45 && q.Value.Race == "Light")
            .Select(q => q.Key).Take(20).ToList();
        Assert.Equal(20, codes.Count);
        var store = new FakeCharacterStore { Progress = (45, 0) };
        foreach (var code in codes)
        {
            var quest = book.Find(code)!;
            store.Quests.Add(QuestLog.New(code, quest) with { Uid = store.Quests.Count + 2000L });
        }
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 45);
        store.Items.Add(FakeCharacterStore.BagItem(DutyScrollItem, 1, wire: ScrollWire));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 1);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(ScrollWire).ToArray(), Now + 2);
        var rs = Assert.Single(replies, p => Op(p) == RidePackets.ItemUseRs);
        Assert.Equal(DutyResults.DutyQuest_Fail_AcceptLimit, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(1, store.Items.Single(i => i.WireId == ScrollWire).Quantity);
    }

    [Fact]
    public void Every_duty_scroll_item_maps_to_a_group_with_eligible_quests_at_cap()
    {
        var duty = DutyTables.Embedded;
        var book = QuestBook.Embedded;
        var boxes = RewardBoxes.Embedded;
        Assert.Equal(86, duty.ScrollItems.Count);
        foreach (var (itemKey, groupName) in duty.ScrollItems)
        {
            var itemId = int.Parse(itemKey);
            Assert.True(duty.Groups.ContainsKey(groupName), $"item {itemId} group {groupName}");
            Assert.True(boxes.ImplementsUseEffect("GetDutyQuest"));
            Assert.Equal("GetDutyQuest", boxes.Use(itemId)!.Effect);
            var pool = duty.Groups[groupName].Quests.Count(c => book.Find(c) is { } q && q.Steps.Length > 0);
            Assert.True(pool > 0, $"group {groupName} has no quests with steps");
        }
    }
}
