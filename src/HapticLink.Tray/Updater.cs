using System.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace HapticLink.App;

/// <summary>
/// Updates from GitHub Releases, like PeripheralHub: only in a copy installed with Setup; checks at startup and daily,
/// downloads in the background, installs at the next start or straight away with "Restart to update".
/// The check runs in a short-lived copy of this exe (<c>HapticLink.exe --check-updates</c>, <see cref="RunCheck"/>):
/// once loaded, Windows' HTTP and TLS code stayed in the tray and took it from ~5 to ~10–11 MB for the rest of the day.
/// Changed fires on a pool thread.
/// </summary>
internal sealed class Updater
{
    public const string Repo = "https://github.com/hemanthvj12/haptic-link";
    private const int Downloaded = 10, NotInstalled = 3, Failed = 1;
    private Timer? _timer;

    public event Action? Changed;

    /// <summary>A copy installed with Setup: Velopack's <c>Update.exe</c> one folder up and its <c>sq.version</c> here.</summary>
    public bool IsInstalled { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "sq.version")) && File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));
    public bool Checking { get; private set; }
    public string? ReadyVersion { get; private set; }
    public string? LastResult { get; private set; }

    public void Start()
    {
        if (!IsInstalled) return;
        _timer = new Timer(_ => _ = CheckAsync(manual: false), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
        _ = CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (!IsInstalled || Checking) return;
        if (ReadyVersion is not null) { if (manual) { LastResult = $"Version {ReadyVersion} is ready"; Changed?.Invoke(); } return; }
        Checking = true;
        Changed?.Invoke();
        try
        {
            using var check = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--check-updates")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            var output = await check.StandardOutput.ReadToEndAsync();
            await check.WaitForExitAsync();
            if (check.ExitCode == Downloaded && output.Trim() is { Length: > 0 } version) { ReadyVersion = version; LastResult = null; }
            else if (check.ExitCode == 0) { if (manual) LastResult = "Up to date"; }
            else if (manual) LastResult = "Couldn't check for updates"; // the check logged why
        }
        catch (Exception ex)
        {
            if (manual) LastResult = "Couldn't check for updates";
            AppLog.Write($"Update check couldn't start: {ex.Message}");
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// <c>HapticLink.exe --check-updates</c>: checks GitHub and downloads a newer version (applied at the next start).
    /// Prints the ready version and exits with 10, or 0 when up to date, 3 when not installed, 1 on failure.
    /// </summary>
    public static int RunCheck()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(Repo, accessToken: null, prerelease: false));
            if (!manager.IsInstalled) return NotInstalled;
            if (manager.UpdatePendingRestart is { } pending) { Console.Out.Write(pending.Version.ToString()); return Downloaded; }
            var update = manager.CheckForUpdatesAsync().GetAwaiter().GetResult();
            if (update is null) return 0;
            AppLog.Write($"Update {update.TargetFullRelease.Version} found, downloading");
            manager.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
            AppLog.Write($"Update {update.TargetFullRelease.Version} downloaded; installs at the next start");
            Console.Out.Write(update.TargetFullRelease.Version.ToString());
            Console.Out.Flush();
            return Downloaded;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Update check failed: {ex.Message}");
            return Failed;
        }
    }
}
