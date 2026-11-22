using System.Net;
using Aion.Application.Authentication;

namespace Aion.Host.Platform;

/// <summary>Placeholder when account databases are not configured (development native ports only).</summary>
public sealed class NullPlatformSecondaryAuth : IPlatformSecondaryAuth
{
    public Task RememberClientAsync(IPAddress? address, Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Guid?> ResolveAccountAsync(IPAddress? address, string? platformAccessToken, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<Guid?> ResolveAccountByWireIdAsync(string wireAccountId, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<byte[]?> AuthTypesAsync(Guid accountId, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    public Task<byte[]?> StartAsync(Guid accountId, string? audienceAppId, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    public Task<byte[]?> FinishAsync(Guid accountId, string sessionId, string? code, string? recoveryCode, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    public Task<bool> IsVerifiedAsync(Guid accountId, string platformAccessToken, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> HasRecentVerificationAsync(Guid accountId, CancellationToken ct) => Task.FromResult(false);
}