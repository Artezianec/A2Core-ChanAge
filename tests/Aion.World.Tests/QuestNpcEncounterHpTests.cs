using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ2102060 Veiled Truth: the first-wave Insane Daeva are NPC_Companion with official 999999 HP during the
/// intro, then ChangeRelationshipEntity makes them Monster_AntiCompanion. They must take level HP or they
/// never die (playtest 2026-11-11, damage landed but HP never reached 0).
/// </summary>
public sealed class QuestNpcEncounterHpTests
{
    const long Now = 1_762_886_400_000;
    const int VeiledTruthMap = 111027;
    const string FirstWaveDagger = "M_D1_Q_Mesmerized_dagger_02_1";
    const string SecondWaveDagger = "M_D1_Q_Mesmerized_dagger_03_2";

    [Fact]
    public void First_wave_Insane_Daeva_spawn_with_official_companion_HP_second_wave_uses_level_HP()
    {
        var targets = Combat.MapTargets(GameData.Embedded, VeiledTruthMap);
        var first = targets.Values.Single(t => t.Name == FirstWaveDagger);
        var second = targets.Values.Single(t => t.Name == SecondWaveDagger);
        Assert.Equal(Combat.OfficialCompanionHp, first.HpMax);
        Assert.False(first.Hostile);
        Assert.True(second.HpMax is > 0 and < Combat.OfficialCompanionHp);
        Assert.Equal(Combat.EncounterHp(first.Level, first.SubType, first.Grade), Combat.EncounterHp(12, "NormalMonster", 2));
    }

    [Fact]
    public void Companions_keep_official_HP_until_they_turn_hostile()
    {
        var salros = GameData.Embedded.Spawns(100011).First(s => s.Name == "N_L0_Starter_HQ002_Salros_01");
        Assert.Equal(Combat.OfficialCompanionHp, Combat.HpFor(salros));
        var t = new Target
        {
            Key = 1, Name = salros.Name, NpcId = salros.NpcId, Level = salros.Level, Relationship = 13,
            Hp = Combat.OfficialCompanionHp, HpMax = Combat.OfficialCompanionHp,
            SubType = salros.SubType, Grade = salros.Grade,
        };
        Assert.False(Combat.TryApplyEncounterHp(t));
        Assert.Equal(Combat.OfficialCompanionHp, t.HpMax);
        t.Relationship = 11;
        Assert.True(Combat.TryApplyEncounterHp(t));
        Assert.Equal(Combat.EncounterHp(t.Level, t.SubType, t.Grade), t.HpMax);
        Assert.Equal(t.HpMax, t.Hp);
        Assert.True(t.HpMax < Combat.OfficialCompanionHp);
    }

    [Fact]
    public async Task Veiled_Truth_first_wave_becomes_killable_when_turned_hostile()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now, level: 12);
        var spawn = GameData.Embedded.Spawns(VeiledTruthMap).First(s => s.Name == FirstWaveDagger);
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        session.Teleport(VeiledTruthMap, at, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var mob = session.Targets.Values.Single(t => t.Name == FirstWaveDagger);
        Assert.Equal(Combat.OfficialCompanionHp, mob.HpMax);
        Assert.False(mob.Hostile);

        ((IMapEventHost)session).Spawn(FirstWaveDagger, 0);
        Assert.False(mob.Dead);
        Assert.Equal(Combat.OfficialCompanionHp, mob.HpMax);
        ((IMapEventHost)session).SetRelationship(FirstWaveDagger, "Monster_AntiCompanion");

        Assert.True(mob.Hostile);
        var expected = Combat.EncounterHp(mob.Level, mob.SubType, mob.Grade);
        Assert.Equal(expected, mob.HpMax);
        Assert.Equal(expected, mob.Hp);
        Assert.True(expected < 5_000, "a level-12 quest mob must be killable in a short fight");

        // Playtest 2026-11-11: Ranger Tempest Shot dealt 146 per hit and never killed at 999999 HP.
        const int playtestHit = 146;
        var hits = (expected + playtestHit - 1) / playtestHit;
        Assert.InRange(hits, 1, 40);
        for (var i = 0; i < hits; i++)
            mob.Hp = Math.Max(0, mob.Hp - playtestHit);
        Assert.True(mob.Dead);
    }
}
