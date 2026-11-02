using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Field treasure chests (Baú): loot from client tables, bag grant, single-use despawn.</summary>
public sealed class FieldTreasureChestTests
{
    const long Now = 1_760_500_000_000;
    const int FieldEventMap = 510036;
    const int S1ChestEnv = 1100130;
    const int S2ChestEnv = 1100312;
    const int FieldEventRewardItem = 534960002;

    static async Task<(GameSession Session, WorldObject Obj, int Key)> NearChestAsync(FakeCharacterStore store, int envId, long now)
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: 1, level: 41);
        var objects = WorldObjects.ObjectsOn(FieldEventMap).ToList();
        var index = objects.FindIndex(o => o.Env == envId);
        Assert.True(index >= 0, $"env {envId} on map {FieldEventMap}");
        var obj = objects[index];
        session.Teleport(FieldEventMap, obj.At, now + 5);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
        await MapEventScenarioHelpers.SyncPositionAsync(session, obj.At, now + 10);
        _ = session.QuestWorldGatingForTests(now + 15);
        return (session, obj, WorldObjects.FirstKey + index);
    }

    static async Task<List<byte[]>> OpenChestAsync(GameSession session, int key, WorldObject obj, long now)
    {
        var replies = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), now + 20);
        if (obj.InteractTime > 0)
            replies.AddRange(await session.TickAsync(now + obj.InteractTime + 200));
        return replies;
    }

    [Fact]
    public void LootTables_resolve_S1_field_treasure_env_by_convention()
    {
        var loot = LootTables.Embedded;
        Assert.True(loot.HasEnvLoot(S1ChestEnv));
        var tables = loot.ResolveEnvLootTableNames(S1ChestEnv, "FE_TH_S2_Box_1", null);
        Assert.Contains("reward_manual_FieldEvent_L_TreasureBox_1100130", tables);
        Assert.True(LootTables.TreasureChestConsumes(tables));
    }

    [Fact]
    public async Task S1_chest_grants_loot_and_despawns()
    {
        var store = new FakeCharacterStore();
        var before = store.Items.Sum(i => i.Quantity);
        var (session, obj, key) = await NearChestAsync(store, S1ChestEnv, Now);
        var replies = await OpenChestAsync(session, key, obj, Now);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3635);
        Assert.Contains(store.Items, i => i.TemplateId == FieldEventRewardItem && i.Quantity > 0);
        Assert.True(store.Items.Sum(i => i.Quantity) > before);
        var again = await OpenChestAsync(session, key, obj, Now + 5000);
        Assert.Contains(again, p => GameSessionTestHelpers.Op(p) == 0x3637 && p.Length >= 4 && p[2] == 1 && p[3] == 0);
    }

    [Fact]
    public async Task S2_chest_grants_loot_once()
    {
        var store = new FakeCharacterStore();
        var (session, obj, key) = await NearChestAsync(store, S2ChestEnv, Now + 20_000);
        await OpenChestAsync(session, key, obj, Now + 20_000);
        Assert.Contains(store.Items, i => i.TemplateId == FieldEventRewardItem && i.Quantity > 0);
        var again = await OpenChestAsync(session, key, obj, Now + 25_000);
        Assert.Contains(again, p => GameSessionTestHelpers.Op(p) == 0x3637 && p.Length >= 4 && p[2] == 1 && p[3] == 0);
    }

    [Fact]
    public async Task PlaytestDebug_logs_env_chest_loot()
    {
        var capture = new CapturePlaytestSink();
        PlaytestDebug.Sink = capture;
        try
        {
            var store = new FakeCharacterStore();
            var (session, obj, key) = await NearChestAsync(store, S2ChestEnv, Now + 40_000);
            await OpenChestAsync(session, key, obj, Now + 40_000);
            Assert.Contains(capture.ActionLog, a => a.Kind == "env_chest_loot" && a.Fields.ContainsKey("envId"));
        }
        finally
        {
            PlaytestDebug.Sink = null;
        }
    }

    sealed class CapturePlaytestSink : IPlaytestDebugSink
    {
        public bool Packets => false;
        public bool Actions => true;
        public bool Entities => false;
        public List<(string Kind, IReadOnlyDictionary<string, object?> Fields)> ActionLog { get; } = [];
        public void Packet(in PlaytestPacketRecord record) { }
        public void Action(string kind, IReadOnlyDictionary<string, object?> fields) => ActionLog.Add((kind, fields));
        public void EntitySpawn(int entityKey, int datId, string? name) { }
    }
}
