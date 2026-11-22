using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBotFollowUpTests
{
    const long Now = 1_760_100_500_000;

    [Fact]
    public void Kill_XP_is_officially_zero_below_level_10()
    {
        Assert.Equal(0, GameSession.KillReward(4, 4, 3, instance: false));
        Assert.Equal(0, GameSession.KillReward(9, 8, 3, instance: false));
        Assert.True(GameSession.KillReward(10, 8, 3, instance: false) > 0);
    }

    [Fact]
    public void Dungeon_L_0016_portal_entry_resolves_on_Poeta()
    {
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(210016);
        Assert.True(dungeonId > 0);
        var portal = WorldObjects.PortalEntryOf(dungeonId);
        Assert.NotNull(portal);
        Assert.Equal(1000, portal!.Value.Map);
    }

    [Fact]
    public void Ranger_basic_has_cast_reach_beyond_melee_kill_radius()
    {
        var data = GameData.Embedded;
        var skill = data.LearnedSkills("Ranger", 4)
            .Select(s => s.SkillId)
            .First(id => data.Skill(id) is { Damage.Length: > 0, Cost.Mp: 0, Cost.Sp: 0 });
        var reach = QaBotCombat.CastReach(data, skill);
        Assert.True(reach > QuestStepDriver.KillReach, $"ranger skill {skill} reach {reach} should exceed melee {QuestStepDriver.KillReach}");
    }

    [Fact]
    public async Task HQ1101018_watchstone_takes_multiple_swing_busy_attacks()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 4);
        session.NoCool = true;
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        for (var i = 0; i < 4 && session.Quests!.Get("HQ1101018") is { Step: var s } && s < 4; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1101018", default);
        var watch = session.Targets!.Values.First(t => t.NpcId == 2290017);
        _ = session.QuestWorldGatingForTests(Now + 10);
        await MapEventScenarioHelpers.SyncPositionAsync(session, watch.Position with { X = watch.Position.X + 80 }, Now + 50);
        watch = session.Targets!.Values.First(t => t.Key == watch.Key);
        var skill = QaBotCombat.BasicAttackSkill(GameData.Embedded, session);
        var hp0 = watch.Hp;
        var castAt = Now + 100;
        for (var swing = 0; swing < 6 && watch.Hp >= hp0 * 0.5 && !watch.Dead; swing++)
        {
            foreach (var packet in new[]
                     {
                         BotRequests.CastBegin(),
                         BotRequests.Cast(skill, watch.Key, 0, watch.Position, (byte)(swing + 1)),
                         BotRequests.CastEnd(),
                     })
                await session.HandleAsync(packet, castAt);
            castAt += QaBotCombat.SwingBusyMs(GameData.Embedded, session, skill);
            await session.TickAsync(castAt);
            watch = session.Targets!.Values.First(t => t.Key == watch.Key);
        }
        Assert.True(watch.Hp < hp0 || watch.Dead, $"expected sustained swings to damage watchstone ({hp0}->{watch.Hp})");
    }
}