using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

/// <summary>Alt+O fashion sets: Quna buy, equip/unequip (including costume-only slots), and hide/unhide.</summary>
public sealed class FashionSetWindowTests
{
    const long Now = 1_760_100_000_000;
    const int EyeWear = 70003101;
    const int Gauntlet = 20004109;
    const int GreatswordSkin = 20005101;
    const int SwordSkin = 20005102;
    const int TorsoSkin = 30067101;
    static readonly int[] ApronSet = [40095201, 40095202, 40095203, 40095205, 40095206];

    [Fact]
    public async Task Empty_world_entry_keeps_the_official_SkinInfo_bytes()
    {
        var store = new FakeCharacterStore();
        var entry = await EnterAsync(store);
        Assert.Contains(entry, p => p.SequenceEqual(Convert.FromHexString("5B8D000000010000")));
    }

    [Fact]
    public async Task Quna_skin_set_purchase_grants_the_set_and_charges_once()
    {
        const int goods = 100106;
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Quna.Seed(store.AccountId, 100_000);
        var session = await EnterWorldAsync(store, Now, level: 55);

        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goods).I32(1).ToArray(), Now);

        Assert.Equal(0, BitConverter.ToUInt16(buy[0], 2));
        Assert.Equal(56_500, (await store.Quna.BalanceAsync(store.AccountId)).Balance);
        Assert.Contains(buy, p => Op(p) == GameSession.SkinUpdateNt);
        Assert.All(ApronSet, id => Assert.Equal(1, session.OwnedSkins[id]));
        Assert.DoesNotContain(store.Items, i => i.TemplateId == 535510221 && i.Quantity > 0);
    }

    [Fact]
    public async Task Zero_price_founders_weapon_skin_is_bought_with_Quna_and_unlocks()
    {
        const int goods = 100088;
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Quna.Seed(store.AccountId, 100_000);
        var session = await EnterWorldAsync(store, Now, level: 55);

        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goods).I32(1).ToArray(), Now);

        Assert.Equal(0, BitConverter.ToUInt16(buy[0], 2));
        Assert.Equal(100_000, (await store.Quna.BalanceAsync(store.AccountId)).Balance);
        Assert.Contains(buy, p => Op(p) == GameSession.SkinUpdateNt);
        Assert.Equal(1, session.OwnedSkins[GreatswordSkin]);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == 535710897 && i.Quantity > 0);
    }

    [Fact]
    public async Task Costume_only_slot_equips_and_unequips_without_an_item()
    {
        var (session, _) = await SkinnedAsync(EyeWear);
        var on = await session.HandleAsync(SkinEquip(23, EyeWear, dbid: 42), Now);
        Assert.Equal(0, BitConverter.ToUInt16(on[0], 2));
        Assert.Equal(42, ReadEquipReply(on[0]).DbId);
        var shown = ReadChanges(on.Single(p => Op(p) == 0x561c)).Single();
        Assert.Equal((12, 0, EyeWear), (shown.Slot, shown.AfterItem, shown.AfterSkin));

        var off = await session.HandleAsync(SkinEquip(23, 0), Now);
        Assert.Equal(0, BitConverter.ToUInt16(off[0], 2));
        Assert.False(session.SkinEquips.ContainsKey(23));
        var cleared = ReadChanges(off.Single(p => Op(p) == 0x561c)).Single();
        Assert.Equal((12, 0), (cleared.Slot, cleared.AfterSkin));
    }

    [Fact]
    public async Task Gauntlet_skin_shows_on_the_main_hand_when_no_weapon_is_worn()
    {
        var (session, store) = await SkinnedAsync(Gauntlet);
        await UnequipWeaponAsync(session, store);
        var on = await session.HandleAsync(SkinEquip(26, Gauntlet), Now);
        Assert.Equal(0, BitConverter.ToUInt16(on[0], 2));
        Assert.Equal(Gauntlet, ReadChanges(on.Single(p => Op(p) == 0x561c)).Single(c => c.Slot == 1).AfterSkin);

        var off = await session.HandleAsync(SkinEquip(26, 0), Now);
        Assert.Equal(0, ReadChanges(off.Single(p => Op(p) == 0x561c)).Single(c => c.Slot == 1).AfterSkin);
    }

    [Fact]
    public async Task Only_the_worn_weapon_skin_shows_and_unequipping_it_clears_the_model()
    {
        var (session, _) = await SkinnedAsync(SwordSkin, GreatswordSkin);
        Assert.Equal("Greatsword", LiveGameData.WeaponType(session.EquippedWeapon));

        var other = await session.HandleAsync(SkinEquip(8, SwordSkin), Now);
        Assert.Equal(0, BitConverter.ToUInt16(other[0], 2));
        Assert.DoesNotContain(other, p => Op(p) == 0x561c);

        var on = await session.HandleAsync(SkinEquip(10, GreatswordSkin), Now);
        Assert.Equal(GreatswordSkin, ReadChanges(on.Single(p => Op(p) == 0x561c)).Single(c => c.Slot == 1).AfterSkin);

        var off = await session.HandleAsync(SkinEquip(10, 0), Now);
        Assert.Equal(0, ReadChanges(off.Single(p => Op(p) == 0x561c)).Single(c => c.Slot == 1).AfterSkin);
        Assert.Equal(SwordSkin, session.SkinEquips[8]);
    }

    [Fact]
    public async Task Taking_off_the_weapon_keeps_its_skin()
    {
        var (session, store) = await SkinnedAsync(GreatswordSkin);
        await session.HandleAsync(SkinEquip(10, GreatswordSkin), Now);
        var weapon = store.Items.Single(i => i.Location == "equipment" && i.Slot == 1);
        var unequip = await session.HandleAsync(new PacketWriter().U16(0xe249).I64(weapon.WireId).U8(1).I32(0).ToArray(), Now);
        var hand = ReadChanges(unequip.Single(p => Op(p) == 0x561c)).Single(c => c.Slot == 1);
        Assert.Equal(0, hand.AfterItem);
        Assert.Equal(GreatswordSkin, hand.AfterSkin);
        Assert.True(hand.AfterDisplay);
    }

    [Fact]
    public async Task Torso_skin_survives_equipping_the_armour()
    {
        var (session, store) = await SkinnedAsync(TorsoSkin);
        await session.HandleAsync(SkinEquip(3, TorsoSkin), Now);
        var armour = FakeCharacterStore.BagItem(210130001, 1, 8802);
        store.Items.Add(armour);
        await session.InventoryChangesAsync(default);
        var worn = await session.HandleAsync(new PacketWriter().U16(0xe247).I64(armour.WireId).U8(5).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(worn[0], 2));
        var torso = ReadChanges(worn.Single(p => Op(p) == 0x561c)).Single(c => c.Slot == 4);
        Assert.Equal(210130001, torso.AfterItem);
        Assert.Equal(TorsoSkin, torso.AfterSkin);
    }

    [Fact]
    public async Task Hide_and_unhide_persist_across_a_relog()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now);
        var hide = await session.HandleAsync(Visibility(2, false), Now);
        Assert.Equal(new PacketWriter().U16(GameSession.EquipSlotVisibilityRs).U16(0).U8(1).U8(2).Bool(false).ToArray(), hide[0]);
        var hidden = ReadChanges(hide.Single(p => Op(p) == 0x561c)).Single(c => c.Slot == 2);
        Assert.False(hidden.AfterDisplay);
        Assert.Equal("[2]", store.Settings["skin_hidden"].GetRawText());

        var show = await session.HandleAsync(Visibility(2, true), Now);
        Assert.True(ReadChanges(show.Single(p => Op(p) == 0x561c)).Single(c => c.Slot == 2).AfterDisplay);
        Assert.Equal("[]", store.Settings["skin_hidden"].GetRawText());

        var refused = await session.HandleAsync(Visibility(0, false), Now);
        Assert.Equal(1, BitConverter.ToUInt16(refused[0], 2));
        Assert.Equal("[]", store.Settings["skin_hidden"].GetRawText());

        await session.HandleAsync(Visibility(4, false), Now);
        var again = await RelogAsync(store);
        var info = again.Single(p => Op(p) == GameSession.SkinInfoNt);
        var (visual, display) = ReadHidden(info).Single();
        Assert.Equal(4, visual);
        Assert.False(display);
    }

    static async Task<List<byte[]>> EnterAsync(FakeCharacterStore store)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3))
        {
            Store = store
        };
        await session.HandleAsync(Login(), Now);
        await session.HandleAsync(LightCreate(), Now);
        await session.HandleAsync(Select(store.States[0].WireId), Now);
        return await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    }

    static async Task<List<byte[]>> RelogAsync(FakeCharacterStore store)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3))
        {
            Store = store
        };
        await session.HandleAsync(Login(), Now + 10);
        await session.HandleAsync(Select(store.States[0].WireId), Now + 10);
        return await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10);
    }

    static async Task<(GameSession Session, FakeCharacterStore Store)> SkinnedAsync(params int[] skins)
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now);
        Assert.NotEmpty(await session.AcquireSkinsAsync(skins, default));
        return (session, store);
    }

    static Task<List<byte[]>> UnequipWeaponAsync(GameSession session, FakeCharacterStore store)
    {
        var weapon = store.Items.Single(i => i.Location == "equipment" && i.Slot == 1);
        return session.HandleAsync(new PacketWriter().U16(0xe249).I64(weapon.WireId).U8(1).I32(0).ToArray(), Now);
    }

    static byte[] Select(int wireId) => new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray();

    static byte[] SkinEquip(int slot, int skin, long dbid = 0) =>
        new PacketWriter().U16(GameSession.SkinEquipRq).Varint(1).U8((byte)slot).U8((byte)slot).I64(dbid).I32(skin).ToArray();

    static byte[] Visibility(int visual, bool display) =>
        new PacketWriter().U16(GameSession.EquipSlotVisibilityRq).U8(1).U8((byte)visual).Bool(display).ToArray();

    static (long DbId, int Skin) ReadEquipReply(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        Assert.Equal(0, r.U16());
        Assert.Equal(1, (int)r.Varint());
        r.U8();
        r.U8();
        return (r.I64(), r.I32());
    }

    static List<(int Slot, int AfterItem, int AfterSkin, bool AfterDisplay)> ReadChanges(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        r.Varint();
        r.I32();
        var count = (int)r.Varint();
        var list = new List<(int, int, int, bool)>();
        for (var i = 0; i < count; i++)
        {
            var slot = r.U8();
            ReadEquip(r);
            var after = ReadEquip(r);
            list.Add((slot, after.Item, after.Skin, after.Display));
        }
        return list;
    }

    static (int Item, int Skin, bool Display) ReadEquip(PacketReader r)
    {
        var item = r.I32();
        r.U8();
        r.U8();
        r.I64();
        r.Bool();
        var skin = r.I32();
        var dyes = (int)r.Varint();
        for (var i = 0; i < dyes; i++) r.I32();
        var godstones = (int)r.Varint();
        for (var i = 0; i < godstones; i++) r.I32();
        var display = r.Bool();
        r.Bool();
        r.Bool();
        r.Varint();
        return (item, skin, display);
    }

    static List<(int Visual, bool Display)> ReadHidden(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var skins = (int)r.Varint();
        for (var i = 0; i < skins; i++) { r.I32(); r.I32(); }
        Assert.Equal(0, (int)r.Varint());
        Assert.Equal(0, (int)r.Varint());
        r.U8();
        Assert.Equal(0, (int)r.Varint());
        var presets = (int)r.Varint();
        var list = new List<(int, bool)>();
        for (var i = 0; i < presets; i++)
        {
            r.U8();
            var options = (int)r.Varint();
            for (var n = 0; n < options; n++)
            {
                r.U8();
                var visual = r.U8();
                var display = r.Bool();
                r.Bool();
                r.Bool();
                r.I32();
                list.Add((visual, display));
            }
        }
        return list;
    }
}
