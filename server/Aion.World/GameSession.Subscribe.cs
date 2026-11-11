using System.Collections.Concurrent;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Membership: the Status tab of the Subscribe window (KiskEmu c25c2317, without multi-realm Quna wallet).
public sealed partial class GameSession
{
    public const ushort SubscribeListNt = SocialOpcodes.E3Base + 1485, SubscribeUpdateNt = SocialOpcodes.E3Base + 1486;
    public const int QuaiMembershipDataId = 4, QuaiMembershipItemId = 583000004;
    public const long MembershipDurationMs = 30L * 86_400_000;

    public static readonly IReadOnlyList<byte> QuaiMembershipTypes =
    [
        1, 25, 26, 27, 2, 4, 28,
    ];

    public sealed record Membership(long StartMs, long EndMs, bool Revoked)
    {
        public bool ActiveAt(long now) => !Revoked && StartMs <= now && now < EndMs;
    }

    static volatile bool membershipByDefault = true;
    static readonly ConcurrentDictionary<string, Membership> memberships = new(StringComparer.Ordinal);

    public static void ConfigureMembership(bool byDefault) => membershipByDefault = byDefault;

    string MembershipKey => Store is { } store ? "account:" + store.AccountId : "dev:" + (CharacterName ?? "");

    public Membership? CurrentMembership(long now)
    {
        var key = MembershipKey;
        var held = memberships.GetValueOrDefault(key);
        if (held is { Revoked: true }) return null;
        if (held is not null && held.ActiveAt(now)) return held;
        if (!membershipByDefault) return null;
        var start = CycleStart(1, now);
        return memberships[key] = new Membership(start, start + MembershipDurationMs, false);
    }

    bool membershipLoaded;

    async Task LoadMembershipAsync(CancellationToken ct)
    {
        if (membershipLoaded || Store is not { } store) return;
        membershipLoaded = true;
        if (await store.MembershipAsync(ct) is { } stored)
            memberships[MembershipKey] = new Membership(stored.StartsAt.ToUnixTimeMilliseconds(), stored.EndsAt.ToUnixTimeMilliseconds(), stored.Revoked);
    }

    static StoredMembership ToStored(Membership m) =>
        new(DateTimeOffset.FromUnixTimeMilliseconds(m.StartMs), DateTimeOffset.FromUnixTimeMilliseconds(m.EndMs), m.Revoked);

    public List<byte[]> SetMembership(bool on, long now)
    {
        var start = CycleStart(1, now);
        var membership = memberships[MembershipKey] = on ? new Membership(start, start + MembershipDurationMs, false) : new Membership(0, 0, true);
        if (Store is { } store)
            _ = Task.Run(async () =>
            {
                try { await store.SaveMembershipAsync(ToStored(membership), CancellationToken.None); }
                catch (Exception error) { DebugShop($"Membership save failed: {error.Message}"); }
            });
        return [SubscribeInfos(SubscribeUpdateNt, now)];
    }

    Membership ExtendedMembership(long now)
    {
        var current = CurrentMembership(now);
        var from = current is null ? now : current.EndMs;
        return new Membership(current?.StartMs ?? now, from + MembershipDurationMs, false);
    }

    byte[] SubscribeInfos(ushort opcode, long now)
    {
        var membership = CurrentMembership(now);
        var w = PacketWriter.Opcode(opcode);
        if (membership is null) return w.Varint(0).ToArray();
        w.Varint((uint)QuaiMembershipTypes.Count);
        foreach (var type in QuaiMembershipTypes) w.U8(type).I32(QuaiMembershipDataId).I64(membership.StartMs).I64(membership.EndMs);
        return w.ToArray();
    }

    byte[] SubscribeList(long now) => SubscribeInfos(SubscribeListNt, now);

    static bool IsMembershipGoods(int goodsId) => PlatformGoodsTables.Embedded.Goods(goodsId)?.ItemId == QuaiMembershipItemId;

    async Task<List<byte[]>> BuyMembershipAsync(int goodsId, int quantity, long now, Func<int, byte[]> reply, CancellationToken ct)
    {
        if (PlatformGoodsTables.Embedded.Goods(goodsId) is not { } goods || quantity != 1) return [reply(1)];
        long price = shopOptions.PremiumMode switch
        {
            ShopPremiumMode.Disabled => -1,
            ShopPremiumMode.Free => 0,
            _ => goods.Price,
        };
        if (price < 0) return [reply(1)];
        var currency = shopOptions.PremiumMode == ShopPremiumMode.Kina ? KinaItemId : PlatformGoodsTables.Embedded.QunaItemId;
        var extended = ExtendedMembership(now);
        var changes = new List<byte[]>();
        if (Store is { } store && CharacterId is { } character)
        {
            if (price > 0)
            {
                // Quna comes from the account wallet (taken first, given back when the purchase fails); Kina from the bag.
                if (PurchaseCosts([(currency, price)]) is not { } paid) return [reply(2)];
                var order = new ShopOrderLine("membership", goodsId, 1, currency, price);
                var pay = await PayQunaAsync(paid.Quna, new { kind = "membership", goods = goodsId },
                    () => StoredOk(() => store.ShopBuyAsync(character, paid.Costs, [], [], [order], ct)), ct);
                if (pay != QunaPay.Done) return [reply(pay == QunaPay.NotEnough ? 2 : 1)];
                changes.AddRange(await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy));
            }
            await Stored(async () => await store.SaveMembershipAsync(ToStored(extended), ct) ? [[]] : []);
        }
        else if (price > 0) return [reply(1)];
        memberships[MembershipKey] = extended;
        return [reply(0), .. changes, SubscribeInfos(SubscribeUpdateNt, now)];
    }
}
