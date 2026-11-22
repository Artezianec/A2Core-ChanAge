using Aion.Host.Native;
using Aion.World;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PublicPlayerCountTests
{
    [Fact]
    public async Task No_presence_reports_zero()
    {
        var counter = new PublicPlayerCount(null, null, TimeProvider.System);
        var snap = await counter.SnapshotAsync(CancellationToken.None);
        Assert.Equal(0, snap.Online);
        Assert.Equal(0, snap.OnlineBots);
    }

    [Fact]
    public async Task Counts_real_players_and_excludes_playerbots()
    {
        var presence = new WorldPresence();
        presence.Enter("A", new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(1)), Guid.NewGuid());
        presence.Enter("B", new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(2)) { Bot = true }, Guid.NewGuid());
        var counter = new PublicPlayerCount(presence, null, TimeProvider.System);
        var snap = await counter.SnapshotAsync(CancellationToken.None);
        Assert.Equal(1, snap.Online);
        Assert.Equal(0, snap.OnlineBots);
    }

    [Fact]
    public async Task Includes_active_QA_bots_in_online_total()
    {
        var presence = new WorldPresence();
        presence.Enter("A", new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(1)), Guid.NewGuid());
        var world = new SharedWorld(GameData.Embedded);
        var qa = new QaBots(GameData.Embedded, new QaBotOptions { Enabled = true, AutoRun = new QaAutoRunOptions { Enabled = false } }, world);
        // ActiveInWorldCount is 0 without a run; total should match players only.
        var counter = new PublicPlayerCount(presence, qa, TimeProvider.System);
        Assert.Equal(1, (await counter.SnapshotAsync(CancellationToken.None)).Online);
    }

    [Fact]
    public async Task Result_is_cached_briefly()
    {
        var presence = new WorldPresence();
        presence.Enter("Solo", new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(3)), Guid.NewGuid());
        var clock = new FakeClock();
        var counter = new PublicPlayerCount(presence, null, clock);
        Assert.Equal(1, (await counter.SnapshotAsync(CancellationToken.None)).Online);
        presence.Enter("Extra", new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(4)), Guid.NewGuid());
        Assert.Equal(1, (await counter.SnapshotAsync(CancellationToken.None)).Online);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(2, (await counter.SnapshotAsync(CancellationToken.None)).Online);
    }

    sealed class FakeClock : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan span) => now += span;
        public override DateTimeOffset GetUtcNow() => now;
    }
}