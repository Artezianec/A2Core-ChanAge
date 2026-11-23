using System.Security.Cryptography;
using System.Text;

namespace Aion.Application.Gameplay;

/// <summary>
/// Shop purchase request ids for Quna idempotency: client serial when present on 0x568b, otherwise a stable
/// fingerprint with at most one retry dedupe inside <see cref="RetryWindowMs"/>.
/// </summary>
public static class ShopPurchaseRequest
{
    public const long RetryWindowMs = 3_000L;

    /// <summary>Optional i32 at bytes 10–13 on 0x568b when the body is longer than the usmap minimum (goods, qty).</summary>
    public static int? ClientSerialFromNpPacket(byte[] plain) =>
        plain.Length >= 14 ? NullIfZero(BitConverter.ToInt32(plain, 10)) : null;

    static int? NullIfZero(int v) => v == 0 ? null : v;

    public static string Fingerprint(int worldId, Guid accountId, Guid characterId, string kind,
        IReadOnlyList<(int GoodsId, int Count)> lines, int? clientSerial)
    {
        if (clientSerial is int serial)
            return $"w{worldId}:a{accountId:N}:c{characterId:N}:{kind}:s{serial}";
        var payload = new StringBuilder(160);
        payload.Append(worldId).Append(':').Append(accountId.ToString("N")).Append(':').Append(characterId.ToString("N"))
            .Append(':').Append(kind);
        foreach (var (goods, count) in lines.OrderBy(l => l.GoodsId))
            payload.Append(':').Append(goods).Append('x').Append(count);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString()));
        return "fp" + Convert.ToHexString(hash).ToLowerInvariant()[..40];
    }

    /// <summary>Picks the Quna/shop_orders request id for this attempt (new purchase vs in-window retry).</summary>
    public static string Allocate(string fingerprint, long nowMs, string? lastRequestId, string? lastStatus, long? lastOrderedAtMs)
    {
        if (lastRequestId is not null && lastStatus is "done" or "pending" && lastOrderedAtMs is long t
            && nowMs - t <= RetryWindowMs)
            return lastRequestId;
        return lastRequestId is null && lastOrderedAtMs is null ? fingerprint : $"{fingerprint}:{nowMs}";
    }

    public static bool MatchesFingerprint(string requestId, string fingerprint) =>
        requestId == fingerprint || requestId.StartsWith(fingerprint + ':', StringComparison.Ordinal);
}