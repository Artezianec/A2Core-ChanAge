// Multi-server settings adapted from KiskEmu (AGPL-3.0, https://github.com/Doxylamin/KiskEmu).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Launcher;

namespace AionLauncher;

/// <summary>One saved server: settings refreshed from the host and the player's sign-in there.</summary>
sealed class ServerEntry
{
    public LauncherConfig Config { get; set; } = null!;
    public string? Username { get; set; }
    public bool KeepSignedIn { get; set; } = true;
    public string? SessionToken { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    public string? SelectedClientVersion { get; set; }

    [JsonIgnore] public string Url => Config.AccountUrl;

    public string? Session()
    {
        if (SessionToken is null || SessionExpiresAt is { } ends && ends <= DateTimeOffset.UtcNow) return null;
        return LauncherSettings.Unprotect(SessionToken);
    }

    public void SetSession(string? token, DateTimeOffset? expiresAt)
    {
        SessionToken = token is null ? null : LauncherSettings.Protect(token);
        SessionExpiresAt = token is null ? null : expiresAt;
    }
}

/// <summary>One private game copy per supported-build fingerprint (shared by servers on the same build).</summary>
sealed class ClientCopy
{
    public string? Folder { get; set; }
    public string? VerifiedStamp { get; set; }
    /// <summary>IPv4 the firewall rules allow this copy to reach (updated when switching servers).</summary>
    public string? FirewallAddress { get; set; }
}

/// <summary>
/// %LOCALAPPDATA%\A2CorePrivate\launcher-settings.json — saved servers, game copies, global options.
/// Legacy single-server fields are migrated once (<see cref="Migrate"/>).
/// </summary>
sealed class LauncherSettings
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A2CorePrivate");
    static string FilePath => Path.Combine(Folder, "launcher-settings.json");
    public static string BuildsFolder => Path.Combine(Folder, "builds");

    public List<ServerEntry> Servers { get; set; } = [];
    public string? CurrentServer { get; set; }
    public string? SeededDefault { get; set; }
    public Dictionary<string, ClientCopy> Copies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool NoInstall { get; set; }
    public string? DownloadFolder { get; set; }
    public string? DownloadBuild { get; set; }
    public string? SteamAccount { get; set; }
    public string? SteamToken { get; set; }
    public bool AllowClientPatchPaks { get; set; }
    public string? RedirectMode { get; set; }
    public string? UiLanguage { get; set; }

    // Legacy single-server layout (read once by Migrate).
    public string? InstallFolder { get; set; }
    public string? VerifiedStamp { get; set; }
    public string? Username { get; set; }
    public bool? KeepSignedIn { get; set; }
    public string? SessionToken { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    public string? SelectedClientVersion { get; set; }

    [JsonIgnore] public ServerEntry? Current => Servers.FirstOrDefault(s => s.Url.Equals(CurrentServer, StringComparison.OrdinalIgnoreCase)) ?? Servers.FirstOrDefault();

    public ServerEntry? Find(string accountUrl) => Servers.FirstOrDefault(s => s.Url.Equals(accountUrl, StringComparison.OrdinalIgnoreCase));

    public ClientCopy Copy(string fingerprint) => Copies.TryGetValue(fingerprint, out var copy) ? copy : Copies[fingerprint] = new ClientCopy();

    public bool Migrate(LauncherConfig? defaultServer, string embeddedFingerprint)
    {
        var changed = false;
        if (defaultServer is not null && !defaultServer.AccountUrl.Equals(SeededDefault, StringComparison.OrdinalIgnoreCase))
        {
            if (Find(defaultServer.AccountUrl) is null)
            {
                var entry = new ServerEntry { Config = defaultServer };
                AdoptLegacySignIn(entry);
                Servers.Insert(0, entry);
            }
            SeededDefault = CurrentServer = defaultServer.AccountUrl;
            changed = true;
        }
        if (InstallFolder is not null)
        {
            var copy = Copy(embeddedFingerprint);
            copy.Folder ??= InstallFolder;
            copy.VerifiedStamp ??= VerifiedStamp;
            copy.FirewallAddress ??= defaultServer?.ServerAddress;
            changed = true;
        }
        if (DownloadFolder is not null && DownloadBuild is null) { DownloadBuild = embeddedFingerprint; changed = true; }
        if (InstallFolder is not null || VerifiedStamp is not null) changed = true;
        InstallFolder = VerifiedStamp = null;
        if (SessionToken is not null || SessionExpiresAt is not null) changed = true;
        SessionToken = null;
        SessionExpiresAt = null;
        if (CurrentServer is null && Servers.Count > 0) { CurrentServer = Servers[0].Url; changed = true; }
        return changed;
    }

    public void AdoptLegacySignIn(ServerEntry entry)
    {
        if (Servers.Count > 0 || Username is null && KeepSignedIn is null && SelectedClientVersion is null) return;
        entry.Username ??= Username;
        entry.KeepSignedIn = KeepSignedIn ?? entry.KeepSignedIn;
        entry.SelectedClientVersion ??= SelectedClientVersion;
        Username = SelectedClientVersion = null;
        KeepSignedIn = null;
    }

    public SteamDownload.SignIn? SteamSignIn()
    {
        if (SteamAccount is null || SteamToken is null) return null;
        return Unprotect(SteamToken) is { } refreshToken ? new(SteamAccount, refreshToken) : null;
    }

    public void SetSteamSignIn(SteamDownload.SignIn? signIn)
    {
        SteamAccount = signIn?.Account;
        SteamToken = signIn is null ? null : Protect(signIn.RefreshToken);
    }

    public static string Protect(string secret) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string protectedBase64)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static LauncherSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath) ? JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath), Json) ?? new() : new();
            settings.Servers.RemoveAll(s => { try { s.Config.Validate(); return false; } catch (Exception e) when (e is InvalidDataException or NullReferenceException) { return true; } });
            settings.Copies = new Dictionary<string, ClientCopy>(settings.Copies, StringComparer.OrdinalIgnoreCase);
            return settings;
        }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        try { if (Directory.Exists(BuildsFolder)) Directory.Delete(BuildsFolder, recursive: true); } catch (IOException) { }
    }
}
