using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Rending Blow (11010000) opens Smashing Blow (11420000); regression for live "no damage" report.</summary>
public sealed class GladiatorSmashingBlowTests
{
    const int RendingBlow = 11_010_000;
    const int SmashingBlow = 11_420_000;
    const long Now = 1_760_250_000_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool Accepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }

    static async Task<GameSession> GladiatorAsync()
    {
        var store = new FakeCharacterStore { Progress = (25, 0) };
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(91));
        session.LearnForTests(SmashingBlow);
        session.SetResource('M', session.Resource!.MpMax);
        return session;
    }

    [Fact]
    public void Data_exports_rending_opens_smashing_and_smashing_has_damage()
    {
        var data = GameData.Embedded;
        Assert.Equal([SmashingBlow], data.ChainFollowUpIds(RendingBlow));
        var smash = data.Skill(SmashingBlow)!;
        Assert.Equal(2, smash.Damage.Length);
        Assert.Equal(11_010_000, smash.ChainPrev);
        var hits = Combat.SkillHits(data, SmashingBlow, "Gladiator", 25, 110160001, 0, new Random(2));
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.Amount > 0));
    }

    [Fact]
    public async Task Smashing_without_chain_window_is_refused()
    {
        var session = await GladiatorAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        Assert.False(Accepted(await session.HandleAsync(Cast(SmashingBlow, session.PlayerKey, mob.Position), Now + 500)));
    }

    [Fact]
    public async Task After_rending_chain_window_smashing_deals_damage_with_correct_effect_id()
    {
        var session = await GladiatorAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var hp = mob.Hp;
        var castAt = Now + 1_000;
        var rending = await session.HandleAsync(Cast(RendingBlow, session.PlayerKey, mob.Position), castAt);
        Assert.True(Accepted(rending));
        Assert.True(session.ActiveChainSkills.ContainsKey(SmashingBlow), "Rending should open Smashing Blow chain window");
        var smashAt = castAt + 200;
        var smash = await session.HandleAsync(Cast(SmashingBlow, session.PlayerKey, mob.Position), smashAt);
        Assert.True(Accepted(smash));
        var land = await session.TickAsync(smashAt + 2_000);
        Assert.True(mob.Hp < hp, "Smashing Blow should reduce target HP");
        var wired = SkillSideData.WireEffectId(114_200_0011, SmashingBlow);
        Assert.Contains(land, p => GameSessionTestHelpers.Op(p) == 0x3804 && ContainsI32(p, wired));
    }

    [Fact]
    public async Task Smashing_with_locked_enemy_target_still_deals_damage()
    {
        var session = await GladiatorAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var hp = mob.Hp;
        var castAt = Now + 2_000;
        Assert.True(Accepted(await session.HandleAsync(Cast(RendingBlow, mob.Key, mob.Position), castAt)));
        var smashAt = castAt + 200;
        Assert.True(Accepted(await session.HandleAsync(Cast(SmashingBlow, mob.Key, mob.Position), smashAt)));
        await session.TickAsync(smashAt + 2_000);
        Assert.True(mob.Hp < hp);
    }

    [Fact]
    public async Task Specialized_rending_lists_and_casts_matching_smashing_child()
    {
        const int RendingChild = 11_010_010;
        const int SmashingChild = 11_420_010;
        const int Part = 11_010_010;
        var data = GameData.Embedded;
        Assert.Equal([SmashingChild], data.ChainFollowUpIds(RendingChild));
        var store = new FakeCharacterStore { Progress = (45, 0) };
        store.Skills.Add(new SkillState(RendingBlow, 12, null, 12));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(92)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("SmashSpec").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.SetSkillPartForTests(RendingBlow, 1, Part);
        session.LearnForTests(RendingChild);
        session.SetResource('M', session.Resource!.MpMax);
        var kit = session.WithSpecializedForTests([(RendingBlow, 12), (RendingChild, 12)]);
        Assert.Contains(kit, k => k.SkillId == SmashingChild);
        var mob = session.Targets![Combat.TrainingDummyKey];
        session.SetPositionForTests(mob.Position);
        var hp = mob.Hp;
        var castAt = Now + 3_000;
        Assert.True(Accepted(await session.HandleAsync(Cast(RendingChild, session.PlayerKey, mob.Position), castAt)));
        Assert.True(session.ActiveChainSkills.ContainsKey(SmashingChild));
        var smashAt = castAt + 200;
        var land = await session.TickAsync(smashAt);
        land.AddRange(await session.HandleAsync(Cast(SmashingChild, session.PlayerKey, mob.Position), smashAt));
        land.AddRange(await session.TickAsync(smashAt + 2_000));
        Assert.True(mob.Hp < hp);
        var wired = SkillSideData.WireEffectId(data.Skill(SmashingChild)!.Damage[0].EffectId, SmashingChild);
        Assert.Contains(land, p => GameSessionTestHelpers.Op(p) == 0x3804 && ContainsI32(p, wired));
        _ = entry;
    }
}