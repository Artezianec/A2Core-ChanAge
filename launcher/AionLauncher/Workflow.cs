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

    public bool Installed => InstallFolder is { } folder && File.Exists(Path.Combine(folder, Build.Executable));

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

    public string? DownloadFolder()
    {
        if (settings.DownloadFolder is { } previous && Build.Fingerprint.Equals(settings.DownloadBuild, StringComparison.OrdinalIgnoreCase)) return previous;
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
        if (InstallFolder is null) return false;
        if (!await Elevated.RunAsync("--setup-firewall", InstallFolder, config.ServerAddress)) return false;
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
        GamePlatformIni.Write(GamePlatformIni.PathFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), config.SavedDirSuffix),
            config.BootstrapUrl(launch.Authorization));

        if (!version.IsSteam) ClientPatchSupport.DeployIfEnabled(folder, settings.AllowClientPatchPaks);
        else if (settings.AllowClientPatchPaks) status(LauncherLoc.StatusPatchNotOnSteam);

        status(LauncherLoc.StatusStartingGame);
        var exe = Path.Combine(folder, executable.Replace('/', '\\'));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        if (RedirectModeSelection.PreferInject(settings.RedirectMode, config))
        {
            var dllPath = DnsInject.PrepareRedirectBundle(config.Server);
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
        var helper = Elevated.Start("--hosts-session", folder, config.ServerAddress)
            ?? throw new LauncherException(LauncherLoc.ErrHostsCancelled);
        helper.Dispose();
        for (var i = 0; i < 30 && !HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)); i++) await Task.Delay(500, ct);
        if (!HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)))
            throw new LauncherException(LauncherLoc.ErrHostsNotAdded);
    }

    public async Task<BuildCheck> VerifyAllAsync(IProgress<(long, long)> progress, CancellationToken ct)
    {
        var result = await ClientFiles.CheckAsync(InstallFolder!, Build, CheckLevel.Full, progress, ct);
        if (result.Ok) { Copy.VerifiedStamp = QuickStamp(InstallFolder!); settings.Save(); }
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
