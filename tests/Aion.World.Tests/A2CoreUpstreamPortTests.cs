using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Ports from Addiboii/A2Core update-26262663 (build 2.0.9.0).</summary>
public sealed class A2CoreUpstreamPortTests
{
    const long Now = 1_760_001_000_000;
    static GameData Data => GameSessionTestHelpers.LiveGameData;

    [Fact]
    public void LevelAfterFailure_never_raises_level_on_negative_fail_penalty()
    {
        Assert.Equal(3, GameSession.LevelAfterFailure(3, -1));
        Assert.Equal(2, GameSession.LevelAfterFailure(3, 1));
        Assert.Equal(0, GameSession.LevelAfterFailure(0, 1));
    }

    [Fact]
    public async Task Failed_rune_enchant_keeps_level_when_dat_fail_penalty_is_negative_one()
    {
        var store = new FakeCharacterStore();
        var empty = JsonSerializer.SerializeToElement(new { });
        const int RuneTemplate = 310900001;
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", RuneTemplate, 1, "bag", null, 1, empty,
            JsonSerializer.SerializeToElement(new { enchant = 1, enchant_bonus = 0 }), 9001));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", GameSession.KinaItemId, 500_000, "bag", null, 1, empty, empty, 9002));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 930100023, 10_000, "bag", null, 1, empty, empty, 9003));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 531110003, 50, "bag", null, 1, empty, empty, 9004));

        var session = new GameSession(Data, new GameSessionOptions { CharacterLevel = 55, WorldId = 1204 }, new Random(21)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        session.EnchantRoll = () => 9999;
        _ = await session.HandleAsync(new PacketWriter().U16(0x5622).I64(9001).Varint(0).ToArray(), Now);
        var rune = store.Items.Single(i => i.WireId == 9001);
        Assert.Equal(1, rune.Upgrades.GetProperty("enchant").GetInt32());
    }

    [Fact]
    public async Task CollectItem_in_bag_advances_quest_on_inventory_sync()
    {
        const int Trace = 631900001;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 16);
        var book = session.Quests!.Book.Find("HQ1102125")!;
        session.Quests!.Set(QuestLog.New("HQ1102125", book) with { Step = 3, Uid = 51 });
        store.Items.Add(FakeCharacterStore.BagItem(Trace, 1, 70));
        var before = session.Quests!.Get("HQ1102125")!.Step;
        var packets = await session.InventoryChangesAsync(default);
        var after = session.Quests!.Get("HQ1102125")!.Step;
        Assert.Equal(3, before);
        Assert.Equal(4, after);
        Assert.Contains(packets, p => GameSessionTestHelpers.Op(p) == 0x8d10);
    }

    [Fact]
    public async Task Level_up_starts_auto_quest_and_sends_it_after_level_messages()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(Data,
            new GameSessionOptions { World = new SharedWorld(Data), CharacterLevel = 1, WorldId = store.WorldId, TrainingDummy = true },
            new Random(9)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var hq = QuestLog.New("HQ1101021", QuestBook.Embedded.Find("HQ1101021")!) with { Uid = 50 };
        store.Quests.Add(hq);
        session.Quests!.Set(hq);
        Assert.DoesNotContain(store.Quests, q => q.Code == "AQ1602000");
        var gain = Enumerable.Range(1, 4).Sum(l => Data.NeedExp(l));
        session.ShareExperience(gain);
        var replies = await session.TickAsync(Now + 1500);
        Assert.Equal(5, session.Level);
        Assert.True(store.Quests.Any(q => q.Code == "AQ1602000"), "AQ1602000 should auto-acquire at level 5");
        var levelIdx = replies.FindIndex(b => GameSessionTestHelpers.Op(b) == 0x8d08);
        var addIdx = replies.FindIndex(b => GameSessionTestHelpers.Op(b) == 0x8d11);
        Assert.True(levelIdx >= 0 && addIdx > levelIdx, "QuestAdd should follow LevelUpdate on level-up recheck");
    }

    [Fact]
    public async Task QuestClassReward_pays_gladiator_weapon_on_HQ1102010()
    {
        const int GladiatorWeapon = 110150028;
        const int TemplarWeapon = 110250028;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 10_000, race: 1, level: 12);
        var record = QuestLog.New("HQ1102010", QuestBook.Embedded.Find("HQ1102010")!) with { Status = QuestLog.Complete, Uid = 52 };
        session.Quests!.Set(record);
        await store.SaveQuestAsync(store.States[0].Summary.CharacterId, record, default);
        var reward = await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32(52).I32(0).ToArray(), Now + 11_000);
        Assert.NotNull(reward);
        Assert.Contains(reward!, b => GameSessionTestHelpers.Op(b) == 0x8d0f);
        Assert.Contains(store.Items, i => i.TemplateId == GladiatorWeapon && i.Quantity >= 1);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == TemplarWeapon);
    }
}