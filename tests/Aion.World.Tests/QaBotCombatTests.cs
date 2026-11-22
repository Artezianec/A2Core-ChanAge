using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>QA bot combat must tick through skill hit delay (same as MapEventScenarioHelpers.CombatCastAsync).</summary>
public sealed class QaBotCombatTests
{
    const long Now = 1_760_090_000_000;

    [Fact]
    public async Task QA_bot_cast_pattern_kills_field_mob_and_grants_xp()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 4);
        session.NoCool = true;
        const int map = 1000;
        session.Teleport(map, GameSessionTestHelpers.MapStartOrDefault(map), Now + 10);
        await session.HandleAsync(BotRequests.LoadComplete(1), Now + 20);
        _ = session.QuestWorldGatingForTests(Now + 30);
        await session.TickAsync(Now + 40);
        var prey = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2101164 && !t.Dead)
            ?? session.Targets!.Values.First(t => !t.Dead && t.Hostile);
        var hp0 = prey.Hp;
        var xp0 = session.Experience;
        var skill = QaBotCombat.BasicAttackSkill(GameData.Embedded, session);
        await MapEventScenarioHelpers.SyncPositionAsync(session, prey.Position with { X = prey.Position.X + 80 }, Now + 100);
        prey = session.Targets!.Values.First(t => t.Key == prey.Key);
        var state = new QuestDriverState();
        var castAt = Now + 200;
        state.CombatBusyUntil = castAt + QaBotCombat.HitLandMs(GameData.Embedded, skill);
        foreach (var packet in new[]
                 {
                     BotRequests.CastBegin(),
                     BotRequests.Cast(skill, prey.Key, 0, prey.Position, 1),
                     BotRequests.CastEnd(),
                 })
            await session.HandleAsync(packet, castAt);
        await session.TickAsync(state.CombatBusyUntil);
        prey = session.Targets!.Values.First(t => t.Key == prey.Key);
        Assert.True(prey.Hp < hp0 || prey.Dead || session.Experience > xp0,
            $"expected damage or XP (hp {hp0}->{prey.Hp}, xp {xp0}->{session.Experience})");
    }

    [Fact]
    public async Task HQ1101018_step4_QA_attack_damages_watchstone()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 4);
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        for (var i = 0; i < 4 && session.Quests!.Get("HQ1101018") is { Step: var s } && s < 4; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1101018", default);
        var watch = session.Targets!.Values.First(t => t.NpcId == 2290017);
        _ = session.QuestWorldGatingForTests(Now + 50);
        var skill = QaBotCombat.BasicAttackSkill(GameData.Embedded, session);
        await MapEventScenarioHelpers.SyncPositionAsync(session, watch.Position with { X = watch.Position.X + 80 }, Now + 100);
        watch = session.Targets!.Values.First(t => t.Key == watch.Key);
        var hp0 = watch.Hp;
        var castAt = Now + 200;
        foreach (var packet in new[]
                 {
                     BotRequests.CastBegin(),
                     BotRequests.Cast(skill, watch.Key, 0, watch.Position, 1),
                     BotRequests.CastEnd(),
                 })
            await session.HandleAsync(packet, castAt);
        await session.TickAsync(castAt + QaBotCombat.HitLandMs(GameData.Embedded, skill));
        watch = session.Targets!.Values.First(t => t.Key == watch.Key);
        Assert.True(watch.Hp < hp0 || watch.Dead, $"watchstone should take damage on step 4 (hp {hp0}->{watch.Hp})");
    }

    [Fact]
    public async Task Quest_turn_in_increments_QaQuestBrain_quests_rewarded()
    {
        var store = new FakeCharacterStore();
        var world = new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = world, CharacterLevel = 1, WorldId = store.WorldId },
            new Random(3)) { Store = store, Bot = true, QaBot = true };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(BotRequests.Create("QA_Rwd", 1), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(BotRequests.Select((uint)wireId << 16), Now);
        await session.HandleAsync(BotRequests.LoadComplete(1), Now);
        await session.QuestCommandAsync("QuestAdd", "HQ1101010", default);
        await session.QuestCommandAsync("QuestComplete", "HQ1101010", default);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 10, new QaBotOptions());
        var turnIn = Now + 200;
        var rewardReq = brain.Think(turnIn).Requests;
        Assert.NotEmpty(rewardReq);
        foreach (var packet in rewardReq)
            await session.HandleAsync(packet, turnIn);
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get("HQ1101010")!.Status);
        brain.Think(turnIn + 50);
        Assert.Equal(1, brain.QuestsRewarded);
    }

    [Fact]
    public void HQ1101018_step5_goal_is_Trigger_Q_Clione2_not_map_start()
    {
        var quest = QuestBook.Embedded.Find("HQ1101018")!;
        var step = quest.Steps.First(s => s.Order == 5);
        var dest = QuestTeleportResolver.Resolve(quest, step, GameData.Embedded);
        Assert.NotNull(dest);
        var trigger = WorldObjects.TriggersOn(1000).First(t => t.Name == "Trigger_Q_Clione2");
        Assert.Equal(1000, dest!.Map);
        Assert.True((dest.At.X - trigger.At.X) * (dest.At.X - trigger.At.X) + (dest.At.Y - trigger.At.Y) * (dest.At.Y - trigger.At.Y) < 500 * 500,
            $"goal should be near Trigger_Q_Clione2 ({trigger.At.X},{trigger.At.Y}), got ({dest.At.X},{dest.At.Y})");
    }
}