using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aion.Application.Gameplay;
using Npgsql;
namespace Aion.Infrastructure.Postgres;

public sealed class PostgresQaBotStore(NpgsqlDataSource source) : IQaBotStore
{
    static readonly Guid QaAccountNamespace = new("a9f4e6c2-7b3d-41e1-9c8a-000000000101");
    static NpgsqlCommand Query(NpgsqlConnection c, string sql, params object?[] args)
    {
        var q = new NpgsqlCommand(sql, c);
        foreach (var arg in args)
            q.Parameters.Add(arg switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value },
                _ => new NpgsqlParameter { Value = arg },
            });
        return q;
    }

    public async Task<long> StartRunAsync(string className, int levelFrom, int levelTo, int botCount, int? worldId, string? startedBy, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c,
            "INSERT INTO game.qa_runs (class_name, level_from, level_to, bot_count, world_id, started_by) VALUES ($1,$2,$3,$4,$5,$6) RETURNING run_id",
            className, levelFrom, levelTo, botCount, worldId, startedBy);
        return (long)(await q.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task FinishRunAsync(long runId, string status, int questsRewarded, int stuckCount, string? note, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c,
            "UPDATE game.qa_runs SET finished_at=now(), status=$2, quests_rewarded=$3, stuck_count=$4, note=$5 WHERE run_id=$1",
            runId, status, questsRewarded, stuckCount, note);
        await q.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateRunCountersAsync(long runId, int questsRewarded, int stuckCount, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, "UPDATE game.qa_runs SET quests_rewarded=$2, stuck_count=$3 WHERE run_id=$1", runId, questsRewarded, stuckCount);
        await q.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RecordStuckAsync(QaStuckReportRecord report, int samplesPerBucket, bool recordDetail = true, int sampleDetailIntervalMinutes = 15, CancellationToken ct = default)
    {
        samplesPerBucket = Math.Clamp(samplesPerBucket, 1, 100);
        sampleDetailIntervalMinutes = Math.Clamp(sampleDetailIntervalMinutes, 1, 24 * 60);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        long bucketId;
        await using (var upsert = Query(c, """
            INSERT INTO game.qa_stuck_buckets (quest_code, quest_step, reason_code, quest_title, hit_count, first_seen, last_seen)
            VALUES ($1,$2,$3,$4,CASE WHEN $5 THEN 1 ELSE 0 END,now(),now())
            ON CONFLICT (quest_code, quest_step, reason_code) DO UPDATE SET
                hit_count = game.qa_stuck_buckets.hit_count + CASE WHEN $5 THEN 1 ELSE 0 END,
                last_seen = now(),
                quest_title = COALESCE(EXCLUDED.quest_title, game.qa_stuck_buckets.quest_title)
            RETURNING bucket_id
            """, report.QuestCode, report.QuestStep, report.ReasonCode, report.QuestTitle, recordDetail))
        {
            upsert.Transaction = t;
            bucketId = (long)(await upsert.ExecuteScalarAsync(ct) ?? 0L);
        }
        if (!recordDetail)
        {
            await t.CommitAsync(ct);
            return false;
        }
        await using (var recent = Query(c, """
            SELECT 1 FROM game.qa_stuck_samples
            WHERE bucket_id = $1 AND created_at > now() - make_interval(mins => $2)
            LIMIT 1
            """, bucketId, sampleDetailIntervalMinutes))
        {
            recent.Transaction = t;
            if (await recent.ExecuteScalarAsync(ct) is not null)
            {
                await t.CommitAsync(ct);
                return false;
            }
        }
        await using (var sample = Query(c, """
            INSERT INTO game.qa_stuck_samples (bucket_id, run_id, bot_index, class_name, character_level, map_id, position, reason_detail, last_actions)
            VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9::jsonb)
            """,
            bucketId, report.RunId, report.BotIndex, report.ClassName, report.CharacterLevel, report.MapId,
            report.Position is { } pos ? JsonSerializer.Serialize(pos) : null,
            report.ReasonDetail, JsonSerializer.Serialize(report.LastActions)))
        {
            sample.Transaction = t;
            await sample.ExecuteNonQueryAsync(ct);
        }
        await using (var trim = Query(c, """
            DELETE FROM game.qa_stuck_samples WHERE bucket_id=$1 AND sample_id NOT IN (
                SELECT sample_id FROM game.qa_stuck_samples WHERE bucket_id=$1 ORDER BY created_at DESC LIMIT $2)
            """, bucketId, samplesPerBucket))
        {
            trim.Transaction = t;
            await trim.ExecuteNonQueryAsync(ct);
        }
        await using (var legacy = Query(c, """
            INSERT INTO game.qa_stuck_reports (run_id, bot_index, quest_code, quest_title, quest_step, class_name, character_level,
                map_id, position, reason_code, reason_detail, last_actions)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10,$11,$12::jsonb)
            """,
            report.RunId, report.BotIndex, report.QuestCode, report.QuestTitle, report.QuestStep, report.ClassName,
            report.CharacterLevel, report.MapId,
            report.Position is { } pos2 ? JsonSerializer.Serialize(pos2) : null,
            report.ReasonCode, report.ReasonDetail, JsonSerializer.Serialize(report.LastActions)))
        {
            legacy.Transaction = t;
            await legacy.ExecuteNonQueryAsync(ct);
        }
        await t.CommitAsync(ct);
        return true;
    }

    public async Task FinishStaleRunsAsync(int? worldId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, """
            UPDATE game.qa_runs
            SET finished_at = COALESCE(finished_at, now()),
                status = 'failed',
                note = COALESCE(note, 'stale running row closed')
            WHERE status = 'running'
              AND ($1::int IS NULL OR world_id IS NOT DISTINCT FROM $1)
            """, worldId);
        await q.ExecuteNonQueryAsync(ct);
    }

    public async Task EnsureInternalAccountsAsync(int slots, CancellationToken ct = default)
    {
        slots = Math.Clamp(slots, 1, 64);
        await using var c = await source.OpenConnectionAsync(ct);
        for (var slot = 0; slot < slots; slot++)
        {
            var accountId = Uuid5(QaAccountNamespace, $"qa-bot-slot-v1:{slot}");
            var subject = $"qa-bot-slot-{slot}";
            await using var q = Query(c, "SELECT game.qa_ensure_internal_account($1,$2,$3)", slot, accountId, subject);
            await q.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<Guid>> InternalAccountIdsAsync(CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, "SELECT account_id FROM game.qa_internal_accounts ORDER BY slot_index, created_at");
        var list = new List<Guid>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }

    public async Task CleanupRetentionAsync(int runRetentionDays, int sampleRetentionDays, CancellationToken ct = default)
    {
        runRetentionDays = Math.Max(1, runRetentionDays);
        sampleRetentionDays = Math.Max(1, sampleRetentionDays);
        await using var c = await source.OpenConnectionAsync(ct);
        await using (var q = Query(c, "DELETE FROM game.qa_runs WHERE finished_at IS NOT NULL AND finished_at < now() - make_interval(days => $1)", runRetentionDays))
            await q.ExecuteNonQueryAsync(ct);
        await using (var q = Query(c, "DELETE FROM game.qa_stuck_samples WHERE created_at < now() - make_interval(days => $1)", sampleRetentionDays))
            await q.ExecuteNonQueryAsync(ct);
        await using (var q = Query(c, "DELETE FROM game.qa_stuck_reports WHERE created_at < now() - make_interval(days => $1)", sampleRetentionDays))
            await q.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<QaRunRecord>> ListRunsAsync(int offset, int limit, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, "SELECT run_id, started_at, finished_at, status, class_name, level_from, level_to, bot_count, world_id, quests_rewarded, stuck_count, started_by, note FROM game.qa_runs ORDER BY started_at DESC OFFSET $1 LIMIT $2", Math.Max(0, offset), Math.Clamp(limit, 1, 100));
        var list = new List<QaRunRecord>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadRun(r));
        return list;
    }

    public async Task<QaRunRecord?> GetRunAsync(long runId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, "SELECT run_id, started_at, finished_at, status, class_name, level_from, level_to, bot_count, world_id, quests_rewarded, stuck_count, started_by, note FROM game.qa_runs WHERE run_id=$1", runId);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRun(r) : null;
    }

    public async Task<IReadOnlyList<QaStuckReportRecord>> ListReportsForRunAsync(long runId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, """
            SELECT s.sample_id, COALESCE(s.run_id, 0), s.bot_index, b.quest_code, b.quest_title, b.quest_step, s.class_name, s.character_level,
                   s.map_id, s.position, b.reason_code, s.reason_detail, s.last_actions, s.created_at
            FROM game.qa_stuck_samples s
            JOIN game.qa_stuck_buckets b USING (bucket_id)
            WHERE s.run_id=$1
            ORDER BY s.created_at
            """, runId);
        return await ReadReports(await q.ExecuteReaderAsync(ct), ct);
    }

    public async Task<IReadOnlyList<QaStuckSummaryRow>> StuckSummaryAsync(int limit, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, """
            SELECT quest_code, quest_title, quest_step, reason_code, hit_count::int, last_seen
            FROM game.qa_stuck_buckets
            ORDER BY hit_count DESC, last_seen DESC
            LIMIT $1
            """, Math.Clamp(limit, 1, 500));
        var rows = new List<QaStuckSummaryRow>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5)));
        return rows;
    }

    public async Task<IReadOnlyList<QaStuckReportRecord>> ExportReportsAsync(long? runId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = runId is { } id
            ? Query(c, """
                SELECT s.sample_id, COALESCE(s.run_id, 0), s.bot_index, b.quest_code, b.quest_title, b.quest_step, s.class_name, s.character_level,
                       s.map_id, s.position, b.reason_code, s.reason_detail, s.last_actions, s.created_at
                FROM game.qa_stuck_samples s JOIN game.qa_stuck_buckets b USING (bucket_id)
                WHERE s.run_id=$1 ORDER BY s.created_at
                """, id)
            : Query(c, """
                SELECT s.sample_id, COALESCE(s.run_id, 0), s.bot_index, b.quest_code, b.quest_title, b.quest_step, s.class_name, s.character_level,
                       s.map_id, s.position, b.reason_code, s.reason_detail, s.last_actions, s.created_at
                FROM game.qa_stuck_samples s JOIN game.qa_stuck_buckets b USING (bucket_id)
                ORDER BY s.created_at DESC LIMIT 5000
                """);
        return await ReadReports(await q.ExecuteReaderAsync(ct), ct);
    }

    static QaRunRecord ReadRun(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
        r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8),
        r.GetInt32(9), r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12));

    static async Task<List<QaStuckReportRecord>> ReadReports(NpgsqlDataReader r, CancellationToken ct)
    {
        var list = new List<QaStuckReportRecord>();
        await using (r)
        while (await r.ReadAsync(ct))
        {
            double[]? pos = null;
            if (!r.IsDBNull(9))
            {
                var el = JsonSerializer.Deserialize<double[]>(r.GetString(9));
                pos = el;
            }
            var actions = JsonSerializer.Deserialize<List<string>>(r.GetString(12)) ?? [];
            list.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5), r.GetString(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8), pos,
                r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), actions, r.GetFieldValue<DateTimeOffset>(13)));
        }
        return list;
    }

    static Guid Uuid5(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. nsBytes, .. Encoding.UTF8.GetBytes(name)]);
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)(bytes[6] & 0x0F | 0x50);
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}