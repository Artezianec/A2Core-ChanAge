using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

/// <summary>A game-port session; Race is the login row's race the player picked (multi-world routing), if recorded.</summary>
public sealed record WorldSession(string Token, Guid AccountId, int WorldId, DateTimeOffset ExpiresAt, int? Race = null)
{
    public override string ToString() => "WorldSession [token redacted]";
}
public sealed record WorldIdentity(VerifiedAccount Account, int WorldId);
public interface IWorldSessions
{
    Task<WorldSession?> OpenAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct=default);
    /// <summary>
    /// 2.0.12 game-port handoff (0xD456): the client sends the login grant but not the 64-hex game
    /// ticket, so open the world session from the single-use grant and wire account alone.
    /// </summary>
    Task<WorldSession?> OpenByGrantAsync(long wireAccountId, string grant, int worldId, CancellationToken ct=default) =>
        Task.FromResult<WorldSession?>(null);
    Task<WorldIdentity?> ValidateAsync(string token, CancellationToken ct=default);
    Task CloseAsync(string token, CancellationToken ct=default);
}
