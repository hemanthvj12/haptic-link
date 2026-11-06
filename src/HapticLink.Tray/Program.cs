using System.Diagnostics;
using Velopack;

namespace HapticLink.App;

/// <summary>
/// HapticLink.exe, the tray. Velopack (installer/updater) runs first: Setup and the uninstaller start the exe with its own
/// arguments, run the matching hook and exit. It also applies a downloaded update on startup. One copy runs at a time.
///   --setup           opens only the setup window (HapticLink.Setup.exe; also while the tray runs)
///   --check-updates   the tray's update check, run as a short-lived process (see Updater)
///   --restart         start after "Restart to update": waits for the old copy; Velopack applies the update first
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--check-updates"])
        {
            // The tray's update check, in its own short-lived process (see Updater). It mustn't apply an update it
            // downloaded earlier: the tray is still running.
            VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
            return Updater.RunCheck();
        }

        // Also applies a downloaded update and restarts (Velopack's auto-apply on startup).
        VelopackApp.Build()
            .OnFirstRun(_ => Installation.FirstRun())
            .OnBeforeUninstallFastCallback(_ => Installation.BeforeUninstall())
            .Run();

        if (args is ["--setup"])
        {
            // The setup window on its own: for checking it, or setting up again by hand.
            Process.Start(Path.Combine(AppContext.BaseDirectory, "HapticLink.Setup.exe"))?.Dispose();
            return 0;
        }

        // One copy at a time. "--restart" (Restart to update) waits a little for the copy that started it to exit.
        using var single = new Mutex(false, @"Local\HapticLink.App");
        try { if (!single.WaitOne(args is ["--restart"] ? TimeSpan.FromSeconds(10) : TimeSpan.Zero)) return 0; }
        catch (AbandonedMutexException) { /* the previous copy exited without letting go: ours now */ }

        AppLog.CatchUnhandled();
        AppLog.Write($"HapticLink {AppLog.Version} starting from {Environment.ProcessPath}");
        return new Tray().Run();
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
