using Aion.Application.Authentication;
using Aion.Core.Accounts;

namespace Aion.Host.Native;

/// <summary>In-memory world session tokens for internal QA accounts (no browser handoff).</summary>
public sealed class QaInternalWorldSessions : IWorldSessions
{
    readonly Dictionary<string, WorldIdentity> tokens = new(StringComparer.Ordinal);
    readonly Lock gate = new();

    public string Register(WorldIdentity identity)
    {
        var token = $"qa-int:{identity.Account.AccountId:N}:{identity.WorldId}";
        lock (gate) tokens[token] = identity;
        return token;
    }

    public Task<WorldSession?> OpenAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct = default) =>
        Task.FromResult<WorldSession?>(null);

    public Task<WorldIdentity?> ValidateAsync(string token, CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult(tokens.TryGetValue(token, out var id) ? id : null);
    }

    public Task CloseAsync(string token, CancellationToken ct = default)
    {
        lock (gate) tokens.Remove(token);
        return Task.CompletedTask;
    }
}