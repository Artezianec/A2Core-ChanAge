namespace Aion.Host.Native;

using Aion.Application.Gameplay;

/// <summary>Rate-limited logging for QA bot login/create/enter failures.</summary>
public sealed class QaBotDiagnostics(ILogger<QaBotDiagnostics> logger) : IQaBotDiagnostics
{
    const int MinLogIntervalSeconds = 30;
    readonly Lock gate = new();
    readonly Dictionary<string, DateTimeOffset> lastLog = new(StringComparer.Ordinal);
    readonly Dictionary<int, DateTimeOffset> lastHeartbeat = new();

    public void BootFailure(int botIndex, string phase, string reasonCode, string? detail, Exception? error)
    {
        var key = $"{botIndex}:{reasonCode}";
        var now = DateTimeOffset.UtcNow;
        var shouldLog = false;
        lock (gate)
        {
            if (!lastLog.TryGetValue(key, out var prev) || now - prev >= TimeSpan.FromSeconds(MinLogIntervalSeconds))
            {
                lastLog[key] = now;
                shouldLog = true;
            }
        }
        if (!shouldLog) return;
        if (error is null)
            logger.LogWarning("QA bot slot {Slot} {Phase} failed ({Reason}): {Detail}", botIndex, phase, reasonCode, detail ?? "");
        else
            logger.LogWarning(error, "QA bot slot {Slot} {Phase} failed ({Reason}): {Detail}", botIndex, phase, reasonCode, detail ?? "");
    }

    public void Heartbeat(int botIndex, string? questCode, int questStep, int level, long experience, long experienceToNext, int? mapId, string? position, string? lastAction, long softStuckUntilMs)
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (lastHeartbeat.TryGetValue(botIndex, out var prev) && now - prev < TimeSpan.FromSeconds(30))
                return;
            lastHeartbeat[botIndex] = now;
        }
        var xp = level < Aion.World.GameSession.KillExperienceLevel
            ? $"{experience}/{experienceToNext} quest-track"
            : experienceToNext > 0 ? $"{experience}/{experienceToNext}" : experience.ToString();
        var paused = "-";
        if (softStuckUntilMs > now.ToUnixTimeMilliseconds())
        {
            var remainSec = (int)Math.Ceiling((softStuckUntilMs - now.ToUnixTimeMilliseconds()) / 1000.0);
            var untilLocal = DateTimeOffset.FromUnixTimeMilliseconds(softStuckUntilMs).ToLocalTime();
            paused = $"{remainSec}s (~{untilLocal:yyyy-MM-dd HH:mm:ss})";
        }
        logger.LogInformation(
            "QA bot slot {Slot} heartbeat: quest={Quest} step={Step} L{Level} xp={Experience} map={Map} at={Position} action={Action} soft_stuck_until={SoftStuckUntil}",
            botIndex, questCode ?? "-", questStep, level, xp, mapId, position ?? "?", lastAction ?? "-", paused);
    }
}