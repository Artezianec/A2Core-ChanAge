using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// 2.0.13 handlers whose 2.0.9 opcode is another live message: they must answer with the modern
/// number while 2.0.9 keeps the old one.
/// </summary>
public sealed class ModernOpcodeWireTests
{
    const long Now = 1_760_001_000_000;

    static ushort Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    static WireVersion Pin(WireVersion version)
    {
        var previous = WireContext.Current;
        WireContext.Current = version;
        return previous;
    }

    [Fact]
    public async Task Airspace_battle_schedule_uses_the_2_0_13_opcode_on_modern()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V213);
        try
        {
            var replies = await session.HandleAsync(new byte[] { 0x00, 0x43 }, Now + 1);
            Assert.Contains(replies, p => Op(p) == 0x4301);   // AirspaceBattleNt
            Assert.DoesNotContain(replies, p => Op(p) == 0x372D);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Airspace_battle_schedule_keeps_the_2_0_9_opcode_on_v209()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V209);
        try
        {
            var replies = await session.HandleAsync(new byte[] { 0x2C, 0x37 }, Now + 1);
            Assert.Contains(replies, p => Op(p) == 0x372D);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Abyss_rift_progress_uses_the_2_0_13_opcodes_on_modern()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V213);
        try
        {
            var request = new PacketWriter().U16(0x6302).I32(1).ToArray();
            var replies = await session.HandleAsync(request, Now + 1);
            Assert.Contains(replies, p => Op(p) == 0x6303);   // AbyssRiftProgressInfo_RS
            Assert.DoesNotContain(replies, p => Op(p) == 0x385C);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Combat_state_requests_only_ack_and_combat_ends_in_the_tick()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        // Enter combat the way a monster hit does: TickAsync must be the one that ends it.
        session.NpcHitsPlayer(1, Now + 1);
        Assert.True(session.InCombat);

        var previous = Pin(WireVersion.V213);
        try
        {
            var on = await session.HandleAsync(new byte[] { 0x3A, 0x38 }, Now + 2);
            Assert.Contains(on, p => Op(p) == 0x383B);
            Assert.True(session.InCombat);

            // 0x383C is only acked; it must not swallow the 0x8D21(false) that TickAsync sends.
            var off = await session.HandleAsync(new byte[] { 0x3C, 0x38 }, Now + 3);
            Assert.Contains(off, p => Op(p) == 0x383D);
            Assert.True(session.InCombat);

            var tick = await session.TickAsync(Now + GameSession.CombatTimeoutMs + 10);
            Assert.Contains(tick, p => p.SequenceEqual(GamePackets.CombatState(session.PlayerKey, false)));
            Assert.False(session.InCombat);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Client_key_binding_info_is_stored_and_never_answered()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var packet = new PacketWriter().U16(0xFFB3).String("main-keys").String("sub-keys").ToArray();
        var replies = await session.HandleAsync(packet, Now + 1);
        Assert.Empty(replies);
        Assert.Equal("main-keys", session.ClientMainKeyBindings);
        Assert.Equal("sub-keys", session.ClientSubKeyBindings);
    }

    [Fact]
    public async Task Two_0_13_select_sends_the_limit_status_entry_extras()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(5))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        var previous = Pin(WireVersion.V213);
        try
        {
            var select = new PacketWriter().U16(0xC4F6).I32(0).I32(0).I32(0)
                .Varint((uint)wireId << 16).I32(0).ToArray();
            var replies = await session.HandleAsync(select, Now + 1);
            Assert.Contains(replies, p => Op(p) == 0xFFA5);
            Assert.Contains(replies, p => Op(p) == 0xFFA7);
            Assert.Contains(replies, p => Op(p) == 0xFFAE);
            Assert.Contains(replies, p => Op(p) == 0x5712);
            Assert.Contains(replies, p => Op(p) == 0x8D83);
            Assert.Contains(replies, p => Op(p) == 0xE34C);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Monster_kill_with_kinah_drops_loots_kina_and_emits_limit_status_on_2_0_13()
    {
        var previous = Pin(WireVersion.V213);
        try
        {
            var store = new FakeCharacterStore();
            var shared = new SharedWorld(GameData.Embedded);
            var session = new GameSession(GameData.Embedded,
                new GameSessionOptions
                {
                    World = shared, CharacterLevel = 10, WorldId = store.WorldId, TrainingDummy = true,
                    Rules = new WorldRules { MonsterKinahDrops = true },
                }, new Random(19)) { Store = store };
            await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
            await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
            var wireId = store.States[0].WireId;
            await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
            var spawn = GameData.Embedded.Spawns(1010).First(s => LootTables.Embedded.HasLoot(s.NpcId) && s.Relationship == 6);
            var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
            session.Teleport(1010, at, Now);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
            var target = session.Targets!.Values.First(t => t.NpcId == spawn.NpcId);
            target.Hp = 1;
            var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
            var castAt = Now + 1_000;
            var kill = await MapEventScenarioHelpers.CombatCastAsync(session, skill, target.Key, at, castAt);
            var landAt = castAt + MapEventScenarioHelpers.SkillHitsLandMs(skill);
            Assert.Contains(kill, p => GameSessionTestHelpers.Op(p) == LootPackets.DropNt);

            var uids = session.GroundDrops.ToList();
            Assert.NotEmpty(uids);
            var loot = new PacketWriter().U16(0x5620).Varint((uint)uids.Count);
            foreach (var uid in uids) loot.I64(uid);
            var answers = await session.HandleAsync(loot.U8(0).ToArray(), landAt + 100);

            // 2.0.13 removes each ground drop with its own 24-byte 0x561f.
            var removals = answers.Where(p => Op(p) == LootPackets.DropRemoveNt).ToList();
            Assert.Equal(uids.Count, removals.Count);
            Assert.All(removals, p => Assert.Equal(24, p.Length));

            var kina = store.Items.Where(i => i.TemplateId == GameSession.KinaItemId && i.Location == "bag").Sum(i => i.Quantity);
            Assert.True(kina > 0, "a monster kill with MonsterKinahDrops should leave Kina to loot");
            var status = Assert.Single(answers, p => Op(p) == 0xFFA6);
            Assert.Equal(kina, BitConverter.ToInt64(status, 2));
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Party_roll_uses_the_2_0_13_opcodes_on_modern_and_2_0_9_on_v209()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V213);
        try
        {
            var modern = await session.HandleAsync(new PacketWriter().U16(0x5722).I64(1).ToArray(), Now + 1);
            Assert.Contains(modern, p => Op(p) == 0x5723);   // RollDice_RS
        }
        finally { WireContext.Current = previous; }

        previous = Pin(WireVersion.V209);
        try
        {
            var legacy = await session.HandleAsync(new PacketWriter().U16(0x381D).I64(2).ToArray(), Now + 2);
            Assert.Contains(legacy, p => Op(p) == 0x381E);
        }
        finally { WireContext.Current = previous; }
    }
}
