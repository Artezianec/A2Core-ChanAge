// Adapted from KiskEmu (AGPL-3.0, https://github.com/Doxylamin/KiskEmu).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aion.Host;

/// <summary>
/// Public launcher metadata: GET /api/launcher/config and build references at /api/launcher/builds/{fingerprint}.
/// </summary>
public sealed class LauncherEndpoints(byte[] buildJson, string fingerprint)
{
    public const string ConfigPath = "/api/launcher/config";
    public string Fingerprint => fingerprint;

    public static LauncherEndpoints? Embedded()
    {
        using var stream = typeof(LauncherEndpoints).Assembly.GetManifestResourceStream("supported-build.json");
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return FromJson(copy.ToArray());
    }

    public static LauncherEndpoints FromJson(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stated = root.GetProperty("fingerprint").GetString() ?? "";
        var computed = ComputeFingerprint(root.GetProperty("files"));
        if (!stated.Equals(computed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("supported-build.json: the fingerprint does not match its files");
        return new LauncherEndpoints(json, computed);
    }

    public static string ComputeFingerprint(JsonElement files)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.EnumerateArray())
        {
            if (!file.TryGetProperty("quick", out var quick) || !quick.GetBoolean()) continue;
            digest.AppendData(Encoding.UTF8.GetBytes(
                $"{file.GetProperty("path").GetString()!.Replace('\\', '/').ToLowerInvariant()}\0{file.GetProperty("size").GetInt64()}\0{file.GetProperty("sha256").GetString()!.ToLowerInvariant()}\n"));
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    public void Map(WebApplication app, Func<string> siteName, string publicAddress, int bootstrapPort)
    {
        app.MapGet(ConfigPath, (IConfiguration configuration) =>
        {
            var section = configuration.GetSection("Aion:Launcher");
            var version = section["ClientVersion"]?.Trim() is { Length: > 0 } named ? named[..Math.Min(named.Length, 40)] : "2.0.6.0";
            var versions = new List<object> { new { id = version, name = version, kind = "private", build = fingerprint } };
            if (section.GetValue<bool?>("AllowSteam") ?? true)
                versions.Add(new { id = "steam-latest", name = "Official latest (Steam)", kind = "steam" });
            return Results.Ok(new
            {
                name = siteName(), serverAddress = publicAddress, bootstrapPort, savedDirSuffix = SavedDirSuffix(configuration),
                clientVersions = versions, selectedClientVersion = version,
            });
        });
        app.MapGet("/api/launcher/builds/{id}", (string id) =>
            id.Equals(fingerprint, StringComparison.OrdinalIgnoreCase) ? Results.Bytes(buildJson, "application/json") : Results.NotFound());
    }

    static string? SavedDirSuffix(IConfiguration configuration) =>
        configuration["Aion:Launcher:SavedDirSuffix"]?.Trim() is { Length: > 0 } suffix ? suffix : null;

    public static byte[] LauncherJson(HttpRequest request, IConfiguration configuration, string siteName, string publicAddress, int bootstrapPort)
    {
        var accountUrl = configuration["Aion:Launcher:AccountUrl"]?.Trim().TrimEnd('/') is { Length: > 0 } configured
            ? configured
            : $"{request.Scheme}://{request.Host.Value}";
        var pin = configuration["Aion:Launcher:CertificateThumbprint"]?.Replace(":", "").Trim() is { Length: 40 } thumbprint ? thumbprint : null;
        var json = new Dictionary<string, object?>
        {
            ["name"] = siteName, ["accountUrl"] = accountUrl, ["serverAddress"] = publicAddress, ["bootstrapPort"] = bootstrapPort,
        };
        if (SavedDirSuffix(configuration) is { } suffix) json["savedDirSuffix"] = suffix;
        if (pin is not null) json["certificateThumbprint"] = pin;
        return JsonSerializer.SerializeToUtf8Bytes(json, new JsonSerializerOptions { WriteIndented = true });
    }
}
