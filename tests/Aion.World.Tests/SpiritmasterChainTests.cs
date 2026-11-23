using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Spiritmaster / Elementalist FixSlot combo (Cold Shock → Vacuum Explosion): wire ids stay on base chain_next rows.</summary>
public sealed class SpiritmasterChainTests
{
    const long Now = 1_760_040_000_000;
    const int ColdShock = 16_010_000, VacuumExplosion = 16_020_000, EarthTremor = 16_030_000;
    const int ColdShockM5 = 16_010_050;

    static bool ContainsI32(byte[] packet, int value)
    {
        var needle = BitConverter.GetBytes(value);
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (packet.AsSpan(i, 4).SequenceEqual(needle)) return true;
        return false;
    }

    static async Task<GameSession> ElementalistAsync(int level = 25)
    {
        var creation = Enumerable.Range(1, 64).First(c =>
            GameSessionTestHelpers.LiveGameData.Profile(c, 1) is { Race: 1, Class: "Elementalist" });
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameSessionTestHelpers.LiveGameData), CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            new Random(16)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ElChain").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).ToArray();

    [Fact]
    public void BasicChainWireStepId_maps_mastery_tier_to_base_combo_rows()
    {
        var data = GameData.Embedded;
        Assert.Equal(ColdShock, data.BasicChainWireStepId(ColdShockM5));
        Assert.Equal(VacuumExplosion, data.BasicChainWireStepId(16_020_050));
        Assert.Equal([VacuumExplosion], data.ChainFollowUpIds(ColdShockM5));
        Assert.Equal([EarthTremor], data.ChainFollowUpIds(16_020_050));
    }

    [Fact]
    public void ChainCastReplies_maps_mastery_parent_to_base_follow_up()
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(1));
        var replies = session.ChainCastRepliesForTests(ColdShockM5, Now);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818
            && ContainsI32(p, ColdShock) && ContainsI32(p, VacuumExplosion) && !ContainsI32(p, 16_020_050));
    }

    [Fact]
    public async Task Mastery_tier_cold_shock_opens_vacuum_explosion_with_base_wire_ids()
    {
        var data = GameData.Embedded;
        var session = await ElementalistAsync();
        for (var i = 0; i < 4; i++)
            await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(ColdShock).ToArray(), Now + 1000L * (i + 1));

        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position ?? Vector3d.Zero;
        // Client still casts the FixSlot base id on the wire; mastery is carried in 0x5100 level.
        var replies = await session.HandleAsync(Cast(ColdShock, dummy.Key, at), Now + 5000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818
            && ContainsI32(p, ColdShock) && ContainsI32(p, VacuumExplosion) && !ContainsI32(p, 16_020_050));
        Assert.True(session.ActiveChainSkills.ContainsKey(VacuumExplosion));
        Assert.DoesNotContain(16_020_050, session.ActiveChainSkills.Keys);

        var lockout = SkillCooldown.LockoutMs(data.Skill(VacuumExplosion)!, projectile: SkillAreas.For(VacuumExplosion) is { Projectile: > 0 });
        var follow = await session.HandleAsync(Cast(VacuumExplosion, dummy.Key, at), Now + 5000 + lockout + 50);
        Assert.Contains(follow, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.True(session.ActiveChainSkills.ContainsKey(EarthTremor));
    }

    [Fact]
    public async Task Basic_attack_combo_step_two_accepted_after_cold_shock()
    {
        var session = await ElementalistAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var atk = new PacketWriter().U16(GameSession.BasicAtkRq).U8(0).Varint(dummy.Key).ToArray();
        var first = await session.HandleAsync(atk, Now + 8000);
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == 0x3818 && ContainsI32(p, VacuumExplosion));
        var data = GameSessionTestHelpers.LiveGameData;
        var gap = SkillCooldown.LockoutMs(data.Skill(ColdShock)!, projectile: SkillAreas.For(ColdShock) is { Projectile: > 0 });
        var second = await session.HandleAsync(atk, Now + 8000 + Math.Max(gap, session.HeldCastLockoutRemainingMs(Now + 8000) + 1));
        Assert.Equal((ushort)0, BitConverter.ToUInt16(second.First(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
        Assert.Contains(second, p => GameSessionTestHelpers.Op(p) == 0x3802 && ContainsI32(p, VacuumExplosion));
    }
}