using System.Text.Json;
using Aion.Host.Native;
using Aion.World;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aion.Host.Tests;

public sealed class StatusEndpointTests
{
    [Fact]
    public async Task Status_json_includes_online_player_count()
    {
        var presence = new WorldPresence();
        presence.Enter("Hero", new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(1)), Guid.NewGuid());
        presence.Enter("Bot", new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(2)) { Bot = true }, Guid.NewGuid());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(presence);
        builder.Services.AddSingleton(new PublicPlayerCount(presence, null, TimeProvider.System));
        var app = builder.Build();
        app.MapGet("/api/status", async (IServiceProvider services, CancellationToken ct) =>
        {
            var snap = await services.GetRequiredService<PublicPlayerCount>().SnapshotAsync(ct);
            return Results.Ok(new { accountsConfigured = true, registrationOpen = true, launcherAvailable = false, gameReady = true, online = snap.Online, onlineBots = snap.OnlineBots });
        });
        await app.StartAsync();

        var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/status");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("accountsConfigured").GetBoolean());
        Assert.True(root.GetProperty("gameReady").GetBoolean());
        Assert.Equal(1, root.GetProperty("online").GetInt32());
        await app.StopAsync();
    }
}