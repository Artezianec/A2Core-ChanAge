using Aion.Launcher;

namespace AionLauncher;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Elevated helper modes (firewall setup, hosts session, cleanup) run without a window.
        if (Elevated.Run(args) is { } exit) return exit;
        // After a self-update: the previous exe (still closing) and any unfinished download.
        if (Environment.ProcessPath is { } exe) _ = LauncherUpdate.RemoveLeftoversAsync(exe);
        ApplicationConfiguration.Initialize();
        var sidecar = Path.Combine(AppContext.BaseDirectory, "launcher.json");
        LauncherConfig? defaultServer = null;
        try { defaultServer = LauncherConfig.LoadDefault(sidecar) ?? LauncherConfig.Load(); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        {
            LauncherLoc.Apply(null);
            MessageBox.Show(LauncherLoc.InvalidLauncherJson(error.Message), LauncherLoc.DialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var settings = LauncherSettings.Load();
        LauncherLoc.Apply(settings.UiLanguage);
        if (settings.Migrate(defaultServer, SupportedBuild.Embedded.Fingerprint))
        {
            try { settings.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (Install.Running) Install.RegisterLinks();
        var link = args.FirstOrDefault(a => a.StartsWith(ServerSetup.LinkScheme + ":", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(settings, uninstall: args is ["--uninstall"], link));
        return 0;
    }
}
