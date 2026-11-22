using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>Runs the QA bot loop (Aion:QaBots): waits for game ports, auto-run pool, adaptive tick when the world is busy.</summary>
public sealed class QaBotService(
    QaBots bots,
    QaBotOptions options,
    SharedWorld world,
    NativePortReadiness readiness,
    IQaBotStore? store,
    TimeProvider clock,
    ILogger<QaBotService> logger) : BackgroundService
{
    const int MaxBackoffMs = 60_000;
    const int MinErrorLogIntervalSeconds = 60;

    WorldLockStats.Snapshot? loadBaseline;
    DateTimeOffset lastCleanup = DateTimeOffset.MinValue;
    DateTimeOffset lastCleanupErrorLog = DateTimeOffset.MinValue;
    int consecutiveTickFailures;
    DateTimeOffset lastTickErrorLog = DateTimeOffset.MinValue;
    bool initialized;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("QA bots service started (Enabled={Enabled}, AutoRun={AutoRun})", options.Enabled, options.AutoRun.Enabled);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await readiness.GameReadyAsync(stoppingToken))
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }
                if (!initialized)
                {
                    try
                    {
                        await bots.InitializeAsync(stoppingToken);
                        initialized = true;
                    }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(error, "QA bots: initialization failed; retrying");
                        await Task.Delay(5000, stoppingToken);
                        continue;
                    }
                }
                await MaybeCleanupAsync(stoppingToken);
                try
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await bots.TickAutoRunAsync(now, stoppingToken);
                    await bots.TickAsync(now, stoppingToken);
                    consecutiveTickFailures = 0;
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    consecutiveTickFailures++;
                    var now = clock.GetUtcNow();
                    if (now - lastTickErrorLog >= TimeSpan.FromSeconds(MinErrorLogIntervalSeconds))
                    {
                        logger.LogError(error, "QA bots: tick failed ({Failures} consecutive); backing off", consecutiveTickFailures);
                        lastTickErrorLog = now;
                    }
                }
                await Task.Delay(LoopDelayMs(consecutiveTickFailures), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { await bots.StopAsync(stoppingToken); }
            catch (Exception error) { logger.LogWarning(error, "QA bots: shutdown stop failed"); }
        }
    }

    public static int FailureBackoffMs(int consecutiveFailures, int tickMs = QaBots.TickMs, int maxMs = MaxBackoffMs) =>
        consecutiveFailures <= 0 ? 0 : Math.Min(maxMs, tickMs * (1 << Math.Min(consecutiveFailures - 1, 10)));

    static int LoopDelayMs(int consecutiveFailures, int cpuGuardDelayMs) =>
        Math.Max(cpuGuardDelayMs, FailureBackoffMs(consecutiveFailures));

    int LoopDelayMs(int consecutiveFailures) => LoopDelayMs(consecutiveFailures, TickDelayMs());

    int TickDelayMs()
    {
        if (!options.CpuGuard.Enabled) return QaBots.TickMs;
        var snap = world.Lock.Stats.Read();
        var busy = 0.0;
        if (loadBaseline is { } prev)
        {
            var delta = snap.Since(prev);
            if (delta.Seconds >= 0.05)
                busy = Math.Max(delta.ExclusiveBusy, delta.SharedBusy);
        }
        loadBaseline = snap;
        return busy > options.CpuGuard.MaxWorldBusy
            ? Math.Max(QaBots.TickMs, options.CpuGuard.SlowTickMs)
            : QaBots.TickMs;
    }

    async Task MaybeCleanupAsync(CancellationToken ct)
    {
        if (store is null || options.Retention.CleanupIntervalHours <= 0) return;
        var now = DateTimeOffset.UtcNow;
        if (lastCleanup != DateTimeOffset.MinValue && now - lastCleanup < TimeSpan.FromHours(options.Retention.CleanupIntervalHours))
            return;
        lastCleanup = now;
        try
        {
            await store.CleanupRetentionAsync(options.Retention.RunRetentionDays, options.Retention.SampleRetentionDays, ct);
        }
        catch (Exception error)
        {
            var loggedAt = clock.GetUtcNow();
            if (loggedAt - lastCleanupErrorLog >= TimeSpan.FromSeconds(MinErrorLogIntervalSeconds))
            {
                logger.LogWarning(error, "QA bots: retention cleanup failed");
                lastCleanupErrorLog = loggedAt;
            }
        }
    }
}