using Velopack;

namespace HapticLink.App;

/// <summary>
/// Entry point. Velopack (installer/updater) runs first: Setup and the uninstaller start the exe with its own arguments,
/// run the matching hook and exit. It also applies a downloaded update on startup. One copy runs at a time.
///   --write-icon &lt;file.ico&gt;   writes the app icon (used once to make Assets\app.ico)
///   --setup                    opens only the setup window
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnFirstRun(_ => Installation.FirstRun())
            .OnBeforeUninstallFastCallback(_ => Installation.BeforeUninstall())
            .Run();

        if (args is ["--write-icon", var path]) { IconMaker.WriteIco(path); return 0; }
        if (args is ["--setup"])
        {
            // The setup window on its own (also while the tray app runs): for checking it, or setting up again by hand.
            ApplicationConfiguration.Initialize();
            Application.Run(new SetupWindow(required: false));
            return 0;
        }

        using var single = new Mutex(true, @"Local\HapticLink.App", out bool first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        AppLog.CatchUnhandled();
        AppLog.Write($"HapticLink {AppLog.Version} starting from {Environment.ProcessPath}");
        Application.Run(new TrayApp());
        return 0;
    }
}

/// <summary>Install-time steps Velopack runs: the first start after Setup, and clean-up before "Uninstall".</summary>
internal static class Installation
{
    /// <summary>First start after Setup: start with Windows (the tray icon and setup check live in this app).</summary>
    public static void FirstRun()
    {
        try { Startup.Set(true); } catch (Exception ex) { AppLog.Write($"First run: couldn't turn on Start with Windows ({ex.Message})"); }
    }

    /// <summary>
    /// Removes what setup installed (service, driver, speaker device, certificate) with one admin prompt; the uninstall
    /// step gets 30 seconds. If the prompt is declined, those stay (harmless) and can be removed by reinstalling and
    /// uninstalling again.
    /// </summary>
    public static void BeforeUninstall()
    {
        try { Startup.RemoveIfThisExe(); } catch (UnauthorizedAccessException) { }
        ServiceSetup.RemoveIfInstalled(TimeSpan.FromSeconds(25));
    }
}
