using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Core.Security;
using Aion.Host.Authentication;
using Aion.Host.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>GET /api/admin/diagnostics: staff session, rank check and safe output shape.</summary>
public sealed class StaffDiagnosticsTests : IAsyncDisposable
{
    const string StaffToken = "staff-token", PlayerToken = "player-token";
    readonly FakeDiagnosticsAuth auth = new();
    readonly Mock<IGameplayStore> game = new();
    readonly HostDiagnostics diagnostics = new();
    string[] roles = [StaffAccess.GamemasterRole];
    WebApplication? app;
    HttpClient? http;

    async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IStaffSessions>(auth);
        builder.Services.AddSingleton<IAccountAuthentication>(auth);
        game.Setup(g => g.AdminProfileAsync(It.IsAny<VerifiedAccount>(), It.IsAny<CancellationToken>()))
            .Returns((VerifiedAccount _, CancellationToken _) =>
            {
                if (!StaffAccess.Can(roles, StaffPermission.ViewDashboard)) throw new GameplayException("forbidden");
                return Task.FromResult(new AdminProfile(roles, null));
            });
        builder.Services.AddSingleton<IGameplayStore>(game.Object);
        builder.Services.AddSingleton(diagnostics);
        builder.Services.AddRateLimiter(options => options.AddPolicy("admin", _ => RateLimitPartition.GetNoLimiter("test")));
        app = builder.Build();
        app.UseRateLimiter();
        app.UseStaffSessions();
        app.MapStaffSessionEndpoints();
        var staff = app.MapGroup("/api/admin").RequireRateLimiting("admin");
        staff.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GameplayException error)
            { return error.Code == "forbidden" ? Results.StatusCode(403) : Results.BadRequest(new { error = error.Code }); }
        });
        DiagnosticsEndpoints.MapDiagnostics(staff);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return http = new HttpClient { BaseAddress = new Uri(address) };
    }

    static HttpRequestMessage Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task DiagnosticsRequiresStaffSessionAndViewDashboard()
    {
        var client = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/admin/diagnostics", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/admin/diagnostics", PlayerToken))).StatusCode);
        roles = [];
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/api/admin/diagnostics", StaffToken))).StatusCode);
        roles = [StaffAccess.GamemasterRole];
        var response = await client.SendAsync(Get("/api/admin/diagnostics", StaffToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("uptimeSeconds", out _));
        Assert.True(json.TryGetProperty("process", out var process));
        Assert.True(process.TryGetProperty("cpuPercent", out _));
        Assert.True(json.TryGetProperty("databases", out var databases) && databases.GetArrayLength() == 2);
        foreach (var db in databases.EnumerateArray())
        {
            Assert.False(db.TryGetProperty("host", out _));
            Assert.False(db.TryGetProperty("connectionString", out _));
        }
        Assert.True(json.TryGetProperty("logins", out var logins));
        Assert.True(logins.TryGetProperty("web", out _));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", body);
    }

    [Fact]
    public async Task DiagnosticsReflectsCounters()
    {
        var client = await StartAsync();
        roles = [StaffAccess.ModeratorRole];
        diagnostics.Login(HostDiagnostics.WebLogin, HostDiagnostics.Outcome.Succeeded);
        diagnostics.Unanswered(0x9100, firstOnConnection: true);
        var json = await (await client.SendAsync(Get("/api/admin/diagnostics", StaffToken))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("logins").GetProperty("web").GetProperty("succeeded").GetInt32());
        var opcodes = json.GetProperty("unansweredOpcodes");
        Assert.Equal("0x9100", opcodes[0].GetProperty("opcode").GetString());
        Assert.Equal(1, opcodes[0].GetProperty("count").GetInt32());
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    }

    sealed class FakeDiagnosticsAuth : IStaffSessions, IAccountAuthentication
    {
        readonly HashSet<string> staffTokens = [StaffToken];
        static readonly Guid Someone = Guid.NewGuid();

        public Task<StaffSignIn> BeginAsync(string username, string password, bool remember, CancellationToken ct) => Task.FromResult(new StaffSignIn(StaffSignInStep.Failed));
        public Task<AccountSession?> CompleteAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct) => Task.FromResult<AccountSession?>(null);
        public Task<TotpEnrollment?> BeginEnrollmentAsync(string challengeToken, CancellationToken ct) => Task.FromResult<TotpEnrollment?>(null);
        public Task<(AccountSession Session, string[] RecoveryCodes)?> ConfirmEnrollmentAsync(string challengeToken, string code, CancellationToken ct) =>
            Task.FromResult<(AccountSession, string[])?>(null);

        Task<VerifiedAccount?> IStaffSessions.AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(staffTokens.Contains(token) ? new VerifiedAccount(Someone) : null);

        Task<VerifiedAccount?> IAccountAuthentication.AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(token == StaffToken || token == PlayerToken ? new VerifiedAccount(Someone) : null);

        public Task SignOutAsync(string token, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> RegisterAsync(string username, string password, CancellationToken ct) => Task.FromResult(false);
        public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken ct) => Task.FromResult<AccountSession?>(null);
        public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken ct) => Task.FromResult<GameTicket?>(null);
    }

}