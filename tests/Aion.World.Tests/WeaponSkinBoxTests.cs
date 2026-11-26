using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

public sealed class WeaponSkinBoxTests
{
    const long Now = 1_760_100_000_000;
    const int Wp002Box = 503700069;
    const int MysticLightBoundBox = 503898725;
    const int MysticLightBoundOrbReward = 535710998;
    const int MysticLightBoundOrbSkin = 20012106;
    const int SecretTeatimeBox = 503700094;
    const int SecretTeatimeBowReward = 535710940;
    const int SecretTeatimeBowSkin = 20010104;
    const int SecretTeatimeGsReward = 535710937;
    const int SecretTeatimeGsSkin = 20010101;

    [Fact]
    public async Task Opening_secret_teatime_weapon_skin_box_lists_reward_and_unlocks_closet()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now, level: 55);
        var stack = FakeCharacterStore.BagItem(SecretTeatimeBox, 1, 9902);
        store.Items.Add(stack);
        await session.InventoryChangesAsync(default);

        // Assassin test default: select index 3 (bow).
        var opened = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.OpenBoxRq).I64(stack.WireId).U8(3).I32(1).ToArray(), Now + 1);
        Assert.Equal(RidePackets.OpenBoxRs, Op(opened[0]));
        Assert.Equal(RidePackets.OpenRewardBoxSuccessWire, BitConverter.ToUInt16(opened[0], 2));
        var rewards = ReadOpenBoxRewards(opened[0]);
        Assert.Single(rewards);
        Assert.Equal(SecretTeatimeBowReward, rewards[0].ItemId);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == SecretTeatimeBowReward && i.Quantity > 0);
        Assert.Contains(opened, p => Op(p) == GameSession.SkinUpdateNt);
        Assert.Contains(opened, p => Op(p) == GameSession.SkinInfoNt);
        Assert.Equal(1, session.OwnedSkins.GetValueOrDefault(SecretTeatimeBowSkin));
    }

    [Fact]
    public async Task Secret_teatime_box_quick_use_grants_class_skin_and_survives_relog()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now, level: 55);
        var stack = FakeCharacterStore.BagItem(SecretTeatimeBox, 1, 9903);
        store.Items.Add(stack);
        await session.InventoryChangesAsync(default);

        var used = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.ItemUseRq).I64(stack.WireId).ToArray(), Now + 1);
        Assert.Equal(RidePackets.OpenBoxRs, Op(used[0]));
        var rewards = ReadOpenBoxRewards(used[0]);
        Assert.Equal(SecretTeatimeGsReward, rewards[0].ItemId);
        Assert.Equal(1, store.Skins.GetValueOrDefault(SecretTeatimeGsSkin));

        var relog = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await relog.HandleAsync(Login(), Now + 20);
        await relog.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 20);
        var entry = await relog.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        var info = entry.Single(p => Op(p) == GameSession.SkinInfoNt);
        Assert.Contains(ReadSkinCounts(info), p => p.SkinId == SecretTeatimeGsSkin && p.Count == 1);
    }

    [Fact]
    public async Task Opening_bound_mystic_light_box_with_default_select_unlocks_orb_skin()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now, level: 55);
        var stack = FakeCharacterStore.BagItem(MysticLightBoundBox, 1, 9744);
        store.Items.Add(stack);
        await session.InventoryChangesAsync(default);

        var opened = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.OpenBoxRq).I64(stack.WireId).U8(255).I32(1).ToArray(), Now + 1);
        Assert.Equal(RidePackets.OpenBoxRs, Op(opened[0]));
        Assert.Equal(RidePackets.OpenRewardBoxSuccessWire, BitConverter.ToUInt16(opened[0], 2));
        var rewards = ReadOpenBoxRewards(opened[0]);
        Assert.Single(rewards);
        Assert.Equal(MysticLightBoundOrbReward, rewards[0].ItemId);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == MysticLightBoundBox && i.Quantity > 0);
        Assert.Contains(opened, p => Op(p) == GameSession.SkinUpdateNt);
        Assert.Contains(opened, p => Op(p) == GameSession.SkinInfoNt);
        Assert.Equal(1, session.OwnedSkins.GetValueOrDefault(MysticLightBoundOrbSkin));
    }

    [Fact]
    public async Task Opening_mystic_light_box_with_select_0xff_grants_class_skin_without_default_row()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now, level: 55);
        var stack = FakeCharacterStore.BagItem(Wp002Box, 1, 9910);
        store.Items.Add(stack);
        await session.InventoryChangesAsync(default);

        var opened = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.OpenBoxRq).I64(stack.WireId).U8(0xFF).I32(1).ToArray(), Now + 1);
        Assert.Equal(RidePackets.OpenBoxRs, Op(opened[0]));
        Assert.Equal(RidePackets.OpenRewardBoxSuccessWire, BitConverter.ToUInt16(opened[0], 2));
        var rewards = ReadOpenBoxRewards(opened[0]);
        Assert.Single(rewards);
        Assert.Equal(535710993, rewards[0].ItemId);
        Assert.Equal(1, session.OwnedSkins.GetValueOrDefault(20012101));
    }

    [Fact]
    public async Task Opening_weapon_skin_box_wp_002_grants_class_skin_to_closet()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now, level: 55);
        var stack = FakeCharacterStore.BagItem(Wp002Box, 1, 9901);
        store.Items.Add(stack);
        await session.InventoryChangesAsync(default);

        // GS class index 0 on a Gladiator-style test character (select index picks weapon skin row).
        var opened = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.OpenBoxRq).I64(stack.WireId).U8(0).I32(1).ToArray(), Now + 1);
        Assert.Equal(RidePackets.OpenBoxRs, Op(opened[0]));
        Assert.Equal(RidePackets.OpenRewardBoxSuccessWire, BitConverter.ToUInt16(opened[0], 2));
        Assert.DoesNotContain(store.Items, i => i.TemplateId == Wp002Box && i.Quantity > 0);
        Assert.Contains(opened, p => Op(p) == GameSession.SkinUpdateNt);
        Assert.Contains(opened, p => Op(p) == GameSession.SkinInfoNt);
    }

    static List<(int ItemId, long Count)> ReadOpenBoxRewards(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        r.U16();
        r.I64();
        r.I32();
        r.U8();
        r.I32();
        var count = (int)r.Varint();
        var list = new List<(int, long)>();
        for (var i = 0; i < count; i++)
        {
            var item = r.I32();
            r.U8();
            list.Add((item, r.I32()));
        }
        return list;
    }

    static List<(int SkinId, int Count)> ReadSkinCounts(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var count = (int)r.Varint();
        var list = new List<(int, int)>();
        for (var i = 0; i < count; i++) list.Add((r.I32(), r.I32()));
        return list;
    }
}