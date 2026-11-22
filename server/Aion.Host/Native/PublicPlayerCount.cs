using Aion.World;

namespace Aion.Host.Native;

/// <summary>Player and QA-bot counts for <c>/api/status</c> (cached briefly; excludes playerbots / market bots).</summary>
public sealed record PublicOnlineSnapshot(int Online, int OnlineBots);

public sealed class PublicPlayerCount(WorldPresence? presence, QaBots? qaBots, TimeProvider clock)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    readonly SemaphoreSlim gate = new(1, 1);
    PublicOnlineSnapshot? cached;
    DateTimeOffset cachedAt = DateTimeOffset.MinValue;

    public async Task<PublicOnlineSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (cached is { } hit && now - cachedAt < CacheFor) return hit;

        await gate.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow();
            if (cached is { } again && now - cachedAt < CacheFor) return again;
            var players = presence?.RealPlayers ?? 0;
            var qa = qaBots?.ActiveInWorldCount ?? 0;
            var snap = new PublicOnlineSnapshot(players + qa, qa);
            cached = snap;
            cachedAt = now;
            return snap;
        }
        finally { gate.Release(); }
    }

    /// <summary>Total online for landing (players + QA bots).</summary>
    public async Task<int> CountAsync(CancellationToken ct) => (await SnapshotAsync(ct)).Online;
}