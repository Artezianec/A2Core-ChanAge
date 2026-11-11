using System.Text.Json;
using Aion.Application.Gameplay;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>
/// The account Quna wallets (migration 111, docs/QUNA_WALLET.md): game.quna_apply and game.quna_refund do the work in one
/// statement each, so the balance check, the ledger entry and the balance change commit together. Ported from KiskEmu
/// (AGPL-3.0); here the wallets live in the one game database.
/// </summary>
public sealed class PostgresQunaWallet(NpgsqlDataSource source) : IQunaWallet
{
    public async Task<QunaResult> BalanceAsync(Guid account, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("SELECT balance, last_entry_id FROM game.quna_wallets WHERE account_id = $1");
        command.Parameters.AddWithValue(account);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new QunaResult(true, reader.GetInt64(0), reader.GetInt64(1)) : new QunaResult(true, 0);
    }

    public async Task<IReadOnlyList<QunaLedgerEntry>> LedgerAsync(Guid account, int limit, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("""
            SELECT entry_id, account_id, delta, balance_after, reason, request_id, world_id, character_id, details::text, created_at
            FROM game.quna_ledger WHERE account_id = $1 ORDER BY entry_id DESC LIMIT $2
            """);
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(Math.Clamp(limit, 0, 500));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var entries = new List<QunaLedgerEntry>();
        while (await reader.ReadAsync(ct))
            entries.Add(new QunaLedgerEntry(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(8)),
                reader.GetFieldValue<DateTimeOffset>(9)));
        return entries;
    }

    public Task<QunaResult> SpendAsync(QunaRequest request, CancellationToken ct = default) =>
        QunaWalletRules.Invalid(request, debit: true) is { } error ? Refused(request.AccountId, error, ct) : ApplyAsync(request, -request.Amount, ct);

    public Task<QunaResult> GrantAsync(QunaRequest request, CancellationToken ct = default) =>
        QunaWalletRules.Invalid(request, debit: false) is { } error ? Refused(request.AccountId, error, ct) : ApplyAsync(request, request.Amount, ct);

    public async Task<QunaResult> RefundAsync(Guid account, string spendRequestId, int? worldId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spendRequestId) || spendRequestId.Length > 190) return await Refused(account, QunaWalletRules.InvalidRequest, ct);
        await using var command = source.CreateCommand("SELECT ok, balance, entry_id, error, repeated FROM game.quna_refund($1, $2, $3)");
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(spendRequestId);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)worldId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        return await ReadAsync(command, ct);
    }

    public async Task<QunaResult?> AbsorbItemStackAsync(Guid account, Guid instance, int? worldId = null, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("SELECT ok, balance, entry_id, error, repeated FROM game.quna_absorb_stack($1, $2, $3)");
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(instance);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)worldId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        return await ReadAsync(command, ct);
    }

    public async Task<QunaResult> StaffChangeAsync(Guid actor, Guid account, string requestId, long delta, string? note, CancellationToken ct = default)
    {
        var request = new QunaRequest(account, requestId, Math.Abs(delta), QunaWalletRules.Staff);
        if (QunaWalletRules.Invalid(request, debit: delta < 0) is { } error) return await Refused(account, error, ct);
        await using var command = source.CreateCommand("SELECT ok, balance, entry_id, error, repeated FROM game.quna_staff_apply($1, $2, $3, $4, $5)");
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(requestId);
        command.Parameters.AddWithValue(delta);
        command.Parameters.AddWithValue(note ?? "");
        try { return await ReadAsync(command, ct); }
        catch (PostgresException failure) when (failure.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        { return await Refused(account, failure.SqlState == PostgresErrorCodes.UniqueViolation ? QunaWalletRules.RequestConflict : QunaWalletRules.NotFound, ct); }
    }

    public async Task<QunaResult> StaffRefundAsync(Guid actor, Guid account, string spendRequestId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spendRequestId) || spendRequestId.Length > 190) return await Refused(account, QunaWalletRules.InvalidRequest, ct);
        await using var command = source.CreateCommand("SELECT ok, balance, entry_id, error, repeated FROM game.quna_staff_refund($1, $2, $3)");
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(spendRequestId);
        return await ReadAsync(command, ct);
    }

    async Task<QunaResult> ApplyAsync(QunaRequest request, long delta, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT ok, balance, entry_id, error, repeated FROM game.quna_apply($1, $2, $3, $4, $5, $6, $7)");
        command.Parameters.AddWithValue(request.AccountId);
        command.Parameters.AddWithValue(request.RequestId);
        command.Parameters.AddWithValue(delta);
        command.Parameters.AddWithValue(request.Reason);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)request.WorldId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)request.CharacterId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = request.Details is { } details ? details.GetRawText() : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        try { return await ReadAsync(command, ct); }
        // The same request id racing for two accounts, or an account the realm does not know.
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        { return await Refused(request.AccountId, error.SqlState == PostgresErrorCodes.UniqueViolation ? QunaWalletRules.RequestConflict : QunaWalletRules.NotFound, ct); }
    }

    static async Task<QunaResult> ReadAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("game.quna_apply returned no row.");
        return new QunaResult(reader.GetBoolean(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4));
    }

    async Task<QunaResult> Refused(Guid account, string error, CancellationToken ct)
    {
        var current = account == Guid.Empty ? new QunaResult(true, 0) : await BalanceAsync(account, ct);
        return new QunaResult(false, current.Balance, current.EntryId, error);
    }
}
