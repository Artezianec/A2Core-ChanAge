using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class StatSheetTests
{
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new Dictionary<string, int>());

    [Fact]
    public void Fixed_template_substat_applies_when_rolled_stats_empty()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.EquippedItem(810160001, 9300, slot: 23));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        Assert.Equal(660, session.GearStat("CombatSpeed"));
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.Equal(660, sheet[282]);
    }

    [Fact]
    public void Main_attributes_appear_on_stat_sheet_from_gear()
    {
        var rolled = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["STR"] = 15, ["DEX"] = 8 });
        var store = new FakeCharacterStore();
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 310340035, 1, "equipment", 12, 1, rolled, Empty, 9301));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.Equal(15, sheet[1]);
        Assert.Equal(8, sheet[2]);
    }

    [Fact]
    public async Task Set_bonus_fixing_damage_on_stat_sheet_when_vigor_tiers_apply()
    {
        const long now = 1_760_100_000_000;
        var store = new FakeCharacterStore();
        // Four different pieces of Primal Vigor (a set counts distinct item ids).
        for (var slot = 23; slot <= 26; slot++)
            store.Items.Add(FakeCharacterStore.EquippedItem(810130001 + (slot - 23) * 10_000, 9400 + slot, slot));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 45);
        session.ReloadInventoryForTests(store.Items);
        Assert.Equal(210, session.SetStatForTests("FixingDamage"));
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.True(sheet.GetValueOrDefault(19) >= 210);
    }

    [Fact]
    public async Task Set_bonus_ignores_duplicate_equipped_item_ids()
    {
        const long now = 1_760_100_000_000;
        var store = new FakeCharacterStore();
        for (var slot = 23; slot <= 26; slot++)
            store.Items.Add(FakeCharacterStore.EquippedItem(810160001, 9450 + slot, slot));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 45);
        session.ReloadInventoryForTests(store.Items);
        Assert.Equal(0, session.SetStatForTests("FixingDamage"));
    }

    [Fact]
    public async Task Mp_max_includes_equipment_stats()
    {
        const long now = 1_760_100_000_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 45);
        session.RefreshStats();
        var before = session.Resource.MpMax;
        var rolled = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["MPMax"] = 120 });
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 310340035, 1, "equipment", 12, 1, rolled, Empty, 9601));
        session.ReloadInventoryForTests(store.Items);
        session.RefreshStats();
        Assert.Equal(before + 120, session.Resource.MpMax);
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.Equal(before + 120, sheet[199]);
    }

    [Fact]
    public async Task Refresh_stats_sends_combat_power_update_only_when_it_changes()
    {
        const long now = 1_760_100_000_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 45);
        session.RefreshStats();
        Assert.DoesNotContain(session.RefreshStats(), p => GameSessionTestHelpers.Op(p) == 0x3656);
        var cpBefore = session.CombatPower;
        store.Items.Add(FakeCharacterStore.EquippedItem(110440035, 9602, slot: 1));
        session.ReloadInventoryForTests(store.Items);
        var replies = session.RefreshStats();
        Assert.NotEqual(cpBefore, session.CombatPower);
        Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x3656);
        Assert.DoesNotContain(session.RefreshStats(), p => GameSessionTestHelpers.Op(p) == 0x3656);
    }

    [Fact]
    public void Weapon_accuracy_sheet_matches_player_weapon_stats()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.EquippedItem(110440035, 9500, slot: 1));
        store.Items.Add(FakeCharacterStore.EquippedItem(115040035, 9501, slot: 2));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.Equal(session.WeaponStatsForTests().Accuracy, sheet[318]);
    }
}
