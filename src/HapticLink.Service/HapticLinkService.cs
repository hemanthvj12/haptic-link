using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HapticLink.Service;

/// <summary>
/// The service's work. While "enabled", it looks for a DualSense connected over Bluetooth about once a second and bridges
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
    private DateTime _retryAt = DateTime.MinValue;

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
        while (!_cts.IsCancellationRequested)
        {
            try { Step(); }
            catch (Exception ex) { Log.Write($"loop: {ex}"); }
            _wake.WaitOne(1000);
        }
        lock (_gate) StopBridge("service stopping");
    }

    private void Step()
    {
        lock (_gate)
        {
            if (!Settings.Enabled)
            {
                StopBridge("turned off");
                Set("off", "Off.");
                return;
            }
            if (_bridge is { Alive: false }) StopBridge("controller disconnected");
            if (_bridge is not null) { _bridge.ApplyFeatures(Settings.Speaker); ShowOn(_bridge); return; }

            var paths = PadConnection.FindBluetoothPads();
            if (paths.Count == 0) { Set("waiting", "Waiting for a DualSense connected over Bluetooth."); return; }
            if (DateTime.UtcNow < _retryAt) return;

            var pad = PadConnection.TryOpen(paths[0]);
            if (pad is null) { Fail("couldn't open the controller (another app may be using or hiding it)"); return; }
            try
            {
                var allowed = new List<string> { Environment.ProcessPath! };
                allowed.AddRange(_readers);
                _bridge = Bridge.Start(pad, _speaker, allowed);
                _bridge.ApplyFeatures(Settings.Speaker);
                ShowOn(_bridge);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Fail(ex.Message);
            }
        }
    }

    // Checked every second while on, so the message follows haptics recording as it starts, fails or recovers.
    private void ShowOn(Bridge bridge)
    {
        if (bridge.HasHaptics)
            Set("on", $"On: games see a wired DualSense (haptics, adaptive triggers, rumble, lights{(Settings.Speaker ? ", speaker" : "")}).");
        else if (!bridge.HasSpeaker) // set up again at the next start: turning HapticLink off and on retries it
            Set("on", $"On without haptics: {_speaker.Problem ?? "Steam's streaming speaker driver isn't installed"} (triggers, rumble and lights work).");
        else if (DateTime.UtcNow - bridge.StartedAt < TimeSpan.FromSeconds(10)) // a first failure often clears in a second
            Set("on", "On: games see a wired DualSense; starting haptics.");
        else
            Set("on", $"On without haptics: {bridge.HapticsProblem ?? "couldn't record the controller's speaker"}. Retrying; triggers, rumble and lights work.");
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
    ///                   state: off, waiting, starting, on, error
    ///   ENABLE / DISABLE → OK
    ///   FEATURES      → OK speaker=&lt;0/1&gt;     (service 1.1.0+; older ones answer ERR; more name=value pairs may follow)
    ///   SET speaker 0|1 → OK                  (the controller speaker switch)
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
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (pipe.IsConnected && !_cts.IsCancellationRequested && reader.ReadLine() is { } line)
                    writer.WriteLine(Handle(line, pipe));
            }
            catch (IOException) { } // the app went away
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
            case "FEATURES":
                return $"OK speaker={(Settings.Speaker ? 1 : 0)}";
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
