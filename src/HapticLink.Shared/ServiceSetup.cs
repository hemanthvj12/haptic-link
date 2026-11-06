using System.ComponentModel;
using System.Diagnostics;

namespace HapticLink.App;

/// <summary>
/// The service, its driver and its speaker device are set up once with one admin prompt: the app ships them in its
/// "service" folder and runs <c>HapticLink.Service.exe setup</c> elevated, which copies them to
/// %ProgramFiles%\HapticLink\Service and installs everything. Setup runs again when the bundled service is newer than the
/// running one (after an update that changed it). <c>remove</c> undoes it when HapticLink is uninstalled.
/// </summary>
public static class ServiceSetup
{
    private static string BundledService => Path.Combine(AppContext.BaseDirectory, "service", "HapticLink.Service.exe");
    private static string InstalledService => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HapticLink", "Service", "HapticLink.Service.exe");

    /// <summary>The bundled service's version (null in a build without the service folder).</summary>
    public static Version? BundledVersion =>
        File.Exists(BundledService) && Version.TryParse(FileVersionInfo.GetVersionInfo(BundledService).FileVersion, out var v) ? v : null;

    /// <summary>Setup is needed: the service isn't running, or it's older than the bundled one.</summary>
    public static bool Needed()
    {
        if (BundledVersion is not { } bundled) return false;
        return ServiceClient.Hello() is not { } running || running < bundled;
    }

    /// <summary>
    /// Runs setup elevated (one admin prompt). <paramref name="onStep"/> gets each step's progress as setup reports it
    /// (step, state run/ok/fail/skip, message), on the calling thread. Returns ok, or an error for the user (null: the
    /// prompt was cancelled).
    /// </summary>
    public static async Task<(bool Ok, string? Error)> RunAsync(Action<string, string, string>? onStep = null)
    {
        if (BundledVersion is null) return (false, "this copy of HapticLink doesn't include the service");
        // Setup appends "step|state|message" lines here; it runs elevated, so its output can't come back any other way.
        var progressFile = Path.Combine(Path.GetTempPath(), $"HapticLink-setup-{Guid.NewGuid():N}.txt");
        int seen = 0;
        void ReadProgress()
        {
            string[] lines;
            try { lines = File.Exists(progressFile) ? File.ReadAllLines(progressFile) : []; }
            catch (IOException) { return; } // being written; next time
            for (; seen < lines.Length; seen++)
                if (lines[seen].Split('|', 3) is [var step, var state, var message]) onStep?.Invoke(step, state, message);
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(BundledService, $"setup --progress \"{progressFile}\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            while (!p.HasExited)
            {
                ReadProgress();
                await Task.Delay(150);
            }
            ReadProgress();
            AppLog.Write($"setup exited with {p.ExitCode}");
            if (p.ExitCode != 0) return (false, $"setup failed (code {p.ExitCode}); details in %ProgramData%\\HapticLink\\service.log");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (false, null); }
        catch (Win32Exception ex) { return (false, ex.Message); }
        finally { try { File.Delete(progressFile); } catch (IOException) { } }
        for (int i = 0; i < 20; i++)
        {
            if (await Task.Run(() => ServiceClient.Hello()) is not null) return (true, null);
            await Task.Delay(500);
        }
        return (false, "the service didn't start");
    }

    /// <summary>Uninstall: removes the service, driver, speaker device and certificate (one admin prompt).</summary>
    public static void RemoveIfInstalled(TimeSpan wait)
    {
        if (!File.Exists(InstalledService)) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(InstalledService, "remove") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit(wait);
        }
        catch (Win32Exception) { /* declined: it all stays, harmlessly */ }
    }
}
