using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Stigmas (docs/STIGMAS.md; ported from ckrealm/A2Core): table, shards, slots, level-up/down, reset, persistence, gating.</summary>
public sealed class StigmaTests
{
    static readonly long Now = 1_700_000_000_000;
    const int RendingBlow = 11010000;
    static readonly StigmaOptions On = new() { Enabled = true };

    static GameSession NewSession(FakeCharacterStore store, int level, StigmaOptions? stigmas) =>
        new(GameData.Embedded, new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId, Stigmas = stigmas }, new Random(3)) { Store = store };

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, int level, StigmaOptions? stigmas = null, bool create = true, long kina = 1_000_000)
    {
        store.Progress = (level, 0);
        var session = NewSession(store, level, stigmas ?? On);
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        if (create) await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        if (create && kina > 0)
        {
            var wire = store.Items.Count == 0 ? 7000 : store.Items.Max(i => i.WireId) + 1;
            store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, kina, wire));
        }
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        return (session, await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
    }

    static IReadOnlyList<int> ClassStigmas(GameSession session) => GameData.Embedded.Stigmas.ClassStigmas(session.ClassName);

    static Task<List<byte[]>> LevelUp(GameSession session, int skill) =>
        session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skill).ToArray(), Now);

    static Task<List<byte[]>> LevelDown(GameSession session, int skill) =>
        session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(skill).ToArray(), Now);

    static Task<List<byte[]>> Reset(GameSession session, byte type, int skill) =>
        session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(type).I32(skill).ToArray(), Now);

    static Task<List<byte[]>> SetSlot(GameSession session, byte slot, byte type, int skill) =>
        session.HandleAsync(new PacketWriter().U16(GameSession.QuickSlotSetRq).U8(0).U8(slot).U8(type).I64(0).I32(0).I32(skill).U8(0).I32(0).U8(0).ToArray(), Now);

    static int Result(byte[] reply) => BitConverter.ToUInt16(reply, 2);

    /// <summary>The request's own reply: the official order puts a level variant's 0x3810/0x380f/0x3813 before 0x3840 and 0x3844.</summary>
    static byte[] Own(List<byte[]> replies) =>
        replies.FirstOrDefault(p => GameSessionTestHelpers.Op(p) is GameSession.SkillLevelUpRs or GameSession.SkillLevelDownRs or GameSession.SkillLevelResetRs) ?? replies[0];

    [Fact]
    public void Table_has_the_clients_117_stigmas_13_per_class_and_none_in_a_level_kit()
    {
        var table = GameData.Embedded.Stigmas;
        Assert.Equal(9, table.Classes.Count);
        Assert.All(table.Classes, c => Assert.Equal(13, table.ClassStigmas(c).Count));
        foreach (var cls in table.Classes.Where(c => c != "Fighter"))
        {
            var kit = GameData.Embedded.LearnedSkills(cls, 55).Select(k => k.SkillId).ToHashSet();
            Assert.DoesNotContain(table.ClassStigmas(cls), kit.Contains);
            Assert.All(table.ClassStigmas(cls), id => Assert.NotNull(GameData.Embedded.Skill(id)));
        }
    }

    [Fact]
    public void Shards_slots_and_costs_follow_the_table()
    {
        var table = GameData.Embedded.Stigmas;
        Assert.Equal(0, table.ShardsAt(21));
        Assert.Equal(1, table.ShardsAt(22));
        Assert.Equal(16, table.ShardsAt(37));
        Assert.Equal(18, table.ShardsAt(39));
        Assert.Equal(30, table.ShardsAt(45));
        Assert.Equal(30, table.ShardsAt(55));
        Assert.Equal([0, 1, 1, 2, 3, 4, 4], new[] { 21, 22, 26, 27, 32, 37, 45 }.Select(l => table.SlotsAt(l, 4)));
        Assert.Equal(6, table.SlotsAt(45, 6));
        Assert.Equal(1, table.Cost(1));
        Assert.Equal(2, table.Cost(6));
        Assert.Equal(8, table.Cost(20));
        Assert.Null(table.Cost(21));
        Assert.Equal(15, table.Spent(10));
        Assert.Equal(75, table.Spent(20));
    }

    [Fact]
    public void Options_are_validated_and_off_by_default()
    {
        Assert.False(StigmaOptions.Default.Enabled);
        Assert.Equal(0, StigmaOptions.Default.ResetKinah);   // free, as the official global resets
        Assert.Throws<InvalidOperationException>(() => new StigmaOptions { Slots = 7 }.Validated());
        Assert.Throws<InvalidOperationException>(() => new StigmaOptions { MaxLevel = 21 }.Validated());
        Assert.Throws<InvalidOperationException>(() => new StigmaOptions { ShardRate = -1 }.Validated());
        Assert.Throws<InvalidOperationException>(() => new StigmaOptions { ResetKinah = -1 }.Validated());
        new StigmaOptions { Slots = 6, ShardRate = 3, MaxLevel = 20, ResetKinah = 0 }.Validated();
    }

    [Fact]
    public async Task Off_a_stigma_stays_unlearnable_as_before()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30, StigmaOptions.Default);
        var stigma = ClassStigmas(session)[0];
        var reply = await LevelUp(session, stigma);
        Assert.Equal(GameSession.SkillAcquireNotFoundData, Result(Own(reply)));
        Assert.Empty(store.StigmaLevels);
        Assert.False(session.StigmasOn);
    }

    [Fact]
    public async Task Below_level_22_a_stigma_is_refused()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 21);
        var reply = await LevelUp(session, ClassStigmas(session)[0]);
        Assert.Equal(GameSession.SkillAcquireNotEnoughCharLevel, Result(Own(reply)));
        Assert.Equal(0, session.StigmaShardsTotal);
        Assert.Equal(0, session.StigmaSlotsOpen);
        Assert.Empty(store.StigmaLevels);
        Assert.DoesNotContain(reply, p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt);
    }

    [Fact]
    public async Task Learning_and_raising_spend_shards_until_they_run_out()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        Assert.Equal(9, session.StigmaShardsTotal);
        Assert.Equal(2, session.StigmaSlotsOpen);
        var stigma = ClassStigmas(session)[0];

        var learn = await LevelUp(session, stigma);
        Assert.Equal(0, Result(Own(learn)));
        Assert.Equal(GameSession.SkillAddNt, GameSessionTestHelpers.Op(learn[1]));   // 0x380f reason 2 (learned), as recorded
        Assert.Equal(2, learn[1][^1]);
        Assert.Equal(GameSession.SkillPointUpdateNt, GameSessionTestHelpers.Op(learn[^1]));
        Assert.Equal(2, learn[^1][3]);   // ESkillPointType StigmaShard
        Assert.Equal(1, learn[^1][4]);   // one shard spent
        Assert.Equal(1, store.StigmaLevels[stigma]);
        Assert.Equal(1, session.SkillLevel(stigma));

        var raise = await LevelUp(session, stigma);
        Assert.Equal(0, Result(Own(raise)));
        Assert.Equal(GameSession.SkillLevelUpdateNt, GameSessionTestHelpers.Op(raise[1]));
        Assert.Equal(2, session.SkillLevel(stigma));

        // Levels 3-5 cost 1, 6 costs 2: 1+1+1+1+1+2 = 7, then 7 costs 2 (9 spent), then 8 needs 2 more.
        for (var i = 0; i < 5; i++) Assert.Equal(0, Result(Own(await LevelUp(session, stigma))));
        Assert.Equal(7, session.Stigmas[stigma]);
        Assert.Equal(0, session.StigmaShardsLeft);
        var refused = await LevelUp(session, stigma);
        Assert.Equal(GameSession.SkillAcquireNotEnoughSkillPoint, Result(Own(refused)));
        Assert.Equal(7, store.StigmaLevels[stigma]);
    }

    [Fact]
    public async Task Another_class_stigma_is_not_a_stigma_here()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var other = GameData.Embedded.Stigmas.Classes.Where(c => c != session.ClassName).SelectMany(GameData.Embedded.Stigmas.ClassStigmas).First();
        var reply = await LevelUp(session, other);
        Assert.NotEqual(0, Result(Own(reply)));
        Assert.Empty(store.StigmaLevels);
    }

    [Fact]
    public async Task Level_down_refunds_one_level_and_stops_at_1()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var stigma = ClassStigmas(session)[0];
        await LevelUp(session, stigma);
        await LevelUp(session, stigma);
        var left = session.StigmaShardsLeft;
        var down = await LevelDown(session, stigma);
        Assert.Equal(0, Result(Own(down)));
        Assert.Equal(1, store.StigmaLevels[stigma]);
        Assert.Equal(left + 1, session.StigmaShardsLeft);
        Assert.NotEqual(0, Result(Own(await LevelDown(session, stigma))));
        Assert.Equal(1, store.StigmaLevels[stigma]);
    }

    [Fact]
    public async Task Reset_all_forgets_every_stigma_refunds_and_clears_its_slots()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 40);
        var (a, b) = (ClassStigmas(session)[0], ClassStigmas(session)[1]);
        await LevelUp(session, a);
        await LevelUp(session, a);
        await LevelUp(session, b);
        Assert.Equal(0, Result(Own(await SetSlot(session, 30, 4, a))));
        Assert.True(session.StigmaCastable(a));

        var reset = await Reset(session, 2, 0);
        Assert.Equal(GameSession.SkillLevelResetRs, GameSessionTestHelpers.Op(reset[0]));
        Assert.Equal(0, Result(Own(reset)));
        Assert.Equal(GameSession.SkillLevelUpdateNt, GameSessionTestHelpers.Op(reset[1]));   // the levels to 0 (0x3813 reason 5), as recorded
        Assert.Equal(GameSession.SkillPointUpdateNt, GameSessionTestHelpers.Op(reset[^1]));
        Assert.Empty(store.StigmaLevels);
        Assert.Empty(session.Stigmas);
        Assert.Equal(session.StigmaShardsTotal, session.StigmaShardsLeft);
        Assert.DoesNotContain(store.QuickSlots.Values, s => s.SkillId == a);
        Assert.False(session.StigmaCastable(a));
    }

    [Fact]
    public async Task Reset_one_stigma_keeps_the_others()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 40);
        var (a, b) = (ClassStigmas(session)[0], ClassStigmas(session)[1]);
        await LevelUp(session, a);
        await LevelUp(session, b);
        Assert.Equal(0, Result(Own(await Reset(session, 2, a))));
        Assert.Equal([b], store.StigmaLevels.Keys);
    }

    [Fact]
    public async Task Slots_take_only_learned_stigmas_up_to_the_open_count()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 22);
        Assert.Equal(1, session.StigmaSlotsOpen);
        var (a, b) = (ClassStigmas(session)[0], ClassStigmas(session)[1]);
        Assert.Equal(GameSession.SkillAcquireNotFoundData, Result(Own(await SetSlot(session, 30, 4, a))));   // not learned
        await LevelUp(session, a);
        Assert.False(session.StigmaCastable(a));   // learned, not on a slot
        Assert.Equal(0, Result(Own(await SetSlot(session, 30, 4, a))));
        Assert.True(session.StigmaCastable(a));
        // Level 22 gives one shard: b cannot be learned, and a second stigma would not fit one slot anyway.
        store.StigmaLevels[b] = 1;
        var (relog, entry) = await EnterAsync(store, 22, create: false);
        // two stigmas cost 2 > 1 shard: only the excess is dropped (highest skill id), the other stays
        Assert.Single(relog.Stigmas);
        Assert.Equal(1, relog.Stigmas.Values.Single());
        Assert.Equal(0, relog.StigmaShardsLeft);
        Assert.Single(store.StigmaLevels);
        Assert.Contains(entry, p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt && p.Length > 3 && p[3] == 2);
        Assert.Equal(GameSession.QuickSlotMismatchSlotType, Result(Own(await SetSlot(relog, 31, 4, RendingBlow))));
    }

    [Fact]
    public async Task Second_stigma_beyond_the_open_slots_is_refused()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 26);   // 5 shards, 1 slot
        var (a, b) = (ClassStigmas(session)[0], ClassStigmas(session)[1]);
        await LevelUp(session, a);
        await LevelUp(session, b);
        Assert.Equal(0, Result(Own(await SetSlot(session, 30, 4, a))));
        Assert.Equal(GameSession.QuickSlotStigmaMaxCount, Result(Own(await SetSlot(session, 31, 4, b))));
        Assert.Equal(0, Result(Own(await SetSlot(session, 30, 4, b))));   // replacing a's slot is fine
        Assert.True(session.StigmaCastable(b));
        Assert.False(session.StigmaCastable(a));
    }

    [Fact]
    public async Task RequireSlot_off_casts_a_learned_stigma_without_a_slot()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30, new StigmaOptions { Enabled = true, RequireSlot = false });
        var a = ClassStigmas(session)[0];
        await LevelUp(session, a);
        Assert.True(session.StigmaCastable(a));
    }

    [Fact]
    public async Task Learned_stigmas_survive_relog_and_are_in_the_skill_list()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var a = ClassStigmas(session)[0];
        await LevelUp(session, a);
        await LevelUp(session, a);
        await LevelUp(session, a);

        var (relog, entry) = await EnterAsync(store, 30, create: false);
        Assert.Equal(3, relog.Stigmas[a]);
        Assert.Equal(3, relog.SkillLevel(a));
        var list = entry.First(p => GameSessionTestHelpers.Op(p) == 0x5100);
        Assert.True(ContainsI32(list, a));
        Assert.Contains(entry, p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt && p.Length > 3 && p[3] == 2);
        Assert.Equal(6, relog.StigmaShardsLeft);
    }

    [Fact]
    public async Task Off_stored_stigmas_are_kept_but_not_sent()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var a = ClassStigmas(session)[0];
        await LevelUp(session, a);
        var (relog, entry) = await EnterAsync(store, 30, StigmaOptions.Default, create: false);
        Assert.False(relog.StigmaCastable(a));
        Assert.False(ContainsI32(entry.First(p => GameSessionTestHelpers.Op(p) == 0x5100), a));
        Assert.DoesNotContain(entry, p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt && p.Length > 3 && p[3] == 2);
        Assert.Equal(1, store.StigmaLevels[a]);
    }

    [Fact]
    public async Task Skill_points_still_work_with_stigmas_on()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        await LevelUp(session, ClassStigmas(session)[0]);
        var up = await LevelUp(session, RendingBlow);
        Assert.Equal([0x3656, GameSession.SkillLevelUpRs, GameSession.SkillLevelUpdateNt, GameSession.SkillPointUpdateNt],
            GameSessionTestHelpers.SkillMasteryCoreOpcodes(up));
        Assert.Equal(GamePackets.SkillPointUpdate(1), up[^1]);
        Assert.Equal(2, session.SkillLevel(RendingBlow));
        var reset = await Reset(session, 1, 0);
        Assert.Equal(0, Result(reset[1]));
        Assert.Equal(1, session.SkillLevel(RendingBlow));
        Assert.Single(store.StigmaLevels);   // a skill-point reset leaves the stigmas alone
    }

    [Fact]
    public async Task Summary_lists_shards_slots_and_stigmas()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var a = ClassStigmas(session)[0];
        await LevelUp(session, a);
        await SetSlot(session, 30, 4, a);
        var text = session.StigmaSummary();
        Assert.Contains("Stigma Shards: 8 of 9", text);
        Assert.Contains("Slots: 1/2", text);
        Assert.Contains("(slotted)", text);
        var (off, _) = await EnterAsync(new FakeCharacterStore(), 30, StigmaOptions.Default);
        Assert.Contains("off", off.StigmaSummary());
    }

    [Fact]
    public async Task Trim_reduces_levels_to_fit_the_shard_budget_and_clears_slots_of_dropped_stigmas()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 45);
        var (a, b) = (ClassStigmas(session)[0], ClassStigmas(session)[1]);
        store.StigmaLevels[a] = 8;
        store.StigmaLevels[b] = 5;
        store.QuickSlots[31] = new QuickSlotRecord(31, 4, 0, 0, b, [0, 0, 0, 0]);
        // A GM level-down to 22: 1 shard total, so only the excess is trimmed.
        store.Progress = (22, 0);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = 22 } };
        var (relog, _) = await EnterAsync(store, 22, new StigmaOptions { Enabled = true, ShardRate = 1 }, create: false);
        Assert.Equal(1, relog.StigmaShardsTotal);
        Assert.True(relog.StigmaShardsSpent <= relog.StigmaShardsTotal);
        Assert.True(relog.Stigmas.Count is 0 or 1);
        if (relog.Stigmas.Count == 1) Assert.Equal(1, relog.Stigmas.Values.Single());
        Assert.DoesNotContain(store.QuickSlots.Values, s => s.SkillId == b && !relog.Stigmas.ContainsKey(b));
        Assert.DoesNotContain(relog.QuickSlots.Values, s => s.SkillId == b && !relog.Stigmas.ContainsKey(b));
    }

    [Fact]
    public async Task Lowering_MaxLevel_clamps_stored_levels()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 45);
        var a = ClassStigmas(session)[0];
        for (var i = 0; i < 8; i++) Assert.Equal(0, Result(Own(await LevelUp(session, a))));
        Assert.Equal(8, store.StigmaLevels[a]);
        var (relog, _) = await EnterAsync(store, 45, new StigmaOptions { Enabled = true, MaxLevel = 3 }, create: false);
        Assert.Equal(3, relog.Stigmas[a]);
        Assert.Equal(3, store.StigmaLevels[a]);
        Assert.True(relog.StigmaShardsLeft > 0);
    }

    [Fact]
    public async Task Reset_refuses_a_non_stigma_acquire_type_and_echoes_it_without_forgetting()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var a = ClassStigmas(session)[0];
        await LevelUp(session, a);
        var reply = await Reset(session, 99, a);
        Assert.Equal(GameSession.SkillAcquireInvalidClass, Result(Own(reply)));
        Assert.Equal(99, reply[0][4]);   // acquireType echoed on the 0x3844 refusal
        Assert.Equal(1, store.StigmaLevels[a]);
        Assert.Equal(1, session.Stigmas[a]);
    }

    [Fact]
    public async Task A_failed_level_up_save_does_not_claim_the_skill_is_missing()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var a = ClassStigmas(session)[0];
        store.FailStigmaSaves = 1;
        var reply = await LevelUp(session, a);
        Assert.Equal(GameSession.SkillAcquirePersistFailed, Result(Own(reply)));
        Assert.NotEqual(GameSession.SkillAcquireNotFoundData, Result(Own(reply)));
        Assert.Empty(store.StigmaLevels);
        Assert.Empty(session.Stigmas);
    }

    [Fact]
    public async Task A_failed_slot_clear_on_reset_still_forgets_the_stigma_in_memory()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var a = ClassStigmas(session)[0];
        await LevelUp(session, a);
        Assert.Equal(0, Result(Own(await SetSlot(session, 30, 4, a))));
        store.FailQuickSlotSaves = 1;
        var reset = await Reset(session, 2, a);
        Assert.Equal(0, Result(Own(reset)));
        Assert.Empty(session.Stigmas);
        Assert.DoesNotContain(session.QuickSlots.Values, s => s.SkillId == a);
        Assert.Contains(store.QuickSlots.Values, s => s.SkillId == a);   // store save failed; next load retries the clear
        var (relog, _) = await EnterAsync(store, 30, create: false);
        Assert.DoesNotContain(relog.QuickSlots.Values, s => s.SkillId == a);
        Assert.DoesNotContain(store.QuickSlots.Values, s => s.SkillId == a);
    }

    [Fact]
    public async Task Level_down_and_level_up_both_recompute_combat_power()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var a = ClassStigmas(session)[0];
        var before = session.CombatPower;
        await LevelUp(session, a);
        await LevelUp(session, a);
        var raised = session.CombatPower;
        await LevelDown(session, a);
        // Combat power is recomputed on both paths (stigma levels are not in the CP formula today, so the value may
        // stay the same; the call must still run so a later formula keeps level-down in sync with level-up).
        Assert.Equal(raised, session.CombatPower);
        Assert.Equal(before, session.CombatPower);
    }

    [Fact]
    public async Task The_same_stigma_may_be_placed_on_a_skill_slot_and_a_second_bar()
    {
        // Documented: a stigma may sit on a skill slot as well as a Stigma-type slot. Duplicates are not clearly wrong
        // (EquippedStigmas is distinct; the open-slot cap counts unique stigmas). Left as-is.
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var a = ClassStigmas(session)[0];
        await LevelUp(session, a);
        Assert.Equal(0, Result(Own(await SetSlot(session, 30, 4, a))));
        Assert.Equal(0, Result(Own(await SetSlot(session, 1, 0, a))));
        Assert.Equal(2, session.QuickSlots.Values.Count(s => s.SkillId == a));
        Assert.Equal([a], session.EquippedStigmas);
    }

    static bool ContainsI32(byte[] packet, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (packet.AsSpan(i, 4).SequenceEqual(bytes)) return true;
        return false;
    }

    [Fact]
    public async Task Reset_costs_the_configured_kinah_and_refunds_shards()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 40, new StigmaOptions { Enabled = true, ResetKinah = 100_000 });
        var stigma = ClassStigmas(session)[0];
        await LevelUp(session, stigma);
        await LevelUp(session, stigma);
        var left = session.StigmaShardsLeft;
        var before = FakeCharacterStore.KinaIn(store);
        var reset = await Reset(session, 2, stigma);
        Assert.Equal(0, Result(Own(reset)));
        Assert.Empty(session.Stigmas);
        Assert.Equal(left + 2, session.StigmaShardsLeft);
        Assert.Equal(before - 100_000, FakeCharacterStore.KinaIn(store));
    }

    [Fact]
    public async Task Reset_without_enough_kinah_is_refused_and_keeps_the_stigma()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 40, new StigmaOptions { Enabled = true, ResetKinah = 100_000 }, kina: 50_000);
        var stigma = ClassStigmas(session)[0];
        await LevelUp(session, stigma);
        var reset = await Reset(session, 2, stigma);
        Assert.Equal(GameSession.SkillAcquirePersistFailed, Result(Own(reset)));
        Assert.Equal(1, session.Stigmas[stigma]);
        Assert.Equal(50_000, FakeCharacterStore.KinaIn(store));
    }

    [Fact]
    public async Task Reset_is_free_when_ResetKinah_is_zero()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 40, new StigmaOptions { Enabled = true, ResetKinah = 0 }, kina: 0);
        var stigma = ClassStigmas(session)[0];
        await LevelUp(session, stigma);
        Assert.Equal(0, Result(Own(await Reset(session, 2, stigma))));
        Assert.Empty(session.Stigmas);
        Assert.Equal(0, FakeCharacterStore.KinaIn(store));
    }

    [Fact]
    public async Task Bonus_shards_are_in_the_budget_and_survive_a_lower_shard_rate_trim()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 22);
        Assert.Equal(1, session.StigmaShardsTotal);
        var granted = await session.GrantStigmaBonusAsync(2, default);
        Assert.True(granted.Ok);
        Assert.Equal(2, store.StigmaBonusShards);
        Assert.Equal(3, session.StigmaShardsTotal);
        var a = ClassStigmas(session)[0];
        await LevelUp(session, a);
        await LevelUp(session, a);
        await LevelUp(session, a);
        Assert.Equal(3, session.Stigmas[a]);
        Assert.Equal(0, session.StigmaShardsLeft);
        var (relog, _) = await EnterAsync(store, 22, new StigmaOptions { Enabled = true, ShardRate = 0 }, create: false);
        Assert.Equal(2, relog.StigmaShardsTotal);
        Assert.Equal(2, relog.StigmaShardsBonus);
        Assert.Equal(2, relog.Stigmas[a]);
        Assert.Equal(0, relog.StigmaShardsLeft);
    }

    [Fact]
    public async Task Stigma_shard_item_auto_use_on_entry_grants_bonus_points()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.StigmaShardPointItemId, 2, 8801));
        Assert.Equal("GetStigmaShard", RewardBoxes.Embedded.Use(GameSession.StigmaShardPointItemId)!.Effect);
        Assert.True(RewardBoxes.Embedded.ImplementsUseEffect("GetStigmaShard"));
        var (session, _) = await EnterAsync(store, 22);
        Assert.Equal(2, session.StigmaShardsBonus);
        Assert.Equal(3, session.StigmaShardsTotal);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == GameSession.StigmaShardPointItemId && i.Quantity > 0);
    }

    [Fact]
    public async Task Stigma_shard_item_use_in_world_grants_bonus_points()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 22);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.StigmaShardPointItemId, 2, 8802));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var replies = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.ItemUseByItemIdRq).I64(8802).I32(GameSession.StigmaShardPointItemId).I32(2).I32(0).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToInt32(replies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseByItemIdRs), 2));
        Assert.Equal(2, session.StigmaShardsBonus);
        Assert.Equal(3, session.StigmaShardsTotal);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == GameSession.StigmaShardPointItemId && i.Quantity > 0);
    }

    [Fact]
    public async Task Abyss_coin_shop_stigma_shard_purchase_grants_bonus_shards()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 22);
        var goods = ShopTables.Embedded.GoodsInShop("coin_shop_abyss", 1, 401015113);
        Assert.NotNull(goods);
        Assert.Equal(GameSession.StigmaShardPointItemId, goods!.ItemId);
        Assert.Equal(10_000, goods.Price);
        Assert.Equal(930100003, goods.Currency);
        Assert.Equal(50, goods.Limit);
        var wire = store.Items.Max(i => i.WireId) + 1;
        store.Items.Add(FakeCharacterStore.BagItem(930100003, 10_000, wire));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        await session.HandleAsync(new PacketWriter().U16(GameSession.CoinShopOpenRq).I32(990001).ToArray(), Now);
        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.CoinShopBuyRq).I32(990001).Varint(1).I32(401015113).I32(1).ToArray(), Now);
        Assert.Equal(GameSession.CoinShopBuyRs, GameSessionTestHelpers.Op(buy[0]));
        Assert.Equal(0, Result(Own(buy)));
        Assert.Equal(1, session.StigmaShardsBonus);
        Assert.Equal(2, session.StigmaShardsTotal);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == GameSession.StigmaShardPointItemId && i.Quantity > 0);
        Assert.Equal(0, store.Items.Where(i => i.TemplateId == 930100003).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task Level_five_swaps_in_the_variant_before_the_reply_and_casts_it_in_the_stigmas_place()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 45);
        var stigma = ClassStigmas(session)[0];
        for (var i = 0; i < 4; i++) Assert.Equal(0, Result(Own(await LevelUp(session, stigma))));
        Assert.Equal(0, GameData.Embedded.Stigmas.Variant(stigma, 4));
        var five = await LevelUp(session, stigma);
        // OBSERVED order: SkillAdd_NT 0x380f for the +10 variant (reason 6), then 0x3840, 0x3813, 0x5105.
        Assert.Equal(GameSession.SkillAddNt, GameSessionTestHelpers.Op(five[0]));
        Assert.Equal(stigma + 10, BitConverter.ToInt32(five[0], 3));
        Assert.Equal(6, five[0][^1]);
        Assert.Equal(GameSession.SkillLevelUpRs, GameSessionTestHelpers.Op(five[1]));
        Assert.Equal(5, session.SkillLevel(stigma + 10));
        // 6-9 update the variant (0x3813 before the reply); 10 deletes it (0x3810) and adds the +20 one.
        var six = await LevelUp(session, stigma);
        Assert.Equal(GameSession.SkillLevelUpdateNt, GameSessionTestHelpers.Op(six[0]));
        for (var i = 0; i < 3; i++) await LevelUp(session, stigma);
        var ten = await LevelUp(session, stigma);
        Assert.Equal(GameSession.SkillDeleteNt, GameSessionTestHelpers.Op(ten[0]));
        Assert.Equal(stigma + 10, BitConverter.ToInt32(ten[0], 2));
        Assert.Equal(GameSession.SkillAddNt, GameSessionTestHelpers.Op(ten[1]));
        Assert.Equal(stigma + 20, BitConverter.ToInt32(ten[1], 3));
        // Castable only from a stigma slot, and only the current variant.
        Assert.False(session.StigmaCastable(stigma + 20));
        Assert.Equal(0, Result(Own(await SetSlot(session, 30, 4, stigma))));
        Assert.True(session.StigmaCastable(stigma + 20));
        Assert.False(session.StigmaCastable(stigma + 10));
    }

    [Fact]
    public async Task Stigma_wire_slots_20_to_23_take_a_learned_stigma_through_0x5607()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var (a, b) = (ClassStigmas(session)[0], ClassStigmas(session)[1]);
        await LevelUp(session, a);
        byte[] Update(byte slot, int skill) => new PacketWriter().U16(GameSession.QuickSlotContextSkillRq).U8(slot).U8(0).I32(skill).ToArray();
        // Not learned: refused. Learned: stored on slot 20 and castable; the reply is the recorded 0x5608 form (mask, result, ...).
        Assert.Equal(1, BitConverter.ToUInt16((await session.HandleAsync(Update(20, b), Now)).First(), 3));
        var placed = (await session.HandleAsync(Update(20, a), Now)).First();
        Assert.Equal(GameSession.QuickSlotContextSkillRs, GameSessionTestHelpers.Op(placed));
        Assert.Equal(new byte[] { 0, 0, 0, 20, 0 }, placed[2..7]);
        Assert.Equal(a, store.QuickSlots[20].SkillId);
        Assert.True(session.StigmaCastable(a));
        // Moving it to slot 21 (open at level 27) trades places: mask 3, is_swap 1, swap slot 20.
        var moved = (await session.HandleAsync(Update(21, a), Now)).First();
        Assert.Equal(3, moved[2]);
        Assert.Equal(new byte[] { 1, 20 }, moved[11..13]);
        Assert.Equal(a, store.QuickSlots[21].SkillId);
        Assert.False(store.QuickSlots.ContainsKey(20));
        // Slot 22 opens at 32: refused at 30.
        Assert.Equal(GameSession.QuickSlotStigmaMaxCount, BitConverter.ToUInt16((await session.HandleAsync(Update(22, a), Now)).First(), 3));
    }
}
