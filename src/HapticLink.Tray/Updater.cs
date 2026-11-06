using Velopack;
using Velopack.Sources;

namespace HapticLink.App;

/// <summary>
/// Updates from GitHub Releases, like PeripheralHub: only in a copy installed with Setup; checks at startup and daily,
/// downloads in the background, installs at the next start or straight away with "Restart to update". Changed fires on
/// a pool thread.
/// </summary>
internal sealed class Updater
{
    public const string Repo = "https://github.com/hemanthvj12/haptic-link";
    private readonly UpdateManager? _manager;
    private Timer? _timer;
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
    }

    public void Start()
    {
        if (!IsInstalled) return;
        _timer = new Timer(_ => _ = CheckAsync(manual: false), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
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
