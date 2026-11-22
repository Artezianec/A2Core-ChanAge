namespace Aion.Application.Gameplay;

public sealed record QaRunRecord(
    long RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string ClassName,
    int LevelFrom,
    int LevelTo,
    int BotCount,
    int? WorldId,
    int QuestsRewarded,
    int StuckCount,
    string? StartedBy,
    string? Note);

public sealed record QaStuckReportRecord(
    long ReportId,
    long RunId,
    int BotIndex,
    string QuestCode,
    string? QuestTitle,
    int QuestStep,
    string ClassName,
    int CharacterLevel,
    int? MapId,
    double[]? Position,
    string ReasonCode,
    string? ReasonDetail,
    IReadOnlyList<string> LastActions,
    DateTimeOffset CreatedAt);

/// <summary>Aggregated stuck counts for the staff dashboard (quest + step + reason).</summary>
public sealed record QaStuckSummaryRow(
    string QuestCode,
    string? QuestTitle,
    int QuestStep,
    string ReasonCode,
    int Count,
    DateTimeOffset? LastSeen);

/// <summary>Persisted QA bot runs and stuck reports (migration 133).</summary>
public interface IQaBotStore
{
    Task<long> StartRunAsync(string className, int levelFrom, int levelTo, int botCount, int? worldId, string? startedBy, CancellationToken ct = default);
    Task FinishRunAsync(long runId, string status, int questsRewarded, int stuckCount, string? note, CancellationToken ct = default);
    Task UpdateRunCountersAsync(long runId, int questsRewarded, int stuckCount, CancellationToken ct = default);
    /// <param name="recordDetail">When false, refreshes bucket last_seen only (no hit_count bump, sample, or legacy row).</param>
    /// <returns>True when a new detail sample (and legacy row) was written.</returns>
    Task<bool> RecordStuckAsync(QaStuckReportRecord report, int samplesPerBucket, bool recordDetail = true, int sampleDetailIntervalMinutes = 15, CancellationToken ct = default);
    Task EnsureInternalAccountsAsync(int slots, CancellationToken ct = default);
    Task FinishStaleRunsAsync(int? worldId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> InternalAccountIdsAsync(CancellationToken ct = default);
    Task CleanupRetentionAsync(int runRetentionDays, int sampleRetentionDays, CancellationToken ct = default);
    Task<IReadOnlyList<QaRunRecord>> ListRunsAsync(int offset, int limit, CancellationToken ct = default);
    Task<QaRunRecord?> GetRunAsync(long runId, CancellationToken ct = default);
    Task<IReadOnlyList<QaStuckReportRecord>> ListReportsForRunAsync(long runId, CancellationToken ct = default);
    Task<IReadOnlyList<QaStuckSummaryRow>> StuckSummaryAsync(int limit, CancellationToken ct = default);
    Task<IReadOnlyList<QaStuckReportRecord>> ExportReportsAsync(long? runId, CancellationToken ct = default);
}