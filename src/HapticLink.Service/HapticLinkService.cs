using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HapticLink.Service;

/// <summary>
/// The service's work. It only works while the HapticLink app (the tray) runs: the app holds a pipe connection open
/// (ATTACH), and with none for a few seconds the bridge stops and the state is "closed" (owner, 2026-11-02: "HapticLink
/// should stop completely when the tray is not running"). While the app runs and the feature is "enabled", it looks for a DualSense connected over Bluetooth about once a second and bridges
/// the first one it finds (<see cref="Bridge"/>); when the pad goes away or the feature is turned off, the bridge stops.
/// Apps talk to it over the control pipe (<see cref="Control"/>).
/// </summary>
internal sealed class HapticLinkService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SpeakerEndpoint _speaker = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    // Apps that read the real pad too (PeripheralHub, for its battery): kept on HidHide's allowed list.
    private readonly HashSet<string> _readers = new(StringComparer.OrdinalIgnoreCase);
    private Thread? _loop, _pipe;
    private Bridge? _bridge;
    private string _state = "starting", _message = "";
    private DateTime _retryAt = DateTime.MinValue, _recheckAt = DateTime.MinValue;
    // The app's held connections (ATTACH). A short grace keeps the bridge through the app restarting (an update).
    private int _attached;
    private DateTime _detachedAt = DateTime.MinValue;
    private static readonly TimeSpan DetachGrace = TimeSpan.FromSeconds(5);

    public void Start()
    {
        Log.Write($"HapticLink service {typeof(HapticLinkService).Assembly.GetName().Version} starting");
        Bridge.UndoLeftovers();
        _speaker.EnsureInBackground();
        _loop = new Thread(Loop) { IsBackground = true, Name = "bridge loop" };
        _pipe = new Thread(ServePipe) { IsBackground = true, Name = "control pipe" };
        _loop.Start();
        _pipe.Start();
    }

    // ---- the bridge loop ---------------------------------------------------------------------

    private void Loop()
    {
        var lastCollect = DateTime.UtcNow;
        while (!_cts.IsCancellationRequested)
        {
            try { Step(); }
            catch (Exception ex) { Log.Write($"loop: {ex}"); }
            // The GC's first budget is sized from the CPU cache (megabytes), so the little garbage the bridge still makes
            // piled up to ~18 MB before a collection came. One every 30 s keeps the service flat; the heap is tiny, so it
            // takes well under a millisecond (no audible effect on the haptics clock).
            if (DateTime.UtcNow - lastCollect > TimeSpan.FromSeconds(30))
            {
                GC.Collect();
                lastCollect = DateTime.UtcNow;
            }
            _wake.WaitOne(1000);
        }
        lock (_gate) StopBridge("service stopping");
    }

    private void Step()
    {
        lock (_gate)
        {
            if (_attached == 0 && DateTime.UtcNow - _detachedAt >= DetachGrace)
            {
                StopBridge("the HapticLink app isn't running");
                Set("closed", "Off while the HapticLink app isn't running.");
                return;
            }
            if (!Settings.Enabled)
            {
                StopBridge("turned off");
                Set("off", "Off.");
                return;
            }
            // HidHide and Steam's speaker driver are requirements (owner, 2026-11-02): with either missing HapticLink stays
            // off and says "Needs setup: …", which makes the app open its setup window.
            if (DateTime.UtcNow >= _recheckAt)
            {
                _speaker.Recheck();
                _recheckAt = DateTime.UtcNow.AddSeconds(5);
            }
            if (_bridge is null && _speaker.Checking) { Set("starting", "Checking HapticLink's setup…"); return; }
            if (MissingRequirement() is { } missing)
            {
                StopBridge($"needs setup: {missing}");
                Set("error", $"{NeedsSetup}{missing}. Open HapticLink's setup to fix it.");
                return;
            }
            // Double input: HidHide stopped hiding the real pad (its settings changed, or Windows' game controller input still
            // has it), so games see two controllers. HapticLink stays off until the setup window's Repair (owner, 2026-11-02:
            // like DSX's "Fix double input", but as part of "Needs setup").
            if (_hidHideBroken is { } broken)
            {
                StopBridge($"double input: {broken}");
                Set("error", $"{NeedsSetup}{HidHideBroken}{broken}. Open HapticLink's setup to fix it.");
                return;
            }
            if (_bridge is { Alive: false }) StopBridge("controller disconnected");
            if (_bridge is not null)
            {
                if (DateTime.UtcNow >= _hidHideCheckAt)
                {
                    _hidHideCheckAt = DateTime.UtcNow.AddSeconds(10);
                    if (HidHideControl.Check(_bridge.InstanceId, Environment.ProcessPath!) is { } changed)
                    {
                        Log.Write($"HidHide check: {changed}");
                        _hidHideBroken = changed;
                        return; // the next step stops the bridge and says so
                    }
                }
                _bridge.ApplyFeatures(Settings.Speaker);
                ShowOn(_bridge);
                return;
            }

            var paths = PadConnection.FindBluetoothPads();
            if (paths.Count == 0) { Set("waiting", "Waiting for a DualSense connected over Bluetooth."); return; }
            if (DateTime.UtcNow < _retryAt) return;

            try
            {
                var allowed = new List<string> { Environment.ProcessPath! };
                allowed.AddRange(_readers);
                _bridge = Bridge.Start(paths[0], _speaker, allowed, _forceRestart);
                _forceRestart = false;
                _hidHideCheckAt = DateTime.UtcNow.AddSeconds(10);
                if (_bridge.HidHideProblem is { } problem)
                {
                    Log.Write($"HidHide: {problem}");
                    _hidHideBroken = problem;
                    StopBridge("HidHide couldn't hide the controller");
                    Set("error", $"{NeedsSetup}{HidHideBroken}{problem}. Open HapticLink's setup to fix it.");
                    return;
                }
                _bridge.ApplyFeatures(Settings.Speaker);
                ShowOn(_bridge);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Fail(ex.Message);
            }
        }
    }

    /// <summary>STATUS messages starting with this mean a requirement is missing; the app opens its setup window on it.</summary>
    public const string NeedsSetup = "Needs setup: ";
    /// <summary>After <see cref="NeedsSetup"/>: double input, which the setup window offers to repair.</summary>
    public const string HidHideBroken = "HidHide isn't hiding the controller: ";

    // Set when the real pad isn't hidden any more (HidHideControl.Check, a failed hide, or the app seeing it in Windows'
    // game controller list); cleared by REPAIR, which also restarts the pad.
    private string? _hidHideBroken;
    private bool _forceRestart;
    private DateTime _hidHideCheckAt = DateTime.MinValue;

    private string? MissingRequirement() =>
        HidHide.FindCli() is null ? "HidHide isn't installed"
        : _speaker.MissingDriver is { } driver ? driver
        : null;

    // Checked every second while on, so the message follows haptics recording as it starts, fails or recovers.
    private void ShowOn(Bridge bridge)
    {
        string message;
        if (bridge.HasHaptics)
            message = $"On: games see a wired DualSense (haptics, adaptive triggers, rumble, lights{(Settings.Speaker ? ", speaker" : "")}).";
        else if (!bridge.HasSpeaker) // set up again at the next start: turning HapticLink off and on retries it
            message = $"On without haptics: {_speaker.Problem ?? "Steam's streaming speaker driver isn't installed"} (triggers, rumble and lights work).";
        else if (DateTime.UtcNow - bridge.StartedAt < TimeSpan.FromSeconds(10)) // a first failure often clears in a second
            message = "On: games see a wired DualSense; starting haptics.";
        else
            message = $"On without haptics: {bridge.HapticsProblem ?? "couldn't record the controller's speaker"}. Retrying; triggers, rumble and lights work.";
        Set("on", message);
    }

    private void Fail(string message)
    {
        _retryAt = DateTime.UtcNow.AddSeconds(15);
        Set("error", $"Couldn't start: {message}. Trying again shortly.");
    }

    private void Set(string state, string message)
    {
        if (state == _state && message == _message) return;
        _state = state;
        _message = message;
        Log.Write($"state: {state} ({message})");
    }

    private void StopBridge(string why)
    {
        if (_bridge is null) return;
        Log.Write($"stopping the bridge: {why}");
        _bridge.Dispose();
        _bridge = null;
    }

    // ---- the control pipe --------------------------------------------------------------------

    /// <summary>
    /// The control pipe "HapticLink": one line per command, answered with "OK …" or "ERR message". Any signed-in user may
    /// use it (turning the feature on or off is all it can do).
    ///   HELLO &lt;app&gt;  → OK &lt;service version&gt; &lt;protocol&gt;    ("HELLO PeripheralHub" also keeps that app on HidHide's
    ///                                                         allowed list, so it still sees the pad it shows)
    ///   STATUS        → OK &lt;state&gt;|&lt;enabled 0/1&gt;|&lt;haptics 0/1&gt;|&lt;controller MAC or empty&gt;|&lt;message&gt;
    ///                   state: closed (the HapticLink app isn't running, so nothing runs), off, waiting, starting, on, error
    ///   ATTACH        → OK; the HapticLink app keeps this connection open while it runs. The bridge only runs while
    ///                   one is open (or for a few seconds after the last one closes).
    ///   ENABLE / DISABLE → OK
    ///   FEATURES      → OK speaker=&lt;0/1&gt;     (service 1.1.0+; older ones answer ERR; more name=value pairs may follow)
    ///   SET speaker 0|1 → OK                  (the controller speaker switch)
    ///   RECHECK       → OK; looks for HidHide and Steam's speaker driver again now (service 1.2.3+). While either is
    ///                   missing STATUS is "error" with a message starting "Needs setup: " and nothing runs.
    ///   DOUBLE-INPUT  → OK; the app saw the real pad in Windows' game controller list while bridging (1.2.3+)
    ///   REPAIR        → OK; hides the real pad again and restarts it (1.2.3+). While HidHide isn't hiding it, STATUS
    ///                   is "error" with "Needs setup: HidHide isn't hiding the controller: …".
    /// New commands are added without changing the protocol number; STATUS keeps its five fields.
    /// </summary>
    public static class Control
    {
        public const string PipeName = "HapticLink";
        public const int Protocol = 1;
    }

    private void ServePipe()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => Serve(connected));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write($"pipe: {ex.Message}"); Thread.Sleep(500); }
            finally { pipe?.Dispose(); }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Control.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024, security);
    }

    private void Serve(NamedPipeServerStream pipe)
    {
        bool attached = false;
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (pipe.IsConnected && !_cts.IsCancellationRequested && reader.ReadLine() is { } line)
                {
                    if (!attached && line.Trim().Equals("ATTACH", StringComparison.OrdinalIgnoreCase))
                    {
                        attached = true;
                        lock (_gate) if (_attached++ == 0) Log.Write("HapticLink app attached");
                        _wake.Set();
                    }
                    writer.WriteLine(Handle(line, pipe));
                }
            }
            catch (IOException) { } // the app went away
            finally
            {
                if (attached)
                {
                    lock (_gate)
                        if (--_attached == 0) { _detachedAt = DateTime.UtcNow; Log.Write("HapticLink app detached"); }
                    _wake.Set();
                }
            }
        }
    }

    private string Handle(string line, NamedPipeServerStream pipe)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts.FirstOrDefault()?.ToUpperInvariant())
        {
            case "HELLO":
                if (parts.Length > 1 && parts[1].Trim().Equals("PeripheralHub", StringComparison.OrdinalIgnoreCase) && ClientImage(pipe) is { Length: > 0 } exe)
                {
                    bool added;
                    lock (_gate) added = _readers.Add(exe);
                    if (added) Task.Run(() => HidHideControl.Allow(exe)); // it may connect while the pad is already hidden
                }
                return $"OK {typeof(HapticLinkService).Assembly.GetName().Version} {Control.Protocol}";
            case "STATUS":
                lock (_gate)
                    return $"OK {_state}|{(Settings.Enabled ? 1 : 0)}|{(_bridge?.HasHaptics == true ? 1 : 0)}|{_bridge?.Mac ?? ""}|{_message}";
            case "ENABLE":
            case "DISABLE":
                Settings.Enabled = parts[0].Equals("ENABLE", StringComparison.OrdinalIgnoreCase);
                Log.Write($"turned {(Settings.Enabled ? "on" : "off")} by an app");
                _wake.Set();
                return "OK";
            case "ATTACH":
                return "OK";
            case "FEATURES":
                return $"OK speaker={(Settings.Speaker ? 1 : 0)}";
            case "RECHECK": // the setup window's Check again: look for the requirements now, not in up to 15 s
                _speaker.Recheck(now: true);
                lock (_gate) _recheckAt = DateTime.MinValue;
                _wake.Set();
                return "OK";
            case "DOUBLE-INPUT": // the app saw the real pad in Windows' game controller list while bridging
                lock (_gate)
                    if (_bridge is not null && _hidHideBroken is null)
                    {
                        Log.Write("the app sees the real controller in Windows' game controller list: double input");
                        _hidHideBroken = "games can still see your real controller";
                    }
                _wake.Set();
                return "OK";
            case "REPAIR": // the setup window's Repair: hide the real pad again and restart it
                lock (_gate)
                {
                    Log.Write($"repair requested{(_hidHideBroken is null ? "" : $" ({_hidHideBroken})")}");
                    _hidHideBroken = null;
                    _forceRestart = true;
                    _retryAt = DateTime.MinValue;
                    StopBridge("repair");
                }
                _speaker.Recheck(now: true);
                _wake.Set();
                return "OK";
            case "SET":
            {
                var args = parts.Length > 1 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
                if (args.Length != 2 || args[1] is not ("0" or "1")) return "ERR usage: SET speaker 0|1";
                bool on = args[1] == "1";
                switch (args[0].ToLowerInvariant())
                {
                    case "speaker": Settings.Speaker = on; break;
                    default: return "ERR unknown feature";
                }
                Log.Write($"{args[0].ToLowerInvariant()} turned {(on ? "on" : "off")} by an app");
                _wake.Set();
                return "OK";
            }
            default:
                return "ERR unknown command";
        }
    }

    /// <summary>The connected app's exe.</summary>
    private static string ClientImage(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) return "";
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid) return "";
        var buffer = new StringBuilder(1024);
        int size = buffer.Capacity;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString() : "";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        _loop?.Join(5000);
        Log.Write("HapticLink service stopped");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
}

/// <summary>
/// HKLM\SOFTWARE\HapticLink: the switches, shared by every app that turns them. Enabled (on after setup), Speaker (the
/// controller speaker, on unless turned off).
/// </summary>
internal static class Settings
{
    private const string Key = @"SOFTWARE\HapticLink";

    public static bool Enabled { get => Get("Enabled", true); set => Put("Enabled", value); }
    public static bool Speaker { get => Get("Speaker", true); set => Put("Speaker", value); }

    private static bool Get(string name, bool missing)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return key?.GetValue(name) is int v ? v != 0 : missing;
    }

    private static void Put(string name, bool value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key);
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }
}
