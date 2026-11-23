using System.Text;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// CANDIDATE: client BM gift wire unknown (no GiftShop* in tools/data/opcode-names.json or captured traces as of 2026-11-22).
// DevPacketData neighbours: BlackCloudShopRefreshCheck 0x5698/0x5699 -> 0x569A RQ / 0x569B RS (CANDIDATE layout).
public sealed partial class GameSession
{
    public const ushort ShopGiftPurchaseRq = 0x569a, ShopGiftPurchaseRs = 0x569b;

    async Task<List<byte[]>> ShopGiftPurchaseAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (!shopFeatures.GiftingEnabled) return [GiftReply(1, 0, 0)];
        if (plain.Length < 10) return [];
        var goodsId = BitConverter.ToInt32(plain, 2);
        var quantity = BitConverter.ToInt32(plain, 6);
        var name = plain.Length > 10 ? Encoding.UTF8.GetString(plain, 10, plain.Length - 10).TrimEnd('\0') : "";
        byte[] GiftReply(int result, int goods, int qty) =>
            PacketWriter.Opcode(ShopGiftPurchaseRs).U16(result).I32(goods).I32(qty).ToArray();
        if (quantity < 1 || quantity > PacketSecurity.MaxShopLineCount || string.IsNullOrWhiteSpace(name)) return [GiftReply(1, goodsId, quantity)];
        if (!ShopFeatureLayer.IsGiftable(goodsId)) return [GiftReply(1, goodsId, quantity)];
        if (Store is not { } store || CharacterId is not { } character) return [GiftReply(1, goodsId, quantity)];
        if (string.Equals(name, CharacterName, StringComparison.OrdinalIgnoreCase)) return [GiftReply(1, goodsId, quantity)];
        var online = World?.FindOnline(name);
        var recipient = online?.CharacterId ?? await StoredValue(() => store.CharacterByNameAsync(name, ct), null);
        if (recipient is not { } to) return [GiftReply(1, goodsId, quantity)];
        if (online is null && await StoredValue(() => store.CharacterWorldIdAsync(to, ct), store.WorldId) != store.WorldId)
            return [GiftReply(1, goodsId, quantity)];
        shopGiftDeliverTo = to;
        var entries = new List<(int Goods, int Count)> { (goodsId, quantity) };
        var replies = await ExecuteShopPurchaseAsync("gift", entries, now, ct, r => GiftReply(r, goodsId, quantity), store, character, ResolveShopGoodsRow);
        if (replies.Count > 0 && replies[0][2] == 0 && online is { } live)
        {
            var posts = await StoredValue(() => store.PostsAsync(to, ct), (IReadOnlyList<PostRecord>)[]);
            if (posts.OrderByDescending(p => p.Sent).FirstOrDefault() is { } post)
                live.Send(WritePost(PacketWriter.Opcode(PostRecvNt), post).ToArray());
        }
        return replies;
    }
}