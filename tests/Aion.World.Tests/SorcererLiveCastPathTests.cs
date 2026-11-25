using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live cast wire path for Sorcerer skills reported broken after #628 (HandleAsync, not data-only checks).</summary>
public sealed class SorcererLiveCastPathTests
{
    const long Now = 1_760_300_000_000;
    const int Hellfire = 15_060_000;
    const int FrostBurst = 15_220_000;
    const int FrostPassive = 15_220_037;
    const int FrostMark = 152_200_381;
    const int FlameArrow = 15_210_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool HitDamagePositive(byte[] packet)
    {
        if (GameSessionTestHelpers.Op(packet) != 0x3804) return false;
        var reader = new PacketReader(packet) { Offset = 2 };
        try
        {
            reader.Varint();
            reader.U8();
            reader.U8();
            reader.Varint();
            reader.I32();
            reader.U8();
            reader.I32();
            reader.U32();
            reader.Varint();
            var damage = reader.I32();
            return damage > 0;
        }
        catch { return false; }
    }

    static async Task<GameSession> SorcererAsync(Random rng)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: "Sorcerer" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            rng) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Live").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var mob = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(mob.Position with { X = mob.Position.X + 600 }), Now + 1_000);
        session.SetResource('M', session.Resource!.MpMax);
        return session;
    }

    [Fact]
    public async Task Hellfire_wire_path_releases_projectile_and_positive_damage_hit()
    {
        var session = await SorcererAsync(new Random(41));
        var mob = session.Targets![Combat.TrainingDummyKey];
        var castAt = Now + 5_000;
        var replies = await session.HandleAsync(Cast(Hellfire, mob.Key, session.Position!.Value), castAt);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.True(session.PendingCombatHitCountForTests > 0);
        var projectile = session.NextProjectileForTests();
        Assert.NotNull(projectile);
        var released = await session.TickAsync(projectile!.Value.DueMs + 1);
        Assert.Contains(released, p => GameSessionTestHelpers.Op(p) == 0x3803);
        var landed = await session.TickAsync(castAt + 5_000);
        Assert.Contains(landed, p => HitDamagePositive(p));
        Assert.True(mob.Hp < mob.HpMax);
    }

    [Fact]
    public async Task Frost_burst_refused_without_mark_then_hits_after_mark_proc_skill()
    {
        var session = await SorcererAsync(new Random(42));
        var mob = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position!.Value;
        var noMark = await session.HandleAsync(Cast(FrostBurst, mob.Key, at), Now + 5_000);
        Assert.DoesNotContain(noMark, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

        session.ApplyNpcDebuffForTests(mob, FrostMark, 10_000, Now + 6_000, skillId: FrostBurst);
        Assert.True(session.NpcHasAbnormalGroup(mob.Key, FrostMark, session.PlayerKey));

        var hp = mob.Hp;
        var burst = await session.HandleAsync(Cast(FrostBurst, mob.Key, at), Now + 8_000);
        Assert.Contains(burst, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        var landed = await session.TickAsync(Now + 10_000);
        Assert.Contains(landed, p => HitDamagePositive(p));
        Assert.True(mob.Hp < hp);
    }

    [Fact]
    public async Task Level_55_sorcerer_world_entry_learns_frost_burst_and_its_mark_passive()
    {
        var session = await SorcererAsync(new Random(43));
        Assert.True(session.Knows(FrostBurst));
        Assert.True(session.Knows(FrostPassive));
    }
}