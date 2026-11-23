using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>HQ1102040 Coastal Cave (map 210001): the barrier stone wall is an EnvObj_Destroy object the player breaks by attacking.</summary>
public sealed class CoastalCaveStoneWallTests
{
    const long Now = 1_760_007_000_000;
    const int CaveMap = 210001;
    const string Wall = "E_CoastalCave_StoneWall_Hit_001";

    static bool Is(byte[] packet, ushort opcode, int key) =>
        packet.Length > 2 && BitConverter.ToUInt16(packet, 0) == opcode && new PacketReader(packet) { Offset = 2 }.Varint() == key;

    [Fact]
    public void Stone_wall_is_destructible_in_client_data()
    {
        var wall = WorldObjects.ObjectsOn(CaveMap).Single(o => o.EnvName == Wall);
        Assert.True(wall.Destructible);
    }

    [Fact]
    public async Task Attacking_the_stone_wall_breaks_it_and_pays_the_UseEnvObj_step()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 12);
        await session.QuestCommandAsync("QuestAdd", "HQ1102040", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102040", default);
        session.Teleport(CaveMap, GameSessionTestHelpers.MapStartOrDefault(CaveMap), Now + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 60);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "CoastalCave_MapEvent", Now + 100, attempts: 12)
                  ?? throw new InvalidOperationException("CoastalCave_MapEvent did not start");

        static bool WallOpen(MapEventRun r) =>
            r.Open.Any(x => x.Objective.Type == "UseEnvObj" && x.Objective.Names.Contains(Wall));
        await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, Now + 1000, maxSeconds: 240, stopWhen: WallOpen);
        var t = Now + 400_000;
        Assert.True(WallOpen(run), $"run status={run.Status} step={run.StepOrder} open=[{string.Join(',', run.Open.Select(x => x.Objective.Type + ':' + x.Objective.Value(0)))}] map={session.MapId}");

        var objects = WorldObjects.ObjectsOn(CaveMap);
        var index = objects.ToList().FindIndex(o => o.EnvName == Wall);
        var wall = objects[index];
        var key = WorldObjects.FirstKey + index;
        await MapEventScenarioHelpers.SyncPositionAsync(session, wall.At with { X = wall.At.X + 150 }, t);
        t += 200;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var replies = await MapEventScenarioHelpers.CombatCastAsync(session, skill, key, wall.At, t);

        Assert.Contains(replies, p => Is(p, 0x3940, key));
        Assert.DoesNotContain(run.Open, x => x.Objective.Type == "UseEnvObj" && x.Objective.Names.Contains(Wall));
    }
}
