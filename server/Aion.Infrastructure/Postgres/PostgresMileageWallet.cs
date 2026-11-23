using System.Text.Json;
using Aion.Application.Gameplay;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

public sealed class PostgresMileageWallet(NpgsqlDataSource source) : IMileageWallet
{
    public async Task<MileageResult> BalanceAsync(Guid account, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("SELECT balance, last_entry_id FROM game.mileage_wallets WHERE account_id = $1");
        command.Parameters.AddWithValue(account);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new MileageResult(true, reader.GetInt64(0), reader.GetInt64(1)) : new MileageResult(true, 0);
    }

    public async Task<IReadOnlyList<MileageLedgerEntry>> LedgerAsync(Guid account, int limit, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("""
            SELECT entry_id, account_id, delta, balance_after, reason, request_id, world_id, character_id, details::text, created_at
            FROM game.mileage_ledger WHERE account_id = $1 ORDER BY entry_id DESC LIMIT $2
            """);
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(Math.Clamp(limit, 0, 500));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var entries = new List<MileageLedgerEntry>();
        while (await reader.ReadAsync(ct))
            entries.Add(new MileageLedgerEntry(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(8)),
                reader.GetFieldValue<DateTimeOffset>(9)));
        return entries;
    }

    public Task<MileageResult> SpendAsync(MileageRequest request, CancellationToken ct = default) =>
        MileageWalletRules.Invalid(request, debit: true) is { } error ? Refused(request.AccountId, error, ct) : ApplyAsync(request, -request.Amount, ct);

    public Task<MileageResult> GrantAsync(MileageRequest request, CancellationToken ct = default) =>
        MileageWalletRules.Invalid(request, debit: false) is { } error ? Refused(request.AccountId, error, ct) : ApplyAsync(request, request.Amount, ct);

    public Task<MileageResult> EarnAsync(MileageRequest request, CancellationToken ct = default) =>
        GrantAsync(request with { Reason = MileageWalletRules.Earn }, ct);

    async Task<MileageResult> ApplyAsync(MileageRequest request, long delta, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT ok, balance, entry_id, error, repeated FROM game.mileage_apply($1, $2, $3, $4, $5, $6, $7)");
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
        return await ReadAsync(command, ct);
    }

    static async Task<MileageResult> ReadAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new MileageResult(false, 0, Error: MileageWalletRules.InvalidRequest);
        return new MileageResult(reader.GetBoolean(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4));
    }

    async Task<MileageResult> Refused(Guid account, string error, CancellationToken ct) =>
        new(false, (await BalanceAsync(account, ct)).Balance, Error: error);
}