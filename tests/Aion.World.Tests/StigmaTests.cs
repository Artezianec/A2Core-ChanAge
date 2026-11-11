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

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, int level, StigmaOptions? stigmas = null, bool create = true)
    {
        store.Progress = (level, 0);
        var session = NewSession(store, level, stigmas ?? On);
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        if (create) await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
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
        Assert.Equal(75, table.Spent(20));
    }

    [Fact]
    public void Options_are_validated_and_off_by_default()
    {
        Assert.False(StigmaOptions.Default.Enabled);
        Assert.Throws<InvalidOperationException>(() => new StigmaOptions { Slots = 7 }.Validated());
        Assert.Throws<InvalidOperationException>(() => new StigmaOptions { MaxLevel = 21 }.Validated());
        Assert.Throws<InvalidOperationException>(() => new StigmaOptions { ShardRate = -1 }.Validated());
        new StigmaOptions { Slots = 6, ShardRate = 3, MaxLevel = 20 }.Validated();
    }

    [Fact]
    public async Task Off_a_stigma_stays_unlearnable_as_before()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30, StigmaOptions.Default);
        var stigma = ClassStigmas(session)[0];
        var reply = await LevelUp(session, stigma);
        Assert.Equal(GameSession.SkillAcquireNotFoundData, Result(reply[0]));
        Assert.Empty(store.StigmaLevels);
        Assert.False(session.StigmasOn);
    }

    [Fact]
    public async Task Below_level_22_a_stigma_is_refused()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 21);
        var reply = await LevelUp(session, ClassStigmas(session)[0]);
        Assert.Equal(GameSession.SkillAcquireNotEnoughCharLevel, Result(reply[0]));
        Assert.Equal(0, session.StigmaShardsTotal);
        Assert.Equal(0, session.StigmaSlotsOpen);
        Assert.Empty(store.StigmaLevels);
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
        Assert.Equal(0, Result(learn[0]));
        Assert.Equal(0x5101, GameSessionTestHelpers.Op(learn[1]));
        Assert.Equal(GameSession.SkillPointUpdateNt, GameSessionTestHelpers.Op(learn[^1]));
        Assert.Equal(2, learn[^1][3]);   // ESkillPointType StigmaShard
        Assert.Equal(1, learn[^1][4]);   // one shard spent
        Assert.Equal(1, store.StigmaLevels[stigma]);
        Assert.Equal(1, session.SkillLevel(stigma));

        var raise = await LevelUp(session, stigma);
        Assert.Equal(0, Result(raise[0]));
        Assert.Equal(GameSession.SkillLevelUpdateNt, GameSessionTestHelpers.Op(raise[1]));
        Assert.Equal(2, session.SkillLevel(stigma));

        // Levels 3-5 cost 1, 6 costs 2: 1+1+1+1+1+2 = 7, then 7 costs 2 (9 spent), then 8 needs 2 more.
        for (var i = 0; i < 5; i++) Assert.Equal(0, Result((await LevelUp(session, stigma))[0]));
        Assert.Equal(7, session.Stigmas[stigma]);
        Assert.Equal(0, session.StigmaShardsLeft);
        var refused = await LevelUp(session, stigma);
        Assert.Equal(GameSession.SkillAcquireNotEnoughSkillPoint, Result(refused[0]));
        Assert.Equal(7, store.StigmaLevels[stigma]);
    }

    [Fact]
    public async Task Another_class_stigma_is_not_a_stigma_here()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 30);
        var other = GameData.Embedded.Stigmas.Classes.Where(c => c != session.ClassName).SelectMany(GameData.Embedded.Stigmas.ClassStigmas).First();
        var reply = await LevelUp(session, other);
        Assert.NotEqual(0, Result(reply[0]));
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
        Assert.Equal(0, Result(down[0]));
        Assert.Equal(1, store.StigmaLevels[stigma]);
        Assert.Equal(left + 1, session.StigmaShardsLeft);
        Assert.NotEqual(0, Result((await LevelDown(session, stigma))[0]));
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
        Assert.Equal(0, Result((await SetSlot(session, 30, 4, a))[0]));
        Assert.True(session.StigmaCastable(a));

        var reset = await Reset(session, 2, 0);
        Assert.Equal(GameSession.SkillLevelResetRs, GameSessionTestHelpers.Op(reset[0]));
        Assert.Equal(0, Result(reset[0]));
        Assert.Equal(GameSession.RemoveSkillListNt, GameSessionTestHelpers.Op(reset[1]));
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
        Assert.Equal(0, Result((await Reset(session, 2, a))[0]));
        Assert.Equal([b], store.StigmaLevels.Keys);
    }

    [Fact]
    public async Task Slots_take_only_learned_stigmas_up_to_the_open_count()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 22);
        Assert.Equal(1, session.StigmaSlotsOpen);
        var (a, b) = (ClassStigmas(session)[0], ClassStigmas(session)[1]);
        Assert.Equal(GameSession.SkillAcquireNotFoundData, Result((await SetSlot(session, 30, 4, a))[0]));   // not learned
        await LevelUp(session, a);
        Assert.False(session.StigmaCastable(a));   // learned, not on a slot
        Assert.Equal(0, Result((await SetSlot(session, 30, 4, a))[0]));
        Assert.True(session.StigmaCastable(a));
        // Level 22 gives one shard: b cannot be learned, and a second stigma would not fit one slot anyway.
        store.StigmaLevels[b] = 1;
        var (relog, _) = await EnterAsync(store, 22, create: false);
        Assert.Empty(relog.Stigmas);   // two stigmas cost 2 > 1 shard: trimmed and refunded at load
        Assert.Empty(store.StigmaLevels);
        Assert.Equal(GameSession.QuickSlotMismatchSlotType, Result((await SetSlot(relog, 31, 4, RendingBlow))[0]));
    }

    [Fact]
    public async Task Second_stigma_beyond_the_open_slots_is_refused()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, 26);   // 5 shards, 1 slot
        var (a, b) = (ClassStigmas(session)[0], ClassStigmas(session)[1]);
        await LevelUp(session, a);
        await LevelUp(session, b);
        Assert.Equal(0, Result((await SetSlot(session, 30, 4, a))[0]));
        Assert.Equal(GameSession.QuickSlotStigmaMaxCount, Result((await SetSlot(session, 31, 4, b))[0]));
        Assert.Equal(0, Result((await SetSlot(session, 30, 4, b))[0]));   // replacing a's slot is fine
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

    static bool ContainsI32(byte[] packet, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (packet.AsSpan(i, 4).SequenceEqual(bytes)) return true;
        return false;
    }
}
