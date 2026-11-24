using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aion.Host.Database;

/// <summary>
/// Applies pending SQL migrations through <c>tools/postgres_dev.py migrate</c> (idempotent;
/// tracked in <c>public.schema_migrations</c>). The host does not embed migration SQL so the
/// checked hashes and LF normalization stay in one place with local dev and deploy scripts.
/// </summary>
public static class PostgresMigrationRunner
{
    public static bool Enabled(IConfiguration config) =>
        config.GetValue("Aion:Database:MigrateOnStartup",
            string.Equals(config["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase));

    public static void ApplyIfEnabled(IConfiguration config, ILogger logger)
    {
        if (!Enabled(config))
            return;
        if (config.GetConnectionString("Aion") is not { Length: > 0 } ||
            config.GetConnectionString("Auth") is not { Length: > 0 })
        {
            logger.LogDebug("Skipping database migration: connection strings are not configured.");
            return;
        }
        var tool = ResolveTool(config);
        if (tool is null)
        {
            logger.LogWarning("Aion:Database:MigrateOnStartup is enabled but no postgres_dev.py tool was found; schema may be stale.");
            return;
        }
        logger.LogInformation("Applying pending database migrations via {Tool}", tool);
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(tool);
        start.ArgumentList.Add("migrate");
        if (config["Aion:Database:PgHome"] is { Length: > 0 } home)
            start.Environment["AION_PG_HOME"] = home;
        else if (config["Aion:Backups:PgHome"] is { Length: > 0 } backupHome)
            start.Environment["AION_PG_HOME"] = backupHome;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("postgres_dev.py did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromMinutes(3));
        if (process.ExitCode != 0)
        {
            var tail = (stderr + stdout).Split('\n').Reverse().FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
            throw new InvalidOperationException(
                $"Database migration failed (exit {process.ExitCode}){(tail is { Length: > 0 } line ? ": " + line : "")}");
        }
        if (stdout.Trim().Length > 0)
            logger.LogDebug("postgres_dev migrate: {Output}", stdout.Trim());
    }

    static string? ResolveTool(IConfiguration config)
    {
        foreach (var candidate in new[]
                 {
                     config["Aion:Database:Tool"],
                     config["Aion:Backups:Tool"],
                     FindRepoTool("tools/postgres_dev.py"),
                 })
        {
            if (candidate is { Length: > 0 } path && File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

    static string? FindRepoTool(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}