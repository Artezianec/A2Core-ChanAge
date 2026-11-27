using System.Net;
using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

public sealed record ClientLaunch(string Authorization, int WorldId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "ClientLaunch [authorization redacted]";
}
public sealed record ClientLoginGrant(Guid AccountId, long WireAccountId, string Grant, int WorldId)
{
    public override string ToString() => "ClientLoginGrant [grant redacted]";
}
public interface IClientHandoff
{
    Task<ClientLaunch?> AuthorizeLaunchAsync(string sessionToken, int worldId, CancellationToken ct);
    Task<GameTicket?> RedeemLaunchAsync(string authorization, CancellationToken ct);
    Task<Guid?> ResolveLaunchAccountAsync(string authorization, CancellationToken ct);
    Task<ClientLoginGrant?> BeginLoginAsync(string ticket, int worldId, CancellationToken ct);
    /// <summary>Mints a fresh game ticket for the account's active session (same rules as launch redemption).</summary>
    Task<GameTicket?> MintGameTicketForAccountAsync(Guid accountId, int worldId, CancellationToken ct);
    /// <summary>Maps a verified platform secondary-auth access token to its account.</summary>
    Task<Guid?> ResolveAccountByPlatformAccessTokenAsync(string accessToken, CancellationToken ct);
    /// <summary>
    /// Turns the wire credential from NPLogin into a 64-char hex game ticket when possible: direct ticket,
    /// embedded hex, launch authorization redemption, or platform access token minting. Client-IP account
    /// binding minting runs only when <paramref name="developmentClientBinding"/> is true (Native logins=development).
    /// </summary>
    Task<string?> ResolveLoginTicketAsync(string wire, int worldId, string? launchAuthorization, IPAddress? clientAddress, bool developmentClientBinding, CancellationToken ct);
    // Read-only: the enabled account a live game ticket belongs to (chat login binding); no expiry change, no grant.
    Task<Guid?> ResolveTicketAsync(string ticket, int worldId, CancellationToken ct);
    Task<VerifiedAccount?> CompleteLoginAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct);
    /// <summary>
    /// Multi-world: rebinds a login's unused handoff grant to the world the player picked (0x390e), with the picked login
    /// row's race for that world's lobby. False when the grant is unknown, used or expired.
    /// </summary>
    Task<bool> RouteLoginAsync(string grant, long wireAccountId, int worldId, int race, CancellationToken ct) => Task.FromResult(false);
}
