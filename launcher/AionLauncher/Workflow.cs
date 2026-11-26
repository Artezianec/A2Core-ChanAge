using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aion.Launcher;
using Microsoft.Win32;

namespace AionLauncher;

sealed class LauncherException(string message) : Exception(message);

/// <summary>Where the supported client build can come from on this PC.</summary>
sealed record ClientSource(string Folder, ClientFiles.Mode Mode, string Description);

/// <summary>Setup and play on one saved server, without UI: the form shows status and progress.</summary>
sealed class Workflow(ServerEntry server, LauncherSettings settings)
{
    public SupportedBuild Build { get; private set; } = SupportedBuild.Embedded;
    public LauncherConfig Config => server.Config;
    LauncherConfig config => server.Config;
    public ServerEntry Profile => server;
    public LauncherSettings Settings => settings;
    public AccountClient Account { get; } = new(server.Config);
    public ClientVersion Version { get; private set; } = ClientVersions.Selected(server.Config, server.SelectedClientVersion);
    public string? GameFolder { get; private set; }

    ClientCopy Copy => settings.Copy(Build.Fingerprint);
    public string? InstallFolder => settings.Copies.TryGetValue(Build.Fingerprint, out var copy) ? copy.Folder : null;
    public IEnumerable<string> AllCopies => settings.Copies.Values.Select(c => c.Folder).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The chosen client is there to start: the private copy of <see cref="Build"/>, or for the Steam version the
    /// player's Steam copy (any build; it is never checked against the reference or copied).
    /// </summary>
    public bool Installed => Version.IsSteam
        ? ClientVersions.SteamReady(Version, config, FindSteam(), Build.Executable)
        : InstallFolder is { } folder && File.Exists(Path.Combine(folder, Build.Executable));

    /// <summary>The server's Steam version, when it offers one and the player's Steam copy is there.</summary>
    public ClientVersion? SteamAlternative => ClientVersions.SteamAlternative(config, FindSteam(), Build.Executable);

    public static SteamInstall? FindSteam()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return path is not null && Directory.Exists(path) ? new SteamInstall(Path.GetFullPath(path)) : null;
    }

    public async Task PrepareAsync(CancellationToken ct)
    {
        try
        {
            if (await Account.ServerConfigAsync(ct) is { } reply)
            {
                server.Config = ServerSetup.Apply(server.Config, new Uri(server.Config.AccountUrl), reply, null);
                Version = ClientVersions.Selected(server.Config, server.SelectedClientVersion);
                settings.Save();
            }
        }
        catch (Exception error) when (error is AccountException or InvalidDataException or System.Text.Json.JsonException) { }
        Build = await BuildForAsync(Version, ct);
    }

    async Task<SupportedBuild> BuildForAsync(ClientVersion version, CancellationToken ct)
    {
        if (version.IsSteam || version.Build is not { Length: > 0 } fingerprint
            || fingerprint.Equals(SupportedBuild.Embedded.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return SupportedBuild.Embedded;
        var cached = Path.Combine(LauncherSettings.BuildsFolder, fingerprint.ToLowerInvariant() + ".json");
        if (File.Exists(cached))
            try { return SupportedBuild.Parse(await File.ReadAllBytesAsync(cached, ct), fingerprint); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { File.Delete(cached); }
        var (build, json) = await Account.BuildReferenceAsync(fingerprint, ct);
        Directory.CreateDirectory(LauncherSettings.BuildsFolder);
        await File.WriteAllBytesAsync(cached, json, ct);
        return build;
    }

    public async Task ChooseVersionAsync(ClientVersion version, CancellationToken ct)
    {
        Build = await BuildForAsync(version, ct);
        Version = version;
        server.SelectedClientVersion = version.Id;
        settings.Save();
    }

    public string TargetFor(string source)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = ClientFiles.SameVolume(source, local)
            ? Path.Combine(local, "A2CorePrivate")
            : Path.Combine(Path.GetPathRoot(Path.GetFullPath(source))!, "A2CorePrivate");
        return Path.Combine(root, CopyName);
    }

    string CopyName => Build.Fingerprint.Equals(SupportedBuild.Embedded.Fingerprint, StringComparison.OrdinalIgnoreCase)
        ? "AION2"
        : "AION2-" + Build.Fingerprint[..12];

    /// <summary>The game version the server runs, for the player, or null when the server names none.</summary>
    public string? GameVersionText => string.IsNullOrWhiteSpace(Version.Name) ? null : Version.Name;

    /// <summary>Whether <paramref name="folder"/>'s drive has room for what is still to download.</summary>
    public bool HasRoom(string folder) => FreeBytes(folder) > StillNeeded(folder) + DownloadReserveBytes;

    /// <summary>
    /// The player picked where to download (before a download starts): a folder named for the copy is made inside it.
    /// A started download stays where it is, its files are there.
    /// </summary>
    public void ChooseDownloadFolder(string parent)
    {
        settings.DownloadChoice = Path.Combine(Path.GetFullPath(parent), CopyName);
        settings.DownloadChoiceBuild = Build.Fingerprint;
        settings.Save();
    }

    public static long FreeBytes(string folder) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;

    public async Task<ClientSource?> FindSourceAsync(SteamInstall steam)
    {
        if (steam.FindApp() is { } app && (app.Manifest == Build.ManifestId || (await ClientFiles.CheckAsync(app.Folder, Build, CheckLevel.Sizes)).Ok))
        {
            var mode = ClientFiles.SameVolume(app.Folder, TargetFor(app.Folder)) ? ClientFiles.Mode.HardLink : ClientFiles.Mode.Copy;
            if (mode == ClientFiles.Mode.HardLink || FreeBytes(TargetFor(app.Folder)) > Build.TotalBytes)
                return new(app.Folder, mode, app.Manifest == Build.ManifestId ? LauncherLoc.SteamSourceInstall : LauncherLoc.SteamSourceInstallSizes);
        }
        return null;
    }

    public long StillNeeded(string folder) => Build.TotalBytes - (Directory.Exists(folder) ? ClientFiles.PresentBytes(folder, Build) : 0);

    public const long DownloadReserveBytes = 2L << 30;

    public void EnsureDownloadSpace(string folder)
    {
        var needed = StillNeeded(folder) + DownloadReserveBytes;
        if (FreeBytes(folder) < needed)
            throw new LauncherException(LauncherLoc.ErrNotEnoughSpace(Path.GetPathRoot(Path.GetFullPath(folder))!, $"{needed / 1e9:0.#}", $"{StillNeeded(folder) / 1e9:0.#}"));
    }

    /// <summary>
    /// Where the download goes: the folder of an interrupted download, else the folder the player chose, else %LOCALAPPDATA%
    /// on the system drive when it has room, else &lt;drive&gt;:\A2CorePrivate on the fixed drive with the most free space.
    /// Null when no drive has room.
    /// </summary>
    public string? DownloadFolder()
    {
        if (settings.DownloadFolder is { } previous && Build.Fingerprint.Equals(settings.DownloadBuild, StringComparison.OrdinalIgnoreCase)) return previous;
        if (settings.DownloadChoice is { } chosen && Build.Fingerprint.Equals(settings.DownloadChoiceBuild, StringComparison.OrdinalIgnoreCase)) return chosen;
        var local = TargetFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (FreeBytes(local) > StillNeeded(local) + DownloadReserveBytes) return local;
        var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
        return best is not null && best.AvailableFreeSpace > StillNeeded(Path.Combine(best.RootDirectory.FullName, "A2CorePrivate", CopyName)) + DownloadReserveBytes
            ? Path.Combine(best.RootDirectory.FullName, "A2CorePrivate", CopyName) : null;
    }

    public async Task<string> DownloadAsync(Action<string> qrCode, IProgress<SteamDownload.Status> progress, CancellationToken ct) =>
        await DownloadAsync(DownloadFolder() ?? throw new LauncherException(LauncherLoc.ErrNoDriveSpace(Build.TotalBytes / 1e9)),
            null, qrCode, progress, ct);

    public async Task<string> DownloadAsync(string folder, IReadOnlySet<string>? onlyPaths, Action<string> qrCode, IProgress<SteamDownload.Status> progress, CancellationToken ct)
    {
        EnsureDownloadSpace(folder);
        settings.DownloadFolder = folder;
        settings.DownloadBuild = Build.Fingerprint;
        settings.Save();
        using var steam = new SteamDownload();
        steam.QrCode += qrCode;
        var signIn = await steam.SignInAsync(settings.SteamSignIn(), ct);
        settings.SetSteamSignIn(signIn);
        settings.Save();
        await steam.DownloadAsync(Build, folder, progress, ct, onlyPaths);
        return folder;
    }

    public async Task InstallAsync(ClientSource source, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        var target = TargetFor(source.Folder);
        Directory.CreateDirectory(target);
        status(source.Mode == ClientFiles.Mode.HardLink ? LauncherLoc.StatusLinkingSteam : LauncherLoc.StatusCopyingSteam);
        await ClientFiles.PlaceAsync(source.Folder, target, Build, source.Mode, progress, ct);
        status(LauncherLoc.StatusCheckingGameFiles);
        var check = await ClientFiles.CheckAsync(target, Build, CheckLevel.Quick, progress, ct);
        if (!check.Ok) throw new LauncherException(LauncherLoc.ErrInstallMismatch(check.Problem!));
        await FinishAsync(target, status);
    }

    public async Task AdoptAsync(string folder, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        folder = ClientFiles.ResolveGameFolder(folder, Build)
            ?? throw new LauncherException(LauncherLoc.ErrNoGameFolder);
        status(LauncherLoc.StatusCheckingGameFiles);
        var check = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Quick, progress, ct);
        if (!check.Ok) throw new LauncherException(LauncherLoc.ErrWrongVersion(check.Problem!));
        await FinishAsync(Path.GetFullPath(folder), status);
    }

    public async Task ImportExistingAsync(string pickedFolder, bool useInPlace, Action<string> qrCode, Action<string> status,
        IProgress<SteamDownload.Status> downloadProgress, IProgress<(long, long)> checkProgress, CancellationToken ct)
    {
        var source = ClientFiles.ResolveGameFolder(pickedFolder, Build)
            ?? throw new LauncherException(LauncherLoc.ErrNoGameFolder);
        status(LauncherLoc.StatusCheckingMatches);
        var survey = await ClientFiles.SurveyAsync(source, Build, ct);
        if (survey.Complete)
        {
            await AdoptAsync(source, status, checkProgress, ct);
            return;
        }
        var target = useInPlace ? source : TargetFor(source);
        if (!useInPlace)
        {
            Directory.CreateDirectory(target);
            var mode = ClientFiles.SameVolume(source, target) ? ClientFiles.Mode.HardLink : ClientFiles.Mode.Copy;
            status(mode == ClientFiles.Mode.HardLink ? LauncherLoc.StatusLinkingMatching : LauncherLoc.StatusCopyingMatching);
            await ClientFiles.PlaceMatchingAsync(source, target, Build, mode, checkProgress, ct);
            survey = await ClientFiles.SurveyAsync(target, Build, ct);
            if (survey.Complete)
            {
                await FinishAsync(target, status);
                return;
            }
        }
        EnsureDownloadSpace(target);
        settings.DownloadFolder = target;
        settings.DownloadBuild = Build.Fingerprint;
        settings.Save();
        status(LauncherLoc.StatusDownloadingMissing(survey.ToDownload.Count, survey.StillNeeded(Build) / 1e9));
        await DownloadAsync(target, survey.ToDownload.ToHashSet(StringComparer.OrdinalIgnoreCase), qrCode, downloadProgress, ct);
        var after = await ClientFiles.CheckAsync(target, Build, CheckLevel.Quick, checkProgress, ct);
        if (!after.Ok) throw new LauncherException(LauncherLoc.ErrStillMismatch(after.Problem!));
        TryDelete(Path.Combine(target, SteamDownload.StateFile));
        await FinishAsync(target, status);
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }

    async Task FinishAsync(string folder, Action<string> status)
    {
        status(LauncherLoc.StatusAllowFirewall);
        if (!await Elevated.RunAsync("--setup-firewall", folder, config.ServerAddress))
            throw new LauncherException(LauncherLoc.ErrFirewallDeclined);
        Copy.Folder = folder;
        Copy.VerifiedStamp = QuickStamp(folder);
        Copy.FirewallAddress = config.ServerAddress;
        settings.DownloadFolder = null;
        settings.DownloadBuild = null;
        settings.DownloadChoice = null;
        settings.DownloadChoiceBuild = null;
        settings.SetSteamSignIn(null);
        settings.Save();
    }

    public static bool FirewallRulesPresent()
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRules.RuleName}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    public async Task<bool> RepairFirewallAsync()
    {
        // The Steam copy has no rules (they would block the official game too), and with it alone there is no private copy.
        if (InstallFolder is not { } folder)
            throw new LauncherException(Version.IsSteam
                ? LauncherLoc.ErrNoPrivateCopyForFirewall
                : LauncherLoc.ErrGameNotInstalled);
        if (!await Elevated.RunAsync("--setup-firewall", folder, config.ServerAddress)) return false;
        Copy.FirewallAddress = config.ServerAddress;
        settings.Save();
        return true;
    }

    public async Task<Process> PlayAsync(SteamInstall steam, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        var version = Version;
        var folder = version.IsSteam
            ? ClientVersions.SteamFolder(version, config, steam) ?? throw new LauncherException(LauncherLoc.ErrSteamCopyMissing)
            : version.ClientPath ?? InstallFolder ?? throw new LauncherException(LauncherLoc.ErrGameNotInstalled);
        if (Elevated.GameRunning(folder)) throw new LauncherException(LauncherLoc.ErrGameAlreadyRunning);
        GameFolder = folder;
        if (Process.GetProcessesByName("steam").Length == 0)
        {
            status(LauncherLoc.StatusStartingSteam);
            Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
            for (var i = 0; i < 60 && Process.GetProcessesByName("steam").Length == 0; i++) await Task.Delay(1000, ct);
            await Task.Delay(5000, ct);
        }

        if (!version.IsSteam)
        {
            status(LauncherLoc.StatusCheckingGameFiles);
            var sizes = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Sizes, null, ct);
            if (!sizes.Ok) throw new LauncherException(LauncherLoc.ErrGameFilesChanged(sizes.Problem!));
            var stamp = QuickStamp(folder);
            if (stamp != Copy.VerifiedStamp)
            {
                var quick = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Quick, progress, ct);
                if (!quick.Ok) throw new LauncherException(LauncherLoc.ErrGameFilesChangedUninstall(quick.Problem!));
                Copy.VerifiedStamp = stamp;
                settings.Save();
            }
            if (!FirewallRulesPresent() || Copy.FirewallAddress != config.ServerAddress)
            {
                status(Copy.FirewallAddress is { } other && other != config.ServerAddress
                    ? LauncherLoc.StatusRepaintFirewall : LauncherLoc.StatusRestoringFirewall);
                if (!await RepairFirewallAsync()) throw new LauncherException(LauncherLoc.ErrFirewallMissing);
            }
        }

        var app = AppInfo.Read(await File.ReadAllBytesAsync(steam.AppInfoPath, ct), SteamInstall.AppId);
        var (executable, steamArguments) = AppInfo.LaunchArguments(app, config.SavedDirSuffix);
        var arguments = ClientVersions.Arguments(steamArguments, version);
        if (!executable.Equals(Build.Executable, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException(LauncherLoc.ErrSteamLaunchChanged);

        status(LauncherLoc.StatusGettingLaunchCode);
        var launch = await Account.AuthorizeLaunchAsync(ct);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bootstrapUrl = config.BootstrapUrl(launch.Authorization);
        var suffixIni = GamePlatformIni.PathFor(localAppData, config.SavedDirSuffix);
        GamePlatformIni.Write(suffixIni, bootstrapUrl);
        if (config.WriteDefaultSavedIniEnabled)
        {
            var defaultIni = GamePlatformIni.DefaultPathFor(localAppData);
            GamePlatformIni.Write(defaultIni, bootstrapUrl);
            DnsInject.Log($"wrote GamePlatform.ini to {suffixIni} and default Saved {defaultIni}");
        }
        else DnsInject.Log($"wrote GamePlatform.ini to {suffixIni}");

        if (!version.IsSteam) ClientPatchSupport.DeployIfEnabled(folder, settings.AllowClientPatchPaks);
        else if (settings.AllowClientPatchPaks) status(LauncherLoc.StatusPatchNotOnSteam);

        // A Steam copy must be started by Steam: -applaunch gives the client a fresh platform
        // ticket (-lp). The cached arguments in appinfo.vdf can be stale (the client then stops at
        // "Failed to retrieve Settings Server value [NP:0:0]"). Hosts/config redirection is applied
        // before Steam starts the game; the Steam launch adds its own -saveddirsuffix=Steam.
        if (version.IsSteam)
        {
            status(LauncherLoc.StatusPointingHosts);
            await EnsureHostsSessionAsync(folder, status, ct);
            status(LauncherLoc.StatusStartingGame);
            var steamExe = Path.Combine(steam.Root, "steam.exe");
            if (!File.Exists(steamExe)) throw new LauncherException(LauncherLoc.ErrSteamCopyMissing);
            var startedAt = DateTime.Now;
            var launchArgs = $"-applaunch {SteamInstall.AppId}";
            DnsInject.Log($"steam launch: \"{steamExe}\" {launchArgs}");
            Process.Start(new ProcessStartInfo(steamExe, launchArgs) { UseShellExecute = true });
            for (var attempt = 0; attempt < 240; attempt++)
            {
                await Task.Delay(500, ct);
                foreach (var process in Process.GetProcessesByName("AION2"))
                {
                    try
                    {
                        if (process.StartTime < startedAt) continue;
                        var path = process.MainModule?.FileName;
                        if (path is not null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                        {
                            DnsInject.Log($"steam launch started {path}");
                            return process;
                        }
                    }
                    catch (Exception) { }
                }
            }
            throw new LauncherException(LauncherLoc.ErrGameDidNotStart);
        }

        status(LauncherLoc.StatusStartingGame);
        var exe = Path.Combine(folder, executable.Replace('/', '\\'));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var expectedSuffix = "-saveddirsuffix=" + config.SavedDirSuffix;
        var hasSavedDirSuffix = arguments.Any(a => a.Equals(expectedSuffix, StringComparison.OrdinalIgnoreCase));
        DnsInject.Log($"game command line: \"{exe}\" {string.Join(" ", arguments)}; saveddirsuffix present={hasSavedDirSuffix} expected={expectedSuffix}");

        if (RedirectModeSelection.PreferInject(settings.RedirectMode, config))
        {
            var dllPath = DnsInject.PrepareRedirectBundle(config.Server, config.RedirectConfigHostsEnabled);
            if (dllPath is not null)
            {
                var process = DnsInject.TryStartWithInject(info, dllPath, out var injectError);
                if (process is not null) return process;
                DnsInject.Log($"DNS inject failed ({injectError}); falling back to hosts session.");
                status(LauncherLoc.StatusDnsInjectFailed);
            }
            else DnsInject.Log("A2Core.Redirect.dll is missing; falling back to hosts session.");
            status(LauncherLoc.StatusDnsInjectUnavailable);
        }

        status(LauncherLoc.StatusPointingHosts);
        await EnsureHostsSessionAsync(folder, status, ct);
        return Process.Start(info) ?? throw new LauncherException(LauncherLoc.ErrGameDidNotStart);
    }

    async Task EnsureHostsSessionAsync(string folder, Action<string> status, CancellationToken ct)
    {
        // The NCGuard sink owns its nine-host block while it runs (it removes it on shutdown). Adding the
        // launcher's block as well makes the sink's RestoreHosts see a changed file and abort.
        if (HostsFile.HasNcGuardBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)))
        {
            DnsInject.Log("NCGuard sink hosts block present; the launcher leaves it alone.");
            return;
        }
        var helper = Elevated.Start("--hosts-session", folder, config.ServerAddress,
            config.RedirectConfigHostsEnabled ? "config-hosts" : "noconfig-hosts")
            ?? throw new LauncherException(LauncherLoc.ErrHostsCancelled);
        helper.Dispose();
        for (var i = 0; i < 30 && !HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)); i++) await Task.Delay(500, ct);
        if (!HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)))
            throw new LauncherException(LauncherLoc.ErrHostsNotAdded);
    }

    public async Task<BuildCheck> VerifyAllAsync(IProgress<(long, long)> progress, CancellationToken ct)
    {
        // The Steam copy is newer than the reference by design; only a private copy is checked.
        if (Version.IsSteam || InstallFolder is not { } folder)
            return BuildCheck.Fail(Version.IsSteam ? LauncherLoc.SteamCopyNotChecked : LauncherLoc.ErrGameNotInstalled, 0);
        var result = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Full, progress, ct);
        if (result.Ok) { Copy.VerifiedStamp = QuickStamp(folder); settings.Save(); }
        return result;
    }

    public async Task RepairFilesAsync(Action<string> qrCode, IProgress<SteamDownload.Status> downloadProgress, IProgress<(long, long)> verifyProgress, CancellationToken ct)
    {
        var folder = InstallFolder ?? throw new LauncherException(LauncherLoc.ErrGameNotInstalled);
        var bad = await ClientFiles.FindHashMismatchesAsync(folder, Build, verifyProgress, ct);
        if (bad.Count == 0)
        {
            Copy.VerifiedStamp = QuickStamp(folder);
            settings.Save();
            return;
        }
        await DownloadAsync(folder, bad.ToHashSet(StringComparer.OrdinalIgnoreCase), qrCode, downloadProgress, ct);
        var after = await ClientFiles.FindHashMismatchesAsync(folder, Build, verifyProgress, ct);
        if (after.Count > 0) throw new LauncherException(LauncherLoc.ErrRepairFile(after[0]));
        TryDelete(Path.Combine(folder, SteamDownload.StateFile));
        Copy.VerifiedStamp = QuickStamp(folder);
        settings.Save();
    }

    string QuickStamp(string folder)
    {
        var text = new StringBuilder();
        foreach (var file in Build.Files.Where(f => f.Quick))
        {
            var info = new FileInfo(Path.Combine(folder, file.Path));
            text.Append(file.Path).Append('|').Append(info.Exists ? info.Length : -1).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
