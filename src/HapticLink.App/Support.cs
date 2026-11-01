using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace HapticLink.App;

/// <summary>%APPDATA%\HapticLink\logs\app.log (kept under ~1 MB).</summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HapticLink", "logs");
    public static string Version => typeof(AppLog).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void CatchUnhandled()
    {
        Application.ThreadException += (_, e) => Write($"unhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"unhandled: {e.ExceptionObject}");
    }
}

/// <summary>"Start with Windows": the per-user Run key, pointing at this exe.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HapticLink";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && IsThisExe(command);
        }
    }

    private static bool IsThisExe(string command) =>
        string.Equals(command.Trim().Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void RemoveIfThisExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string command && IsThisExe(command)) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// Updates from GitHub Releases, like PeripheralHub: only in a copy installed with Setup; checks at startup and daily,
/// downloads in the background, installs at the next start or straight away with "Restart to update".
/// </summary>
internal sealed class Updater
{
    public const string Repo = "https://github.com/hemanthvj12/haptic-link";
    private readonly UpdateManager? _manager;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)TimeSpan.FromHours(24).TotalMilliseconds };
    private UpdateInfo? _ready;

    public event Action? Changed;
    public bool IsInstalled => _manager is not null;
    public bool Checking { get; private set; }
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();
    public string? LastResult { get; private set; }

    public Updater()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(Repo, accessToken: null, prerelease: false));
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception) { /* not an installed copy */ }
        _timer.Tick += async (_, _) => await CheckAsync(manual: false);
    }

    public void Start()
    {
        if (!IsInstalled) return;
        _timer.Start();
        _ = CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (_manager is null || Checking) return;
        if (_ready is not null) { if (manual) { LastResult = $"Version {ReadyVersion} is ready"; Changed?.Invoke(); } return; }
        Checking = true;
        Changed?.Invoke();
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null) { if (manual) LastResult = "Up to date"; return; }
            AppLog.Write($"Update {update.TargetFullRelease.Version} found, downloading");
            await _manager.DownloadUpdatesAsync(update);
            _ready = update;
            LastResult = null;
            AppLog.Write($"Update {ReadyVersion} downloaded; installs at the next start");
        }
        catch (Exception ex)
        {
            if (manual) LastResult = "Couldn't check for updates";
            AppLog.Write($"Update check failed: {ex.Message}");
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    public void ApplyAndRestart()
    {
        if (_manager is not null && _ready is not null) _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }
}
