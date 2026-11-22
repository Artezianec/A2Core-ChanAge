namespace Aion.Application.Gameplay;

/// <summary>Rate-limited QA bot boot diagnostics (host implements logging).</summary>
public interface IQaBotDiagnostics
{
    void BootFailure(int botIndex, string phase, string reasonCode, string? detail, Exception? error);
    /// <param name="experienceToNext">NeedExp for the current level (0 when unknown/maxed); experience is stored per-level.</param>
    void Heartbeat(int botIndex, string? questCode, int questStep, int level, long experience, long experienceToNext, int? mapId, string? position, string? lastAction, long softStuckUntilMs);
}