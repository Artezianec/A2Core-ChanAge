using System.Buffers.Binary;
using System.Text;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Open-world field bosses (FieldBosses, GameSession.FieldBosses; ported from ckrealm/A2Core 77ac9ed, AGPL-3.0, with retail-like
/// hours and official-style personal rewards): long respawns, optional EN/RU broadcasts, a personal bag reward for every
/// contributor, and WorldMapContentsInfo 0x3A0C/0x3A0D.
/// </summary>
[Collection(FieldBossCollection.Name)]
public sealed class FieldBossTests : IDisposable
{
    const long Now = 1_760_400_000_000;
    const int Kashapa = 2101122;
    const int KashapaChest = 533700373;   // Box_FieldBoss_A_46_b, client participant table (100%)
    const int GartuaChest = 533700375;    // Box_FieldBoss_A_48_b (Odium Gartua / Island of Eternity)
    long clock = 1_800_000_000_000;

    public FieldBossTests()
    {
        FieldBosses.Configure(FieldBossOptions.Default);
        FieldBosses.Clear();
        FieldBosses.UtcMs = () => clock;
    }

    public void Dispose()
    {
        FieldBosses.Configure(FieldBossOptions.Default);
        FieldBosses.Clear();
        FieldBosses.UtcMs = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    static Target Npc(string name, string subType = "HeroMonster", int grade = 6, int relationship = 6) => new()
    {
        Key = 2001, Name = name, Hp = 100, HpMax = 100, Level = 48, SubType = subType, Grade = grade, Relationship = relationship,
    };

    [Fact]
    public void Ranks_open_world_hero_monsters_only()
    {
        Assert.Equal(FieldRank.Boss, FieldBosses.Rank(Npc("M_L1_FieldBoss_Kashapa_01_1"), inInstance: false));
        Assert.Equal(FieldRank.Boss, FieldBosses.Rank(Npc("M_D2_FieldBoss_Lagta_02_001", grade: 2), inInstance: false));
        Assert.Equal(FieldRank.Boss, FieldBosses.Rank(Npc("M_D1_IoI_OdiumGartua_01_001"), inInstance: false));
        Assert.Equal(FieldRank.Named, FieldBosses.Rank(Npc("M_L1_TF_Kalnif_Named_01_1", grade: 5), inInstance: false));
        Assert.Equal(FieldRank.None, FieldBosses.Rank(Npc("M_L1_FieldBoss_Kashapa_01_1"), inInstance: true));
        Assert.Equal(FieldRank.None, FieldBosses.Rank(Npc("M_L1_Wolf_01", subType: "NormalMonster", grade: 3), inInstance: false));
        Assert.Equal(FieldRank.None, FieldBosses.Rank(Npc("M_AR2_ACRA_DebGuard_02_001_QuestSup", grade: 2), inInstance: false));
        Assert.Equal(FieldRank.None, FieldBosses.Rank(Npc("M_L1_FieldBoss_Kashapa_01_1", relationship: 1), inInstance: false));
    }

    [Fact]
    public void Respawns_take_retail_like_hours_with_per_boss_overrides()
    {
        var rng = new Random(3);
        Assert.Equal(6 * 3_600_000L, FieldBosses.RespawnMs(Npc("M_L1_FieldBoss_Kashapa_01_1"), FieldRank.Boss, rng));
        Assert.Equal(11 * 3_600_000L, FieldBosses.RespawnMs(Npc("M_D1_FieldBoss_Lagta_02_1"), FieldRank.Boss, rng));
        Assert.Equal(11 * 3_600_000L, FieldBosses.RespawnMs(Npc("M_D1_IoI_OdiumGartua_01_001"), FieldRank.Boss, rng));
        for (var i = 0; i < 20; i++)
            Assert.InRange(FieldBosses.RespawnMs(Npc("M_L1_TF_Kalnif_Named_01_1", grade: 5), FieldRank.Named, rng), 30 * 60_000L, 60 * 60_000L);

        FieldBosses.Configure(new FieldBossOptions { BossRespawnMinMinutes = 120, BossRespawnMaxMinutes = 180, RespawnMinutesByName = new() { ["Kashapa"] = 45 } });
        Assert.Equal(45 * 60_000L, FieldBosses.RespawnMs(Npc("M_L1_FieldBoss_Kashapa_01_1"), FieldRank.Boss, rng));
        Assert.InRange(FieldBosses.RespawnMs(Npc("M_D1_FieldBoss_BeritraD_01_001"), FieldRank.Boss, rng), 120 * 60_000L, 180 * 60_000L);
        Assert.Throws<InvalidOperationException>(() => new FieldBossOptions { BossRespawnMinMinutes = 10, BossRespawnMaxMinutes = 5 }.Validated());
        Assert.Throws<InvalidOperationException>(() => new FieldBossOptions { AnnouncementScope = "Everyone" }.Validated());
    }

    [Fact]
    public void Official_defaults_do_not_broadcast()
    {
        Assert.Equal(FieldBossBroadcastScope.Off, FieldBossOptions.Default.EffectiveScope());
        Assert.False(FieldBossOptions.Default.Announcements);
        Assert.False(FieldBossOptions.Default.ChatBroadcast);
        Assert.True(FieldBossOptions.Default.ParticipantNotices);
        Assert.True(FieldBossOptions.Default.SharedLoot);
        FieldBosses.Announce(FieldBosses.Defeated("Alice", 1, "M_L1_FieldBoss_Kashapa_01_1", 48, 1010));
        Assert.Empty(FieldBosses.TakeDue());
        // Legacy configs that only set Announcements=true still broadcast server-wide.
        FieldBosses.Configure(new FieldBossOptions { Announcements = true });
        Assert.Equal(FieldBossBroadcastScope.Server, FieldBosses.Options.EffectiveScope());
        FieldBosses.Configure(new FieldBossOptions { AnnouncementScope = "Zone" });
        Assert.Equal(FieldBossBroadcastScope.Zone, FieldBosses.Options.EffectiveScope());
    }

    [Fact]
    public void Notices_name_the_boss_in_English_and_Russian()
    {
        Assert.Equal("Kashapa", FieldBosses.DisplayName("M_L1_FieldBoss_Kashapa_01_1"));
        Assert.Equal("Odium Gartua", FieldBosses.DisplayName("M_D1_IoI_OdiumGartua_01_001"));
        Assert.Equal("Island Of Eternity Boss", FieldBosses.DisplayName("World_L_A_Single_IslandOfEternity_Boss_1"));
        Assert.Equal("Beritra", FieldBosses.DisplayName("M_D1_FieldBoss_BeritraD_01_001"));
        var defeated = FieldBosses.Defeated("Alice", 2, "M_L1_FieldBoss_Kashapa_01_1", 48, 1010);
        Assert.Equal("Alice and 2 others defeated the field boss Kashapa (level 48) in the Light lands!", defeated.English);
        Assert.Equal("Alice и ещё 2: полевой босс Кашапа (48 ур.) повержен — земли Света!", defeated.Russian);
        var appeared = FieldBosses.Appeared("M_D1_IoI_OdiumGartua_01_001", 51, 1110);
        Assert.Equal("The field boss Odium Gartua (level 51) has appeared in the Dark lands.", appeared.English);
        Assert.Equal("Полевой босс Одиум Гартуа (51 ур.) появился — земли Тьмы.", appeared.Russian);
        var personal = FieldBosses.DefeatedPersonal("M_L1_FieldBoss_Kashapa_01_1");
        Assert.Equal("Defeated Kashapa.", personal.English);
        Assert.Equal("Повержен: Кашапа.", personal.Russian);
        Assert.Equal("Reward collected.", FieldBosses.RewardCollected.English);
        var packet = GameSession.Notice(appeared.English, appeared.Russian);
        Assert.True(packet.AsSpan().IndexOf(Encoding.UTF8.GetBytes(appeared.Russian)) > 0);
        Assert.True(GameSession.Notice(personal.English, personal.Russian).AsSpan().IndexOf(Encoding.UTF8.GetBytes(personal.English)) > 0);
    }

    [Fact]
    public void Delayed_notices_come_due_on_the_wall_clock_once()
    {
        FieldBosses.Configure(new FieldBossOptions { AnnouncementScope = "Server" });
        FieldBosses.Announce(new FieldBossNotice("now", "сейчас"));
        FieldBosses.Announce(new FieldBossNotice("later", "позже"), 60_000);
        Assert.Equal(["now"], FieldBosses.TakeDue().Select(n => n.English));
        Assert.Empty(FieldBosses.TakeDue());
        clock += 61_000;
        Assert.Equal(["later"], FieldBosses.TakeDue().Select(n => n.English));
        Assert.Empty(FieldBosses.Pending);

        FieldBosses.Configure(new FieldBossOptions { AnnouncementScope = "Off", Announcements = false });
        FieldBosses.Announce(new FieldBossNotice("muted", "тихо"));
        Assert.Empty(FieldBosses.TakeDue());
    }

    [Fact]
    public void Participant_tables_use_real_client_chest_ids()
    {
        Assert.True(LootTables.Embedded.HasParticipantLoot(Kashapa));
        Assert.True(LootTables.Embedded.HasParticipantLoot(2400800));   // Odium Gartua
        Assert.True(LootTables.Embedded.HasParticipantLoot(2101074));   // Island of Eternity (Light-side Gartua)
        var kashapa = LootTables.Embedded.RollParticipant(Kashapa, race: 1, new Random(1));
        Assert.Contains(kashapa, i => i.ItemId == KashapaChest && i.Count == 1);
        var gartua = LootTables.Embedded.RollParticipant(2400800, race: 2, new Random(1));
        Assert.Contains(gartua, i => i.ItemId == GartuaChest && i.Count == 1);
        Assert.Contains(LootTables.Embedded.RollParticipant(2101074, race: 1, new Random(1)), i => i.ItemId == GartuaChest);
    }

    static async Task<GameSession> EnterAsync(SharedWorld world, string name, uint creation, Vector3d at)
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { World = world, WorldId = 1204, CharacterLevel = 50 }, new Random((int)creation))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States.Single().WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.Teleport(1010, at, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        Assert.Equal(1010, session.MapId);
        return session;
    }

    static bool HasNotice(IEnumerable<byte[]> packets, string text) =>
        packets.Any(p => p.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(p) == GameSession.NoticeNt
            && Encoding.UTF8.GetString(p).Contains(text, StringComparison.Ordinal));

    static async Task<(SharedWorld World, Target Boss, GameSession Alice, GameSession Bob, long KillAt)> KillKashapaAsync(
        Action<NpcAi.Brain, GameSession, GameSession>? prepare = null)
    {
        var world = new SharedWorld(GameData.Embedded);
        var map = world.Map(1010);
        var boss = map.Targets.Values.Single(t => t.Name == "M_L1_FieldBoss_Kashapa_01_1");
        Assert.Equal(Kashapa, boss.NpcId);
        var near = boss.Position with { X = boss.Position.X + 150 };
        var alice = await EnterAsync(world, "Alice", 1, near);
        var bob = await EnterAsync(world, "Bobby", 2, near with { Y = near.Y + 100 });
        map.Ai.Engage(map.Targets, boss, Now + 100, bob.PlayerKey, 5_000);
        map.Ai.Engage(map.Targets, boss, Now + 100, alice.PlayerKey, 1_000);
        if (prepare is not null && map.Ai.Brains.TryGetValue(boss.Key, out var brain)) prepare(brain, alice, bob);
        boss.Immune = false;
        boss.Hp = 1;
        var t = Now + 1_000;
        await MapEventScenarioHelpers.CombatCastAsync(alice, MapEventScenarioHelpers.BasicAttackSkill(alice), boss.Key, boss.Position, t);
        Assert.True(boss.Dead);
        return (world, boss, alice, bob, t);
    }

    [Fact]
    public async Task A_field_boss_kill_respawns_hours_later_and_gives_every_contributor_a_personal_chest()
    {
        var (_, boss, alice, bob, t) = await KillKashapaAsync();
        var due = alice.WorldPlayer!.Map!.RespawnAt[boss.Key];
        Assert.InRange(due - t, 6 * 3_600_000L - 5_000, 6 * 3_600_000L + 5_000);

        // Official default: no server-wide broadcast.
        Assert.Empty(FieldBosses.TakeDue());
        Assert.Empty(FieldBosses.Pending);
        var clock = Assert.Single(FieldBosses.StatesOnMap(1010), s => s.EntityKey == boss.Key);
        Assert.False(clock.Spawned);
        Assert.Equal(due, clock.RespawnAtMs);
        Assert.Equal(Kashapa, clock.NpcId);

        var bobTick = await bob.TickAsync(t + 100);
        var aliceTick = await alice.TickAsync(t + 100);
        Assert.Equal(1, bob.FieldBossShares);
        Assert.Equal(1, alice.FieldBossShares);
        Assert.Contains(((FakeCharacterStore)bob.Store!).Items, i => i.TemplateId == KashapaChest);
        Assert.Contains(((FakeCharacterStore)alice.Store!).Items, i => i.TemplateId == KashapaChest);
        Assert.True(HasNotice(bobTick, "Defeated Kashapa."));
        Assert.True(HasNotice(bobTick, "Reward collected."));
        Assert.True(HasNotice(aliceTick, "Defeated Kashapa."));
        Assert.True(HasNotice(aliceTick, "Reward collected."));
        // No single-tagger ground loot from the boss's kill table.
        Assert.Empty(alice.GroundDrops);
        Assert.Empty(bob.GroundDrops);

        Assert.InRange(boss.KilledAtMs, t, t + 5_000);
        await alice.TickAsync(boss.KilledAtMs + GameSession.CorpseMs + 500);
        Assert.True(boss.CorpseRemoved, $"killed at +{boss.KilledAtMs - t} ms, respawn at +{due - t} ms");
        await alice.TickAsync(t + GameSession.RespawnMs + 5_000);
        Assert.True(boss.Dead);
    }

    [Fact]
    public async Task A_healer_who_never_damaged_the_boss_still_gets_the_chest()
    {
        var world = new SharedWorld(GameData.Embedded);
        var map = world.Map(1010);
        var boss = map.Targets.Values.Single(t => t.Name == "M_L1_FieldBoss_Kashapa_01_1");
        var near = boss.Position with { X = boss.Position.X + 150 };
        var alice = await EnterAsync(world, "Alice", 1, near);
        var cleric = await EnterAsync(world, "Cleric", 3, near with { Y = near.Y + 80 });
        map.Ai.Engage(map.Targets, boss, Now + 100, alice.PlayerKey, 2_000);
        Assert.True(map.Ai.Brains.TryGetValue(boss.Key, out var brain));
        brain.Healers.Add(cleric.PlayerKey);
        boss.Immune = false;
        boss.Hp = 1;
        var t = Now + 1_000;
        await MapEventScenarioHelpers.CombatCastAsync(alice, MapEventScenarioHelpers.BasicAttackSkill(alice), boss.Key, boss.Position, t);
        await cleric.TickAsync(t + 100);
        await alice.TickAsync(t + 100);
        Assert.Equal(1, cleric.FieldBossShares);
        Assert.Contains(((FakeCharacterStore)cleric.Store!).Items, i => i.TemplateId == KashapaChest);
    }

    [Fact]
    public async Task Server_broadcasts_can_be_turned_back_on()
    {
        FieldBosses.Configure(new FieldBossOptions { AnnouncementScope = "Server" });
        var (_, _, alice, _, _) = await KillKashapaAsync();
        var notices = FieldBosses.TakeDue();
        Assert.Contains(notices, n => n.English == "Alice and 1 other defeated the field boss Kashapa (level 48) in the Light lands!"
            && n.Russian.StartsWith("Alice и ещё 1: полевой босс Кашапа", StringComparison.Ordinal)
            && n.Scope == FieldBossBroadcastScope.Server && n.MapId == 1010);
        var back = Assert.Single(FieldBosses.Pending, p => p.Notice.English.StartsWith("The field boss Kashapa", StringComparison.Ordinal));
        Assert.Equal(FieldBossBroadcastScope.Server, back.Notice.Scope);
        Assert.InRange(back.DueUtcMs - clock, 6 * 3_600_000L - 10_000, 6 * 3_600_000L + 10_000);
        _ = alice;
    }

    [Fact]
    public async Task World_map_contents_lists_spawned_bosses_and_leaves_the_timer_list_empty()
    {
        var world = new SharedWorld(GameData.Embedded);
        var map = world.Map(1010);
        var boss = map.Targets.Values.Single(t => t.Name == "M_L1_FieldBoss_Kashapa_01_1");
        var alice = await EnterAsync(world, "Alice", 1, boss.Position with { X = boss.Position.X + 150 });
        var replies = await alice.HandleAsync(PacketWriter.Opcode(GameSession.WorldMapContentsInfoRq).I32(1010).ToArray(), Now + 100);
        var packet = Assert.Single(replies, p => p.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(p) == GameSession.WorldMapContentsInfoRs);
        var reader = new PacketReader(packet) { Offset = 2 };
        Assert.Equal(0, reader.I32());       // result
        Assert.Equal(1010, reader.I32());    // map
        Assert.Equal(0u, reader.Varint());   // field-named list empty (WorldMapFieldNamed table not exported)
        var count = reader.Varint();
        Assert.True(count >= 1, $"expected at least Kashapa in the spawned-boss list, got {count}");
        var found = false;
        for (var i = 0; i < (int)count; i++)
        {
            var uid = reader.I32();
            var npc = reader.I32();
            _ = reader.F32(); _ = reader.F32(); _ = reader.F32();
            if (uid == boss.Key && npc == Kashapa) found = true;
        }
        Assert.True(found, "Kashapa should appear in WorldMapBossInfo while spawned");
        Assert.Equal(0u, reader.Varint());   // rifts
        Assert.Equal(0u, reader.Varint());   // env
    }

    [Fact]
    public async Task Off_keeps_the_ordinary_respawn_and_says_nothing()
    {
        FieldBosses.Configure(new FieldBossOptions { Enabled = false });
        var world = new SharedWorld(GameData.Embedded);
        var map = world.Map(1010);
        var boss = map.Targets.Values.Single(t => t.Name == "M_L1_FieldBoss_Kashapa_01_1");
        var alice = await EnterAsync(world, "Alice", 1, boss.Position with { X = boss.Position.X + 150 });
        boss.Hp = 1;
        var t = Now + 1_000;
        await MapEventScenarioHelpers.CombatCastAsync(alice, MapEventScenarioHelpers.BasicAttackSkill(alice), boss.Key, boss.Position, t);
        Assert.True(boss.Dead);
        Assert.InRange(map.RespawnAt[boss.Key] - t, 0, GameSession.RespawnMs + 5_000);
        Assert.Empty(FieldBosses.TakeDue());
        Assert.Empty(FieldBosses.Pending);
        Assert.Empty(FieldBosses.StatesOnMap(1010));
        Assert.Equal(0, alice.FieldBossShares);
    }
}

/// <summary>FieldBosses is process-wide (options, notice queue): its tests run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FieldBossCollection
{
    public const string Name = "Field bosses";
}
