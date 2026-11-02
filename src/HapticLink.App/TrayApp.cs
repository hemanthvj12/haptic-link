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
    private readonly ToolStripMenuItem _setup = new("Finish setup (admin prompt)…");
    private readonly ToolStripMenuItem _hidHide = new("Install HidHide (admin prompt)…")
    {
        ToolTipText = "HidHide (free, by Nefarius) hides the real controller, so games only see HapticLink's and don't drive both.",
    };
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
    private bool _busy, _setupNeeded;
    private readonly IDisposable _attach = ServiceClient.HoldAttached();

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        int small = SystemInformation.SmallIconSize.Width;
        _activeIcon = IconMaker.Create(small, active: true);
        _idleIcon = IconMaker.Create(small, active: false);

        _setup.Click += async (_, _) => await RunSetupAsync();
        _hidHide.Click += async (_, _) => await RunSetupAsync(installHidHide: true);
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

        _menu.Items.AddRange([_status, _setup, _hidHide, new ToolStripSeparator(), _toggle, _speaker, new ToolStripSeparator(), _startup, _update, log,
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

    /// <summary>First start (or after an update that changed the service): set up, with one admin prompt.</summary>
    private async Task StartupCheckAsync()
    {
        _setupNeeded = await Task.Run(ServiceSetup.Needed);
        Refresh();
        if (_setupNeeded) await RunSetupAsync();
        await PollAsync();
    }

    /// <param name="installHidHide">
    /// Also install HidHide (the tray item). Otherwise, when HidHide is missing, the user is asked first: it's another
    /// developer's driver, so it's never installed without a yes.
    /// </param>
    private async Task RunSetupAsync(bool installHidHide = false)
    {
        if (_busy) return;
        _busy = true;
        Refresh();
        if (!installHidHide && !ServiceSetup.HidHideInstalled)
            installHidHide = MessageBox.Show(
                "HapticLink needs HidHide (free, by Nefarius) to hide your real controller, so games only see HapticLink's " +
                "controller and don't drive both.\n\nDownload and install HidHide as part of setup?",
                "HapticLink setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        var (ok, error) = await ServiceSetup.RunAsync(installHidHide);
        AppLog.Write($"setup: {(ok ? "done" : error ?? "cancelled")}");
        _setupNeeded = !ok;
        _busy = false;
        if (!ok && error is not null) _status.Text = $"Setup failed: {error}";
        await PollAsync();
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
    }

    private void Refresh()
    {
        var s = _last;
        bool running = s is not null;
        _setup.Visible = _setupNeeded || !running;
        _setup.Enabled = !_busy;
        _hidHide.Visible = running && !_setupNeeded && !ServiceSetup.HidHideInstalled; // "Finish setup" asks about it already
        _hidHide.Enabled = !_busy;
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

        if (!_status.Text?.StartsWith("Setup failed", StringComparison.Ordinal) ?? true)
            _status.Text = _busy && !running ? "Setting up…"
                : !running ? "Not set up yet: the HapticLink service isn't running."
                : s!.Message;
        _icon.Icon = s?.State == "on" ? _activeIcon : _idleIcon;
        var tip = !running ? "HapticLink: not set up" : s!.State switch
        {
            // The service adds a "Warning: … HidHide …" when games can still see the real controller.
            "on" when s.Message.Contains("HidHide", StringComparison.Ordinal) => "HapticLink: on (real controller not hidden)",
            "on" => s.Haptics ? "HapticLink: on" : "HapticLink: on (no haptics)",
            "waiting" => "HapticLink: waiting for a DualSense",
            "off" => "HapticLink: off",
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
