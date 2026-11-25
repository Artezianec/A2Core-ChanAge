using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

public sealed class SkinDecorateTests
{
    const long Now = 1_760_100_000_000;
    const int GreatswordSkin = 20005101;

    static byte[] DecorateRq(GameSession.SkinDecoInstance instance, int quna = 200) =>
        BuildDecorateBody([instance], new Dictionary<int, long> { [ShopCurrency.QunaGroup] = quna });

    static byte[] BuildDecorateBody(IReadOnlyList<GameSession.SkinDecoInstance> instances, Dictionary<int, long> costs)
    {
        var w = new PacketWriter();
        w.Varint(instances.Count);
        foreach (var inst in instances) WriteInstance(w, inst);
        w.Varint(costs.Count);
        foreach (var (currency, amount) in costs) w.U8(currency).I64(amount);
        return new PacketWriter().U16(GameSession.SkinDecorateRq).Raw(w.ToArray()).ToArray();
    }

    static void WriteInstance(PacketWriter w, GameSession.SkinDecoInstance i)
    {
        w.U8(i.Type).I64(i.SkinDbid).I32(i.SkinId).Varint(i.DecoSlots.Count);
        foreach (var s in i.DecoSlots)
            w.U8(s.Slot).U8(s.ColorR).U8(s.ColorG).U8(s.ColorB).Bool(s.ColorRemove)
                .I32(s.MaterialId).I32(s.PatternId)
                .U8(s.PatternColorR).U8(s.PatternColorG).U8(s.PatternColorB).Bool(s.PatternColorRemove)
                .I32(s.PatternColorOpacity).I32(s.PatternOpacity).I32(s.PatternSizeHorizontal).I32(s.PatternSizeVertical)
                .U8(s.PatternType).Bool(s.PatternAdditional).I32(s.PatternGlossy).I32(s.PatternMetallic);
        w.I64(i.UpdateTime).I32(i.Index);
    }

    static GameSession.SkinDecoSlot RedSlot(byte slot = 1) => new(
        slot, 255, 0, 0, false, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, false, 0, 0);

    [Fact]
    public async Task Skin_dye_purchase_applies_and_lists_instances()
    {
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Quna.Seed(store.AccountId, 10_000);
        var session = await EnterWorldAsync(store, Now, level: 55);
        Assert.NotEmpty(await session.AcquireSkinsAsync([GreatswordSkin], default));

        var inst = new GameSession.SkinDecoInstance(1, 7, GreatswordSkin, [RedSlot()], 0, 0);
        var dyed = await session.HandleAsync(DecorateRq(inst), Now);

        Assert.Equal(0, BitConverter.ToInt32(dyed[0], 2));
        Assert.Contains(dyed, p => Op(p) == GameSession.SkinDecorateRs);
        Assert.Contains(dyed, p => Op(p) == GameSession.SkinDecoInstanceInfoNt);
        Assert.Equal(9_800, (await store.Quna.BalanceAsync(store.AccountId)).Balance);

        var listed = await session.HandleAsync(new byte[] { 0x68, 0x8d }, Now + 1);
        Assert.Equal(2, listed.Count);
        Assert.Equal(GameSession.SkinDecoInstanceListRs, Op(listed[0]));

        var relog = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await relog.HandleAsync(Login(), Now + 20);
        await relog.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 20);
        await relog.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        var relisted = await relog.HandleAsync(new byte[] { 0x68, 0x8d }, Now + 21);
        Assert.Contains(relisted, p => Op(p) == GameSession.SkinDecoInstanceInfoNt);
    }

    [Fact]
    public async Task Quna_skin_set_purchase_refreshes_SkinInfo()
    {
        const int goods = 100106;
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Quna.Seed(store.AccountId, 100_000);
        var session = await EnterWorldAsync(store, Now, level: 55);

        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goods).I32(1).ToArray(), Now);

        Assert.Contains(buy, p => Op(p) == GameSession.SkinUpdateNt);
        Assert.Contains(buy, p => Op(p) == GameSession.SkinInfoNt);
    }
}