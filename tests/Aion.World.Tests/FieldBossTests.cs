using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Open-world field bosses (FieldBosses, GameSession.FieldBosses; ported from ckrealm/A2Core 77ac9ed, AGPL-3.0, with retail-like
/// hours): long respawns, EN/RU kill and return notices, and a loot roll for everyone else who fought.
/// </summary>
[Collection(FieldBossCollection.Name)]
public sealed class FieldBossTests : IDisposable
{
    const long Now = 1_760_400_000_000;
    const int Kashapa = 2101122;
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
        // The client shows the text of its language: the Russian goes at culture index 7 of the notice.
        var packet = GameSession.Notice(appeared.English, appeared.Russian);
        Assert.True(packet.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(appeared.Russian)) > 0);
    }

    [Fact]
    public void Delayed_notices_come_due_on_the_wall_clock_once()
    {
        FieldBosses.Announce(new FieldBossNotice("now", "сейчас"));
        FieldBosses.Announce(new FieldBossNotice("later", "позже"), 60_000);
        Assert.Equal(["now"], FieldBosses.TakeDue().Select(n => n.English));
        Assert.Empty(FieldBosses.TakeDue());
        clock += 61_000;
        Assert.Equal(["later"], FieldBosses.TakeDue().Select(n => n.English));
        Assert.Empty(FieldBosses.Pending);

        FieldBosses.Configure(new FieldBossOptions { Announcements = false });
        FieldBosses.Announce(new FieldBossNotice("muted", "тихо"));
        Assert.Empty(FieldBosses.TakeDue());
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

    [Fact]
    public async Task A_field_boss_kill_respawns_hours_later_announces_and_gives_every_fighter_a_roll()
    {
        var world = new SharedWorld(GameData.Embedded);
        var map = world.Map(1010);
        var boss = map.Targets.Values.Single(t => t.Name == "M_L1_FieldBoss_Kashapa_01_1");
        Assert.Equal(Kashapa, boss.NpcId);
        Assert.True(LootTables.Embedded.HasParticipantLoot(Kashapa));
        var near = boss.Position with { X = boss.Position.X + 150 };
        var alice = await EnterAsync(world, "Alice", 1, near);
        var bob = await EnterAsync(world, "Bobby", 2, near with { Y = near.Y + 100 });
        Assert.Same(map, alice.WorldPlayer!.Map);
        Assert.Equal(FieldRank.Boss, alice.FieldRankOf(boss));

        // Both fought it (its hate table); Alice lands the kill.
        map.Ai.Engage(map.Targets, boss, Now + 100, bob.PlayerKey, 5_000);
        map.Ai.Engage(map.Targets, boss, Now + 100, alice.PlayerKey, 1_000);
        boss.Immune = false;
        boss.Hp = 1;
        var t = Now + 1_000;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(alice);
        await MapEventScenarioHelpers.CombatCastAsync(alice, skill, boss.Key, boss.Position, t);
        Assert.True(boss.Dead);
        var due = map.RespawnAt[boss.Key];
        Assert.InRange(due - t, 6 * 3_600_000L - 5_000, 6 * 3_600_000L + 5_000);

        var notices = FieldBosses.TakeDue();
        Assert.Contains(notices, n => n.English == "Alice and 1 other defeated the field boss Kashapa (level 48) in the Light lands!"
            && n.Russian.StartsWith("Alice и ещё 1: полевой босс Кашапа", StringComparison.Ordinal));
        var back = Assert.Single(FieldBosses.Pending, p => p.Notice.English.StartsWith("The field boss Kashapa", StringComparison.Ordinal));
        Assert.InRange(back.DueUtcMs - clock, 6 * 3_600_000L - 10_000, 6 * 3_600_000L + 10_000);

        // Bob rolls his own share on his loop; Alice does not get a second one.
        await bob.TickAsync(t + 100);
        await alice.TickAsync(t + 100);
        Assert.Equal(1, bob.FieldBossShares);
        Assert.Equal(0, alice.FieldBossShares);

        // The corpse goes CorpseMs after the kill, not hours later; the boss stays down until its time.
        Assert.InRange(boss.KilledAtMs, t, t + 5_000);
        await alice.TickAsync(boss.KilledAtMs + GameSession.CorpseMs + 500);
        Assert.True(boss.CorpseRemoved, $"killed at +{boss.KilledAtMs - t} ms, respawn at +{due - t} ms");
        await alice.TickAsync(t + GameSession.RespawnMs + 5_000);
        Assert.True(boss.Dead);
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
    }
}

/// <summary>FieldBosses is process-wide (options, notice queue): its tests run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FieldBossCollection
{
    public const string Name = "Field bosses";
}
