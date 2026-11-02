using System.Diagnostics;

namespace HapticLink.App;

/// <summary>
/// The tray icon and its menu. The icon is coloured while the bridge runs and grey otherwise; hovering shows the status.
/// The work happens in the service, but only while this app runs (it holds an ATTACH connection): Exit turns HapticLink
/// off until the app starts again, and the switch is remembered (owner, 2026-11-02).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Icon _activeIcon, _idleIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _setup = new("Set up HapticLink…")
    {
        ToolTipText = "Checks for HidHide and Steam, which HapticLink needs, and installs HapticLink's driver and service.",
    };
    private SetupWindow? _setupWindow;
    private readonly ToolStripMenuItem _toggle = new("Full DualSense features");
    private readonly ToolStripMenuItem _speaker = new("Controller speaker")
    {
        ToolTipText = "Game sounds meant for the controller play from it (or headphones plugged into it). If haptics stutter while it plays, try turning it off.",
    };
    private bool? _speakerOn;
    private readonly ToolStripMenuItem _startup = new("Start with Windows");
    private readonly ToolStripMenuItem _update = new("Check for updates");
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private readonly Updater _updater = new();
    private readonly SynchronizationContext _ui;
    private ServiceStatus? _last;
    private bool _busy, _setupNeeded, _neededSetup, _reportedDoubleInput;
    private DateTime? _onSince;
    private readonly IDisposable _attach = ServiceClient.HoldAttached();

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        int small = SystemInformation.SmallIconSize.Width;
        _activeIcon = IconMaker.Create(small, active: true);
        _idleIcon = IconMaker.Create(small, active: false);

        _setup.Click += (_, _) => ShowSetup(WhyOff());
        _toggle.Click += async (_, _) => await ToggleAsync();
        _speaker.Click += async (_, _) => await SetFeatureAsync("speaker", !(_speakerOn ?? true));
        _startup.Click += (_, _) => { try { Startup.Set(!Startup.IsEnabled); } catch (Exception ex) { AppLog.Write($"startup: {ex.Message}"); } Refresh(); };
        _update.Click += async (_, _) =>
        {
            if (_updater.ReadyVersion is not null) _updater.ApplyAndRestart();
            else await _updater.CheckAsync(manual: true);
        };
        var log = new ToolStripMenuItem("Open log folder");
        log.Click += (_, _) => { Directory.CreateDirectory(AppLog.Folder); Process.Start(new ProcessStartInfo(AppLog.Folder) { UseShellExecute = true }); };
        var exit = new ToolStripMenuItem("Exit (turns HapticLink off)") { ToolTipText = "Games get the controller back as it is. Starting HapticLink again turns it back on." };
        exit.Click += (_, _) => ExitThread();

        _menu.Items.AddRange([_status, _setup, new ToolStripSeparator(), _toggle, _speaker, new ToolStripSeparator(), _startup, _update, log,
            new ToolStripSeparator(), new ToolStripMenuItem($"HapticLink {AppLog.Version}") { Enabled = false }, exit]);
        _menu.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon { Icon = _idleIcon, Text = "HapticLink", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            // A left click opens the same menu (NotifyIcon only does it for the right button by itself).
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_icon, null);
        };

        _updater.Changed += () => _ui.Post(_ => Refresh(), null);
        _poll.Tick += (_, _) => _ = PollAsync();
        _poll.Start();
        _updater.Start();
        _ = StartupCheckAsync();
    }

    /// <summary>First start (or after an update that changed the service): the setup window.</summary>
    private async Task StartupCheckAsync()
    {
        _setupNeeded = await Task.Run(ServiceSetup.Needed);
        Refresh();
        if (_setupNeeded) ShowSetup();
        await PollAsync();
    }

    /// <summary>
    /// Opens the setup window (or brings it forward). Exiting it while the service isn't running at all exits the app
    /// too: nothing works without setup, and the window comes back at the next start. With a running (older) service the
    /// app stays, and the menu offers setup again.
    /// </summary>
    /// <param name="missing">Why the service won't turn on (a requirement it found missing), shown in the window.</param>
    private void ShowSetup(string? missing = null)
    {
        if (_setupWindow is { IsDisposed: false })
        {
            _setupWindow.Activate();
            return;
        }
        _setupWindow = new SetupWindow(required: _setupNeeded, missing);
        _setupWindow.FormClosed += async (_, _) =>
        {
            bool completed = _setupWindow.Completed;
            _setupWindow = null;
            if (completed) _setupNeeded = false;
            await PollAsync();
            if (!completed && _last is null)
            {
                AppLog.Write("setup exited before HapticLink was set up; exiting");
                ExitThread();
            }
        };
        _setupWindow.Show();
        _setupWindow.Activate();
    }

    private async Task ToggleAsync()
    {
        if (_busy || _last is null) return;
        _busy = true;
        bool on = !_last.Enabled;
        await Task.Run(() => ServiceClient.SetEnabled(on));
        AppLog.Write($"Full DualSense features: {(on ? "on" : "off")}");
        _busy = false;
        await PollAsync();
    }

    private async Task SetFeatureAsync(string name, bool on)
    {
        if (_busy) return;
        _busy = true;
        await Task.Run(() => ServiceClient.SetFeature(name, on));
        AppLog.Write($"controller {name}: {(on ? "on" : "off")}");
        _busy = false;
        await PollAsync();
    }

    private async Task PollAsync()
    {
        _last = await Task.Run(ServiceClient.Status);
        _speakerOn = _last is null ? null : await Task.Run(ServiceClient.SpeakerOn);
        Refresh();
        // A requirement went missing (HidHide or Steam's speaker driver removed): the service stays off, and the setup
        // window says what's needed. Once each time it happens, so closing the window doesn't bring it straight back.
        bool needsSetup = _last?.Message.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal) == true;
        if (needsSetup && !_neededSetup && !_setupNeeded)
        {
            AppLog.Write($"service: {_last!.Message}");
            ShowSetup(WhyOff());
        }
        _neededSetup = needsSetup;

        // Double input: while HapticLink is on, the real controller mustn't be in Windows' game controller list. A few
        // seconds' grace after it turns on (the controller restarts while being hidden), then once per "on" stretch.
        if (_last?.State == "on")
        {
            _onSince ??= DateTime.UtcNow;
            if (!_reportedDoubleInput && DateTime.UtcNow - _onSince > TimeSpan.FromSeconds(5) && await Task.Run(DoubleInput.RealPadVisible))
            {
                _reportedDoubleInput = true;
                AppLog.Write("the real controller is in Windows' game controller list while HapticLink is on: double input");
                await Task.Run(ServiceClient.ReportDoubleInput);
            }
        }
        else
        {
            _onSince = null;
            _reportedDoubleInput = false;
        }
    }

    /// <summary>What the service says is missing ("HidHide isn't installed"), or null when nothing is.</summary>
    private string? WhyOff() =>
        _last?.Message is { } m && m.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal)
            ? m[ServiceClient.NeedsSetup.Length..].Split(". Open HapticLink's setup", 2)[0]
            : null;

    private void Refresh()
    {
        var s = _last;
        bool running = s is not null;
        // Shown while something is missing: setup not done, or the service reports HidHide or Steam missing.
        _setup.Visible = _setupNeeded || !running
            || s!.Message.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal)
            || s.Message.Contains("HidHide isn't installed", StringComparison.Ordinal)
            || s.Message.Contains("Steam isn't installed", StringComparison.Ordinal);
        _setup.Text = _setupNeeded || !running ? "Finish setting up HapticLink…" : "Set up HapticLink…";
        _toggle.Enabled = running && !_busy;
        _toggle.Checked = s?.Enabled == true;
        // The speaker switch, indented under the main one; hidden for a service too old to have it.
        _speaker.Visible = _speakerOn is not null;
        _speaker.Enabled = running && !_busy;
        _speaker.Checked = _speakerOn == true;
        _speaker.Padding = new Padding(16, 0, 0, 0);
        _startup.Checked = Startup.IsEnabled;
        _update.Visible = _updater.IsInstalled;
        _update.Enabled = !_updater.Checking;
        _update.Text = _updater.ReadyVersion is { } ready ? $"Restart to update to {ready}"
            : _updater.Checking ? "Checking for updates…"
            : _updater.LastResult is { } result ? $"Check for updates ({result})" : "Check for updates";

        _status.Text = !running ? "Not set up yet: the HapticLink service isn't running." : s!.Message;
        _icon.Icon = s?.State == "on" ? _activeIcon : _idleIcon;
        var tip = !running ? "HapticLink: not set up" : s!.State switch
        {
            "on" => s.Haptics ? "HapticLink: on" : "HapticLink: on (no haptics)",
            "waiting" => "HapticLink: waiting for a DualSense",
            "off" => "HapticLink: off",
            "error" when s.Message.StartsWith(ServiceClient.NeedsSetup + ServiceClient.HidHideBroken, StringComparison.Ordinal) => "HapticLink: off (double input, needs repair)",
            "error" when s.Message.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal) => "HapticLink: off (needs setup)",
            _ => "HapticLink: " + s.State,
        };
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    protected override void ExitThreadCore()
    {
        _poll.Stop();
        _attach.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
