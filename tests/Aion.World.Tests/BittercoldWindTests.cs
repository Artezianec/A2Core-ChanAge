using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: the Sorcerer's Bittercold Wind 15280000 was accepted and dealt no damage: no Damage row, and its
/// Spawn effect places an invisible summon that has no attacks.</summary>
public sealed class BittercoldWindTests
{
    const int BittercoldWind = 15280000;
    static readonly long Now = 1_700_000_100_000;

    [Fact]
    public void Skill_has_no_damage_row_and_its_summon_has_no_attacks_so_the_zone_carries_the_damage()
    {
        var skill = GameData.Embedded.Skill(BittercoldWind)!;
        Assert.Equal("Bittercold Wind", skill.Name);
        Assert.Empty(skill.Damage);
        Assert.Null(PcSummons.For(BittercoldWind));
        var area = SkillAreas.For(BittercoldWind)!;
        Assert.True(area is { IsArea: true, OnTarget: true, Max: 4 });

        var zone = PcZones.For(BittercoldWind)!;
        Assert.Equal(2920011, zone.Npc);
        Assert.Equal(10, zone.Ticks);
        Assert.NotNull(zone.Damage);
        Assert.Equal(1528000211, zone.Damage!.EffectId);
        Assert.NotNull(zone.CastEffects);
        Assert.Contains(zone.CastEffects!, r => r.EffectId == 1528000011);
        Assert.Contains(zone.CastEffects!, r => r.EffectId == 1528000021);
        Assert.Equal(238, zone.Damage.Flat[0]);
        Assert.Equal(23225, zone.Damage.Ratio[0]);
        Assert.Equal("7", zone.Damage.Value4);
        Assert.Null(PcZones.For(15110000));
    }

    static async Task<(GameSession Session, Dictionary<int, Target> Map, Vector3d At)> Entered()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        return (session, Assert.IsType<Dictionary<int, Target>>(session.Targets), new Vector3d(60_000, 60_000, 0));
    }

    static Target Mob(Dictionary<int, Target> map, int key, Vector3d at) => map[key] = new Target
    {
        Key = key, Name = "Mob", NpcId = 2400032, Hp = 1_000_000, HpMax = 1_000_000, Relationship = 6, Position = at,
    };

    [Fact]
    public async Task Cast_on_a_monster_hurts_every_enemy_around_it_each_tick_until_the_wind_dies()
    {
        var (session, map, at) = await Entered();
        var main = Mob(map, 880_301, at);
        var beside = Mob(map, 880_302, at with { X = at.X + 350 });
        var far = Mob(map, 880_303, at with { X = at.X + 3_000 });

        var placed = session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now);
        var marker = Assert.Single(map.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.Equal(2920011, marker.NpcId);
        Assert.Equal(main.Position, marker.Position);
        Assert.Contains(placed, p => GameSessionTestHelpers.Op(p) == 0x3641);
        Assert.Equal(1, session.PlacedZoneCount);

        await session.TickAsync(Now + 100);
        Assert.Equal(main.HpMax, main.Hp);   // the first tick lands after the hit delay

        var first = await session.TickAsync(Now + 700);
        Assert.True(main.Hp < main.HpMax && beside.Hp < beside.HpMax, "the first tick hurts the target and the enemy beside it");
        Assert.Equal(far.HpMax, far.Hp);
        Assert.Equal(3, first.Count(p => GameSessionTestHelpers.Op(p) == 0x3804));

        var afterFirst = main.Hp;
        await session.TickAsync(Now + 1_700);
        Assert.True(main.Hp < afterFirst, "the wind keeps hurting every second");

        await session.TickAsync(Now + 12_000);
        Assert.Equal(0, session.PlacedZoneCount);
        Assert.DoesNotContain(map.Values, t => t.OwnerKey == session.PlayerKey && !t.CorpseRemoved && t.Hp > 0);
        var ended = main.Hp;
        await session.TickAsync(Now + 20_000);
        Assert.Equal(ended, main.Hp);
        Assert.Equal(far.HpMax, far.Hp);
    }

    [Fact]
    public async Task One_cast_deals_ten_ticks_to_a_lone_monster_and_hits_at_most_four_enemies()
    {
        var (session, map, at) = await Entered();
        var main = Mob(map, 880_401, at);
        var crowd = Enumerable.Range(0, 5).Select(i => Mob(map, 880_410 + i, at with { Y = at.Y + 100 + i * 50 })).ToList();
        session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now);

        var hits = 0;
        for (var t = Now + 100; t <= Now + 12_000; t += 100)
            hits += (await session.TickAsync(t)).Count(p => GameSessionTestHelpers.Op(p) == 0x3804);

        var victims = crowd.Prepend(main).Where(m => m.Hp < m.HpMax).ToList();
        Assert.Equal(4, victims.Count);
        Assert.Contains(main, victims);
        Assert.Equal(10 * 5, hits);   // one marker pulse 0x3804 per tick plus up to four victim hits
        Assert.Equal(2, crowd.Count(m => m.Hp == m.HpMax));
    }

    [Fact]
    public async Task A_second_cast_replaces_the_first_wind()
    {
        var (session, map, at) = await Entered();
        var main = Mob(map, 880_501, at);
        session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now);
        session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now + 2_000);
        Assert.Equal(1, session.PlacedZoneCount);
        Assert.Single(map.Values, t => t.OwnerKey == session.PlayerKey);
    }

    [Fact]
    public async Task Leaving_the_map_takes_the_wind_with_it()
    {
        var (session, map, at) = await Entered();
        var main = Mob(map, 880_601, at);
        session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now);
        session.DismissAllSummonsForTest(Now);
        await session.TickAsync(Now + 1_000);
        Assert.Equal(0, session.PlacedZoneCount);
        Assert.Equal(main.HpMax, main.Hp);
    }

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool CastAccepted(IEnumerable<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }

    static async Task<GameSession> SorcererAsync()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (25, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: "Sorcerer" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 25, WorldId = store.WorldId, TrainingDummy = true },
            new Random(12)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Bitter").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public async Task Wired_cast_is_accepted_plays_skill_start_and_places_the_zone_marker()
    {
        var session = await SorcererAsync();
        Assert.True(session.Knows(BittercoldWind), "Bittercold Wind is auto-learned on the Sorcerer kit");
        session.SetResource('M', session.Resource!.MpMax);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var castAt = Now + 3_000;
        var replies = await session.HandleAsync(Cast(BittercoldWind, mob.Key, mob.Position), castAt);
        Assert.True(CastAccepted(replies));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3802 && ContainsI32(p, BittercoldWind));
        var fxPackets = replies.Where(p => GameSessionTestHelpers.Op(p) == 0x3804).ToList();
        Assert.Equal(3, fxPackets.Count);
        Assert.Contains(fxPackets, p => ContainsI32(p, 1528000011));
        Assert.Contains(fxPackets, p => ContainsI32(p, 1528000021));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3641);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == MechanicTelegraphs.RangeNoticeFilterLocationNt);
        var marker = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey && t.NpcId == 2920011);
        Assert.Equal(2, fxPackets.Count(p => ContainsVarint(p, marker.Key)));
        Assert.Equal(1, session.PlacedZoneCount);
        await session.TickAsync(castAt + 700);
        Assert.True(mob.Hp < mob.HpMax, "the first zone tick damages the target");
    }

    static bool ContainsVarint(byte[] packet, int value)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        while (reader.Offset < packet.Length)
        {
            try
            {
                if ((int)reader.Varint() == value) return true;
            }
            catch { break; }
        }
        return false;
    }
}
