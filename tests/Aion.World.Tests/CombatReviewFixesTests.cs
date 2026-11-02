using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class CombatReviewFixesTests
{
    [Fact]
    public void Ratio_permille_uses_long_for_large_hp_pools()
    {
        var req = new SkillResourceCondition("HPRatio_More", 5000);
        Assert.True(SkillResourceThreshold.Met(req, 2_100_000_000, 2_147_000_000, 0, 1));
    }

    [Fact]
    public void Unknown_skill_effect_condition_fails_closed()
    {
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded) }, new Random(1));
        var cond = new SkillEffectConditions.Condition("TotallyUnknownCondition", ["1"]);
        var ctx = session.BuildEffectConditionContext(null, null, 1, 1, true, 0);
        Assert.False(SkillEffectConditions.Met(cond, ctx));
    }

    [Fact]
    public void Skill_level_raises_damage_with_candidate_scale()
    {
        var row = new DamageRow([0], [10_000], null, 0);
        var rng = new Random(9);
        var low = Combat.RowHits(GameData.Embedded, [row], "Gladiator", 45, 110160001, 0, rng, skillLevel: 1).Max(h => h.Amount);
        var high = Combat.RowHits(GameData.Embedded, [row], "Gladiator", 45, 110160001, 0, rng, skillLevel: 5).Max(h => h.Amount);
        Assert.True(high > low);
    }

    [Fact]
    public void Crit_damage_stat_amplifies_critical_hits()
    {
        var row = new DamageRow([0], [10_000], null, 0);
        int CritAmount(int critBp)
        {
            for (var seed = 0; seed < 500; seed++)
            {
                var hit = Combat.RowHits(GameData.Embedded, [row], "Gladiator", 45, 110160001, 0, new Random(seed),
                    extraCritical: 50_000, critDamageBp: critBp).First();
                if (hit.Critical) return hit.Amount;
            }
            throw new InvalidOperationException("no critical in sample");
        }
        Assert.True(CritAmount(10_000) > CritAmount(0));
    }

    [Fact]
    public void Negative_accuracy_does_not_raise_effective_defense()
    {
        var row = new DamageRow([0], [10_000], null, 0);
        var rng = new Random(3);
        var baseline = Combat.RowHits(GameData.Embedded, [row], "Gladiator", 45, 110160001, 100, rng, extraAccuracy: 0).Max(h => h.Amount);
        rng = new Random(3);
        var worse = Combat.RowHits(GameData.Embedded, [row], "Gladiator", 45, 110160001, 100, rng, extraAccuracy: -500).Max(h => h.Amount);
        Assert.Equal(baseline, worse);
    }

    [Fact]
    public async Task Reflect_kill_runs_npc_death_path()
    {
        const long now = 1_760_030_000_000;
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(530800003, 1, 82));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, now, level: 55);
        var at = session.Targets![Combat.TrainingDummyKey].Position;
        session.Teleport(1010, at, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, now);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(82).ToArray(), now + 100);
        session.Targets![Combat.TrainingDummyKey].Hp = 1;
        session.SetResource('H', session.Resource!.HpMax);
        session.NpcHitsPlayer(500, now + 200, killerKey: Combat.TrainingDummyKey);
        var dummy = session.Targets[Combat.TrainingDummyKey];
        Assert.True(dummy.Dead);
    }

    [Fact]
    public async Task Out_of_range_cast_is_refused()
    {
        const int Snipe = 14_020_000;
        const long now = 1_760_030_100_000;
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var data = GameSessionTestHelpers.LiveGameData;
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(5, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Range").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var here = session.Position!.Value;
        dummy.Position = here with { X = here.X + 50_000 };
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(Snipe).U8(2).Varint(dummy.Key)
            .F32(0).Vec3(dummy.Position).ToArray();
        var replies = await session.HandleAsync(cast, now + 5000);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
    }

    [Fact]
    public void Chain_sibling_window_closes_when_one_branch_is_cast()
    {
        const long now = 1_760_030_200_000;
        var session = GameSessionTestHelpers.Session(new FakeCharacterStore(), level: 40);
        session.SetActiveChainSkillForTests(11_999_002, now + 3000);
        session.SetActiveChainSkillForTests(11_999_003, now + 3000);
        session.ChainCastRepliesForTests(11_999_002, now);
        Assert.False(session.ActiveChainSkills.ContainsKey(11_999_003));
    }

    [Fact]
    public void Npc_dot_refresh_preserves_total_damage()
    {
        var target = new Target { Key = 9001, Name = "t", Hp = 1000, HpMax = 10_000, Position = Vector3d.Zero };
        var session = GameSessionTestHelpers.Session(new FakeCharacterStore(), level: 45);
        session.ApplyNpcDebuffForTests(target, 100000701, 3000, 1_760_030_300_000);
        session.ApplyNpcDebuffForTests(target, 100000701, 3000, 1_760_030_300_100);
        var replies = session.ApplyNpcDebuffForTests(target, 100000701, 3000, 1_760_030_300_200);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x382b);
    }

    [Fact]
    public async Task Heal_with_non_positive_amount_is_ignored()
    {
        const long now = 1_760_030_400_000;
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), now, level: 45);
        var hp = session.Resource!.Hp;
        var replies = session.ReceiveFriendlyRow(["HpHeal", "0", "0"], now);
        Assert.Empty(replies);
        Assert.Equal(hp, session.Resource.Hp);
    }

    [Fact]
    public async Task Burn_ap_with_zero_roll_does_not_mutate_ap()
    {
        const long now = 1_760_030_450_000;
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), now, level: 45);
        session.SetApForTests(500, 1000);
        Assert.Empty(session.BurnAp(["ApBurn", "0", "0"], new Random(0)));
        Assert.Equal(500, session.ApForTests);
    }

    [Fact]
    public async Task Die_clears_pending_combat_hits()
    {
        const long now = 1_760_030_500_000;
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), now, level: 45);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var hpBefore = dummy.Hp;
        session.ScheduleCombatHitForTests(dummy.Key, dueMs: now + 100, damage: 9999);
        Assert.Equal(1, session.PendingCombatHitCountForTests);
        session.NpcHitsPlayer(session.Resource!.HpMax, now + 50, killerKey: dummy.Key);
        Assert.Equal(0, session.PendingCombatHitCountForTests);
        await session.TickAsync(now + 200);
        Assert.Equal(hpBefore, dummy.Hp);
    }
}
