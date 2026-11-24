using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ1101020 / HQ2101050: Ashar turn-in on map 200005 (Dungeon.dat Type Ascension). QuestInstanceExitHold must treat
/// Ascension like Quest so DungeonClear does not start the way out before the ascension reward at N_Dungeon_0005_Ashar_01.
/// </summary>
public sealed class AscensionSubspaceInstanceTests
{
    const long Now = 1_760_110_000_000;
    const int AscensionMap = 200005;
    const int AsharNpcId = 2970218;

    static byte[] Loaded() => new PacketWriter().U16(0x3622).I32(1).ToArray();

    static async Task<GameSession> InSubspaceAwaitingAsharTurnInAsync(FakeCharacterStore store, long now, string questCode)
    {
        var race = questCode.StartsWith("HQ21", StringComparison.Ordinal) ? 2 : 1;
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: race, level: 5);
        await session.QuestCommandAsync("QuestAdd", questCode, default);
        await session.QuestCommandAsync("QuestSetStep", questCode, default, stepOrder: 2);
        await session.QuestCommandAsync("QuestComplete", questCode, default);
        Assert.Equal(QuestLog.Complete, session.Quests!.Get(questCode)!.Status);

        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(AscensionMap);
        Assert.NotEmpty(session.EnterDungeonById(dungeonId, now + 100));
        await session.HandleAsync(Loaded(), now + 200);
        await session.TickAsync(now + 250);
        Assert.Equal(AscensionMap, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
        Assert.Contains(session.Targets!.Values, t => t.NpcId == AsharNpcId && !t.Dead);
        return session;
    }

    [Theory]
    [InlineData("HQ1101020")]
    [InlineData("HQ2101050")]
    public async Task Dungeon_clear_does_not_eject_before_Ashar_ascension_turn_in(string questCode)
    {
        var store = new FakeCharacterStore();
        var session = await InSubspaceAwaitingAsharTurnInAsync(store, Now, questCode);
        var host = (IMapEventHost)session;
        var subRun = session.MapEventRuns.FirstOrDefault(r =>
            r.Name.Equals("Dungeon_0005_Sub_MapEvent", StringComparison.OrdinalIgnoreCase))
            ?? session.MapEventRuns.FirstOrDefault();
        if (subRun is not null)
            host.DungeonClear(subRun);
        else
        {
            host.MapExit("LastPosition", "LastPosition");
            await session.TickAsync(Now + 1);
        }
        await session.TickAsync(Now + 15_000);
        Assert.Equal(AscensionMap, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
    }

    [Fact]
    public async Task Rewarding_HQ1101020_stays_in_subspace_for_flight_tutorial_quest()
    {
        var store = new FakeCharacterStore();
        var session = await InSubspaceAwaitingAsharTurnInAsync(store, Now + 10_000, "HQ1101020");
        var record = session.Quests!.Get("HQ1101020")!;
        var reward = await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(0).ToArray(), Now + 10_300);
        Assert.Contains(reward, p => GameSessionTestHelpers.Op(p) == 0x8d0f);
        Assert.Equal(AscensionMap, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
        Assert.NotNull(session.Quests!.Get("HQ1101021"));
    }
}