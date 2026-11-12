using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Aion.Application.Authentication;

namespace Aion.Host;

/// <summary>
/// Player downloads (the launcher) for signed-in accounts only. Self-update stays exe-only; the account page can request a ZIP
/// (exe + launcher.json) via POST /api/downloads/launcher?package=zip.
/// </summary>
public sealed class Downloads(string folder, TimeProvider clock)
{
    public const string LauncherFile = "A2Core Launcher.exe";
    const string Route = "/download/launcher";
    const string ZipRoute = "/download/launcher.zip";
    public const string ZipEntryName = "A2Core Launcher.exe";
    public const string VersionFile = LauncherFile + ".version";
    public const string VersionSuffix = ".version";
    sealed record ZipCache(long Length, DateTime Written, string Json, byte[] Zip);
    ZipCache? zipped;
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    readonly ConcurrentDictionary<string, DateTimeOffset> tickets = new();
    (long Length, DateTime Written, string Sha256)? hashed;

    public bool LauncherAvailable => File.Exists(Path.Combine(folder, LauncherFile));

    string Issue()
    {
        var now = clock.GetUtcNow();
        foreach (var stale in tickets.Where(t => t.Value <= now).ToList()) tickets.TryRemove(stale);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tickets[ticket] = now + Lifetime;
        return ticket;
    }

    bool Valid(string? ticket) => ticket is { Length: 64 } && tickets.TryGetValue(ticket, out var until) && until > clock.GetUtcNow();

    async Task<object?> ManifestAsync(CancellationToken ct)
    {
        var info = new FileInfo(Path.Combine(folder, LauncherFile));
        if (!info.Exists) return null;
        string sha256;
        if (hashed is { } cached && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc) sha256 = cached.Sha256;
        else
        {
            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            hashed = (info.Length, info.LastWriteTimeUtc, sha256);
        }
        var versionPath = Path.Combine(folder, VersionFile);
        var version = File.Exists(versionPath) ? (await File.ReadAllTextAsync(versionPath, ct)).Trim() : "";
        return new { sha256, size = info.Length, version = version.Length > 0 ? version : null };
    }

    byte[] Zip(string path, byte[] launcherJson)
    {
        var info = new FileInfo(path);
        var json = Convert.ToBase64String(launcherJson);
        if (zipped is { } cached && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc && cached.Json == json) return cached.Zip;
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(path, ZipEntryName, CompressionLevel.NoCompression);
            var entry = archive.CreateEntry("launcher.json", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(launcherJson);
        }
        var zip = buffer.ToArray();
        zipped = new ZipCache(info.Length, info.LastWriteTimeUtc, json, zip);
        return zip;
    }

    public void Map(WebApplication app, Func<Microsoft.AspNetCore.Http.HttpRequest, byte[]>? launcherJson = null)
    {
        app.MapPost("/api/downloads/launcher", async (HttpContext context, IAccountAuthentication auth, string? package, CancellationToken ct) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (await auth.AuthenticateAsync(header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "", ct) is null)
                return Results.Unauthorized();
            if (!LauncherAvailable) return Results.NotFound();
            var zip = package == "zip" && launcherJson is not null;
            return Results.Ok(new { url = $"{(zip ? ZipRoute : Route)}?ticket={Issue()}" });
        }).RequireRateLimiting("account-entry");
        app.MapGet("/api/downloads/launcher/manifest", async (CancellationToken ct) =>
            await ManifestAsync(ct) is { } manifest ? Results.Ok(manifest) : Results.NotFound());
        app.MapGet(Route, (string? ticket) =>
            Valid(ticket) && LauncherAvailable
                ? Results.File(Path.Combine(folder, LauncherFile), "application/octet-stream", LauncherFile, enableRangeProcessing: true)
                : Results.NotFound());
        if (launcherJson is not null)
            app.MapGet(ZipRoute, (Microsoft.AspNetCore.Http.HttpRequest request, string? ticket) =>
                Valid(ticket) && LauncherAvailable
                    ? Results.File(Zip(Path.Combine(folder, LauncherFile), launcherJson(request)), "application/zip", "A2Core Launcher.zip", enableRangeProcessing: true)
                    : Results.NotFound());
    }
}
