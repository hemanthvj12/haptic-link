using System.Diagnostics;
using System.Runtime.InteropServices;
using static HapticLink.App.Native;

namespace HapticLink.App;

/// <summary>
/// The tray icon and its menu. The icon is coloured while the bridge runs and grey otherwise; hovering shows the status.
/// The work happens in the service, but only while this app runs (it holds an ATTACH connection): Exit turns HapticLink
/// off until the app starts again, and the switch is remembered (owner, 2026-11-02).
/// All state lives on the window's thread; pipe calls run on the thread pool and post their result back as a message.
/// </summary>
internal sealed unsafe class Tray
{
    private const uint WM_TRAY = WM_APP + 1, WM_POLLED = WM_APP + 2, WM_DONE = WM_APP + 3, WM_SETUP_CLOSED = WM_APP + 4,
        WM_REFRESH = WM_APP + 5, WM_CHECKED = WM_APP + 6;
    private const int IdStatus = 1, IdSetup = 2, IdToggle = 3, IdSpeaker = 4, IdStartup = 5, IdUpdate = 6, IdLog = 7,
        IdVersion = 8, IdExit = 9;

    private static Tray? _instance;
    private IntPtr _window, _activeIcon, _idleIcon, _shownIcon;
    private uint _taskbarCreated;
    private string _shownTip = "";
    private readonly Updater _updater = new();
    private readonly IDisposable _attach = ServiceClient.HoldAttached();
    private ServiceStatus? _last;
    private bool? _speakerOn;
    private bool _busy, _setupNeeded, _neededSetup, _reportedDoubleInput, _polling, _pollAgain, _exitIfNotRunning, _setupChecked;
    private volatile bool _setupNeededResult;
    private volatile PollResult? _polled;
    private DateTime? _onSince;
    private int _polls;
    private Process? _setup;

    private sealed record PollResult(ServiceStatus? Status, bool? SpeakerOn);

    public int Run()
    {
        _instance = this;
        AllowDarkMenus();
        var module = GetModuleHandleW(IntPtr.Zero);
        fixed (char* name = "HapticLinkTray")
        {
            var wc = new WindowClass
            {
                Size = (uint)sizeof(WindowClass),
                WndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                Instance = module,
                ClassName = (IntPtr)name,
            };
            RegisterClassExW(&wc);
            // A hidden top-level window (not message-only: those miss Explorer's "TaskbarCreated" broadcast).
            _window = CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        }
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        int size = GetSystemMetrics(SM_CXSMICON);
        _activeIcon = LoadIcon("tray-on.ico", size);
        _idleIcon = LoadIcon("tray-off.ico", size);
        _shownIcon = _idleIcon;
        _shownTip = "HapticLink";
        UpdateIcon(NIM_ADD);

        SetTimer(_window, 1, 2000, IntPtr.Zero);
        _updater.Changed += () => PostMessageW(_window, WM_REFRESH, 0, 0);
        _updater.Start();
        // First start (or after an update that changed the service): the setup window.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            _setupNeededResult = ServiceSetup.Needed();
            PostMessageW(_window, WM_CHECKED, 0, 0);
        });

        Msg msg;
        while (GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_instance is { } tray && tray._window == window && tray.Handle(msg, lParam)) return 0;
        }
        catch (Exception ex) { AppLog.Write($"unhandled: {ex}"); }
        return DefWindowProcW(window, msg, wParam, lParam);
    }

    private bool Handle(uint msg, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAY:
                // A left click opens the same menu as a right click.
                if ((uint)lParam is WM_LBUTTONUP or WM_RBUTTONUP) ShowMenu();
                return true;
            case WM_TIMER: Poll(); return true;
            case WM_POLLED: Polled(); return true;
            case WM_DONE: _busy = false; Poll(); return true;
            case WM_REFRESH: Refresh(); return true;
            case WM_SETUP_CLOSED: SetupClosed(); return true;
            case WM_CHECKED:
                _setupNeeded = _setupNeededResult;
                _setupChecked = true;
                Refresh();
                if (_setupNeeded) ShowSetup();
                Poll();
                return true;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            UpdateIcon(NIM_ADD); // Explorer restarted: the icon has to be added again.
            return true;
        }
        return false;
    }

    private void Poll()
    {
        if (_polling) { _pollAgain = true; return; }
        _polling = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var status = ServiceClient.Status();
            _polled = new PollResult(status, status is null ? null : ServiceClient.SpeakerOn());
            PostMessageW(_window, WM_POLLED, 0, 0);
        });
    }

    private void Polled()
    {
        _polling = false;
        if (_polled is { } result) (_last, _speakerOn) = (result.Status, result.SpeakerOn);
        Refresh();

        // Each poll leaves a few KB of garbage, and the GC's first budget is sized from the CPU cache (megabytes), so the
        // tray grew ~0.35 MB a minute before a collection came. A collection a minute keeps it flat (~5 MB); the heap is
        // tiny, so it takes well under a millisecond. (DOTNET_GCgen0size works too, but only as an environment variable:
        // Native AOT doesn't read it from the exe's runtime settings.)
        if (++_polls % 30 == 0) GC.Collect();

        if (_exitIfNotRunning)
        {
            _exitIfNotRunning = false;
            if (_last is null)
            {
                AppLog.Write("setup exited before HapticLink was set up; exiting");
                Exit();
                return;
            }
        }

        // A requirement went missing (HidHide or Steam's speaker driver removed): the service stays off, and the setup
        // window says what's needed. Once each time it happens, so closing the window doesn't bring it straight back.
        bool needsSetup = _last?.Message.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal) == true;
        if (needsSetup && !_neededSetup && !_setupNeeded && _setupChecked)
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
            if (!_reportedDoubleInput && DateTime.UtcNow - _onSince > TimeSpan.FromSeconds(5) && DoubleInput.RealPadVisible())
            {
                _reportedDoubleInput = true;
                AppLog.Write("the real controller is in Windows' game controller list while HapticLink is on: double input");
                ThreadPool.QueueUserWorkItem(_ => ServiceClient.ReportDoubleInput());
            }
        }
        else
        {
            _onSince = null;
            _reportedDoubleInput = false;
        }

        if (_pollAgain) { _pollAgain = false; Poll(); }
    }

    /// <summary>What the service says is missing ("HidHide isn't installed"), or null when nothing is.</summary>
    private string? WhyOff() =>
        _last?.Message is { } m && m.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal)
            ? m[ServiceClient.NeedsSetup.Length..].Split(". Open HapticLink's setup", 2)[0]
            : null;

    /// <summary>
    /// Opens the setup window (HapticLink.Setup.exe) or brings it forward. Exiting it while the service isn't running at
    /// all exits the app too: nothing works without setup, and the window comes back at the next start. With a running
    /// (older) service the app stays, and the menu offers setup again.
    /// </summary>
    /// <param name="missing">Why the service won't turn on (a requirement it found missing), shown in the window.</param>
    private void ShowSetup(string? missing = null)
    {
        if (_setup is { HasExited: false })
        {
            _setup.Refresh();
            var w = _setup.MainWindowHandle;
            if (w != IntPtr.Zero)
            {
                if (IsIconic(w)) ShowWindow(w, SW_RESTORE);
                SetForegroundWindow(w);
            }
            return;
        }
        var exe = Path.Combine(AppContext.BaseDirectory, "HapticLink.Setup.exe");
        if (!File.Exists(exe)) { AppLog.Write($"setup window: {exe} is missing"); return; }
        var start = new ProcessStartInfo(exe);
        if (_setupNeeded) start.ArgumentList.Add("--required");
        else if (missing is not null) { start.ArgumentList.Add("--why"); start.ArgumentList.Add(missing); }
        try
        {
            AllowSetForegroundWindow(uint.MaxValue); // let the window come to the front
            _setup = Process.Start(start)!;
            _setup.EnableRaisingEvents = true;
            _setup.Exited += (_, _) => PostMessageW(_window, WM_SETUP_CLOSED, 0, 0);
        }
        catch (Exception ex) { AppLog.Write($"setup window: couldn't start ({ex.Message})"); _setup = null; }
    }

    private void SetupClosed()
    {
        if (_setup is not { HasExited: true } setup) return;
        bool completed = setup.ExitCode == 0;
        setup.Dispose();
        _setup = null;
        if (completed) _setupNeeded = false;
        else _exitIfNotRunning = true;
        Poll();
    }

    /// <summary>Runs a pipe call on the pool; the menu's switches are greyed out until it's done.</summary>
    private void Command(Action action, string log)
    {
        if (_busy) return;
        _busy = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            action();
            AppLog.Write(log);
            PostMessageW(_window, WM_DONE, 0, 0);
        });
    }

    private void ShowMenu()
    {
        var s = _last;
        bool running = s is not null;
        var menu = CreatePopupMenu();
        void Item(int id, string text, bool enabled = true, bool check = false) =>
            AppendMenuW(menu, MF_STRING | (enabled ? 0 : MF_GRAYED) | (check ? MF_CHECKED : 0), (nuint)id, text.Replace("&", "&&"));
        void Separator() => AppendMenuW(menu, MF_SEPARATOR, 0, null);

        Item(IdStatus, !running ? "Not set up yet: the HapticLink service isn't running." : s!.Message, enabled: false);
        // Shown while something is missing: setup not done, or the service reports HidHide or Steam missing.
        if (_setupNeeded || !running
            || s!.Message.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal)
            || s.Message.Contains("HidHide isn't installed", StringComparison.Ordinal)
            || s.Message.Contains("Steam isn't installed", StringComparison.Ordinal))
            Item(IdSetup, _setupNeeded || !running ? "Finish setting up HapticLink…" : "Set up HapticLink…");
        Separator();
        Item(IdToggle, "Full DualSense features", enabled: running && !_busy, check: s?.Enabled == true);
        // The speaker switch, indented under the main one; hidden for a service too old to have it.
        if (_speakerOn is not null) Item(IdSpeaker, "    Controller speaker", enabled: running && !_busy, check: _speakerOn == true);
        Separator();
        Item(IdStartup, "Start with Windows", check: Startup.IsEnabled);
        if (_updater.IsInstalled)
            Item(IdUpdate, _updater.ReadyVersion is { } ready ? $"Restart to update to {ready}"
                : _updater.Checking ? "Checking for updates…"
                : _updater.LastResult is { } result ? $"Check for updates ({result})" : "Check for updates",
                enabled: !_updater.Checking);
        Item(IdLog, "Open log folder");
        Separator();
        Item(IdVersion, $"HapticLink {AppLog.Version}", enabled: false);
        Item(IdExit, "Exit (turns HapticLink off)");

        // The usual dance for tray menus: foreground first so the menu closes when clicking elsewhere, WM_NULL after.
        SetForegroundWindow(_window);
        Point pt;
        GetCursorPos(&pt);
        int id = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _window, IntPtr.Zero);
        PostMessageW(_window, WM_NULL, 0, 0);
        DestroyMenu(menu);
        Execute(id);
    }

    private void Execute(int id)
    {
        switch (id)
        {
            case IdSetup: ShowSetup(WhyOff()); break;
            case IdToggle:
                if (_last is null) break;
                bool on = !_last.Enabled;
                Command(() => ServiceClient.SetEnabled(on), $"Full DualSense features: {(on ? "on" : "off")}");
                break;
            case IdSpeaker:
                bool speaker = !(_speakerOn ?? true);
                Command(() => ServiceClient.SetFeature("speaker", speaker), $"controller speaker: {(speaker ? "on" : "off")}");
                break;
            case IdStartup:
                try { Startup.Set(!Startup.IsEnabled); } catch (Exception ex) { AppLog.Write($"startup: {ex.Message}"); }
                break;
            case IdUpdate:
                if (_updater.ReadyVersion is not null)
                {
                    // Exit, and start again: Velopack applies the downloaded update as the new copy starts, then
                    // restarts it. "--restart" makes the new copy wait for this one to let go of the single-copy lock.
                    AppLog.Write($"restarting to update to {_updater.ReadyVersion}");
                    Exit();
                    try { Process.Start(Environment.ProcessPath!, "--restart")?.Dispose(); }
                    catch (Exception ex) { AppLog.Write($"restart to update: {ex.Message}"); }
                }
                else _ = _updater.CheckAsync(manual: true);
                break;
            case IdLog:
                try
                {
                    Directory.CreateDirectory(AppLog.Folder);
                    Process.Start(new ProcessStartInfo(AppLog.Folder) { UseShellExecute = true });
                }
                catch (Exception ex) { AppLog.Write($"open log folder: {ex.Message}"); }
                break;
            case IdExit: Exit(); break;
        }
    }

    private void Refresh()
    {
        var s = _last;
        var tip = s is null ? "HapticLink: not set up" : s.State switch
        {
            "on" => s.Haptics ? "HapticLink: on" : "HapticLink: on (no haptics)",
            "waiting" => "HapticLink: waiting for a DualSense",
            "off" => "HapticLink: off",
            "error" when s.Message.StartsWith(ServiceClient.NeedsSetup + ServiceClient.HidHideBroken, StringComparison.Ordinal) => "HapticLink: off (double input, needs repair)",
            "error" when s.Message.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal) => "HapticLink: off (needs setup)",
            _ => "HapticLink: " + s.State,
        };
        var icon = s?.State == "on" ? _activeIcon : _idleIcon;
        if (tip == _shownTip && icon == _shownIcon) return;
        _shownTip = tip;
        _shownIcon = icon;
        UpdateIcon(NIM_MODIFY);
    }

    private void UpdateIcon(uint message)
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _window,
            Id = 1,
            Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            CallbackMessage = WM_TRAY,
            Icon = _shownIcon,
        };
        Copy(_shownTip, data.Tip, 128);
        Shell_NotifyIconW(message, &data);
    }

    private void Exit()
    {
        KillTimer(_window, 1);
        _attach.Dispose();
        UpdateIcon(NIM_DELETE);
        DestroyIcon(_activeIcon);
        DestroyIcon(_idleIcon);
        PostQuitMessage(0);
    }
}
