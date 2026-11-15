using Aion.Infrastructure.Postgres;
using Npgsql;

namespace Aion.Host;

/// <summary>Warns at startup when character creation cannot succeed because starter profiles are missing.</summary>
public sealed class StarterProfileHealth(NpgsqlDataSource source, ILogger<StarterProfileHealth> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT count(*)::int FROM game.starter_profiles WHERE enabled", connection);
            var enabled = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (enabled == 0)
            {
                logger.LogWarning(
                    "No enabled rows in game.starter_profiles; character creation will fail with profile_unavailable. {Hint}",
                    PostgresGameplayStore.StarterSetupHint);
            }
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(error, "Could not check game.starter_profiles at startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
