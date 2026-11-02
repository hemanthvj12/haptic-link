using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HapticLink.Service;

/// <summary>
/// One bridged controller: the virtual wired DualSense games see (driver + software device + shared memory), the real
/// pad hidden from games with HidHide, its input fed in, games' output reports relayed back over Bluetooth, and the
/// <see cref="HapticsRelay"/> on the virtual pad's speaker. Everything is undone on Dispose; what HidHide must undo is
/// written to a file first, so a crash is cleaned up at the next service start.
/// </summary>
internal sealed class Bridge : IDisposable
{
    private const string Enumerator = "VID_054C&PID_0CE6&MI_03"; // Steam and SDL match a pad's views by this token
    private const string Instance = "HLINK_PAD_0";

    private PadConnection _pad = null!; // set in Start before the bridge is handed out
    private readonly byte[] _usbInput = new byte[DualSenseReports.UsbInputLength];
    private readonly CancellationTokenSource _cts = new();
    private SafeFileHandle? _sectionHandle;
    private volatile VirtualPadChannel? _channel;
    private IntPtr _device;
    private HidHideControl.Undo? _hidHideUndo;
    private SpeakerEndpoint? _speaker;
    private HapticsRelay? _haptics;
    private Thread? _pump;
    private byte _sequence;
    private volatile bool _headphones;
    // The audio fields of the game's last output reports (USB 0x02 body offsets 4, 5, 7 and 37); -1 = never set, use defaults.
    private volatile int _headphoneVolume = -1, _speakerVolume = -1, _audioControl = -1, _audioControl2 = -1;

    /// <summary>Haptics reach the pad: Steam's speaker driver is there and its speaker is being recorded.</summary>
    public bool HasHaptics => _haptics?.Recording == true;
    /// <summary>The speaker could be set up (Steam's driver is installed); recording it may still be failing.</summary>
    public bool HasSpeaker => _haptics is not null;
    public string? HapticsProblem => _haptics?.Problem;
    /// <summary>Why games may still see the real pad (HidHide missing, inactive, or the pad couldn't restart); null when hidden.</summary>
    public string? HidHideProblem { get; private set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public string Mac => _pad.Mac;
    /// <summary>The real pad's HID instance id (what HidHide hides).</summary>
    public string InstanceId => _pad.InstanceId;
    /// <summary>The pad is still connected and sending (a Bluetooth pad in full mode streams non-stop).</summary>
    public bool Alive => !_pad.IsClosed && DateTime.UtcNow - _pad.LastReportAt < TimeSpan.FromSeconds(4);

    private static string LeftoverFile => Path.Combine(Log.Folder, "session-undo.txt");

    private Bridge() { }

    /// <summary>Bridges the pad at <paramref name="path"/> (a Bluetooth DualSense's HID path).</summary>
    /// <param name="forceRestart">Restart the real pad even when HidHide's settings were right already (Repair).</param>
    public static Bridge Start(string path, SpeakerEndpoint speaker, IReadOnlyCollection<string> allowedExes, bool forceRestart = false)
    {
        var bridge = new Bridge();
        try
        {
            // Hide the real pad first: before the virtual one appears, so games starting now never see two, and before
            // opening it, so this service is on HidHide's allowed list even when someone else hid the pad already.
            path = bridge.HideRealPad(path, allowedExes, forceRestart);
            var pad = bridge._pad = OpenPad(path) ?? throw new IOException("couldn't open the controller (another app may be using it)");

            // Calibration and firmware replies (games read them from the virtual pad); reading 0x05 also switches the
            // real pad to full reports.
            var calibration = pad.GetFeature(0x05, 41) ?? throw new IOException("the controller didn't answer");
            var firmware = pad.GetFeature(0x20, 64) ?? [];
            bridge.CreateSection(calibration, firmware);
            pad.Report += bridge.OnInput;
            pad.Start();
            bridge.CreateDevice();

            var release = new byte[DualSenseReports.BtOutputLength];
            DualSenseReports.LightbarRelease(bridge._sequence++, release);
            pad.Write(release);
            bridge._pump = new Thread(bridge.PumpOutputs) { IsBackground = true, Name = "output pump", Priority = ThreadPriority.AboveNormal };
            bridge._pump.Start();

            if (speaker.Show() is { } endpoint)
            {
                bridge._speaker = speaker;
                bridge._haptics = new HapticsRelay(endpoint, report => pad.Write(report.Span), bridge.SpeakerSetup, Log.Write);
                bridge._haptics.Start();
            }
            Log.Write($"bridge started: {pad.InstanceId}{(bridge.HasSpeaker ? "" : $" (no haptics: {speaker.Problem})")}");
            return bridge;
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
    }

    // Hides the pad with HidHide and makes sure it took: restarts the pad when its visibility changed (what had it open,
    // Windows' game controller input above all, would keep seeing it) or HidHide's filter isn't attached to it (HidHide
    // installed while it was connected), then checks the filter is there. Returns the pad's path after a restart.
    private string HideRealPad(string path, IReadOnlyCollection<string> allowedExes, bool forceRestart)
    {
        var id = PadConnection.InstanceIdOf(path);
        var hide = HidHideControl.Hide(id, allowedExes);
        _hidHideUndo = hide.Undo;
        File.WriteAllText(LeftoverFile, hide.Undo.Serialize());
        if (hide.Problem is not null) { HidHideProblem = hide.Problem; return path; }

        bool? filtered = DeviceNode.HasDriver(id, "HidHide");
        if (hide.Undo.Changed || filtered == false || forceRestart)
        {
            if (DeviceNode.Restart(id) is { } error)
            {
                Log.Write($"HidHide: couldn't restart the controller ({error})");
                HidHideProblem = $"HidHide couldn't hide the controller: it couldn't be restarted ({error}). Turn the controller off and on, then choose Repair";
                return path;
            }
            path = WaitForPad(id) ?? throw new IOException("the controller didn't come back after restarting it");
            filtered = DeviceNode.HasDriver(id, "HidHide");
            Log.Write($"HidHide: restarted the controller (HidHide attached: {filtered?.ToString() ?? "unknown"})");
        }
        if (filtered == false)
            HidHideProblem = "HidHide couldn't hide the controller: HidHide isn't active yet. Restart Windows (HidHide needs that after installing), then choose Repair";
        return path;
    }

    // Right after a restart the path can be listed a moment before it opens.
    private static PadConnection? OpenPad(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (PadConnection.TryOpen(path) is { } pad) return pad;
            if (attempt == 8) return null;
            Thread.Sleep(250);
        }
    }

    private static string? WaitForPad(string instanceId)
    {
        for (var until = DateTime.UtcNow.AddSeconds(8); DateTime.UtcNow < until; Thread.Sleep(250))
            if (PadConnection.FindBluetoothPads().FirstOrDefault(p => PadConnection.InstanceIdOf(p) == instanceId) is { } path)
                return path;
        return null;
    }

    /// <summary>The tray's speaker switch, applied every second while the bridge runs.</summary>
    public void ApplyFeatures(bool speaker)
    {
        if (_haptics is { } haptics) haptics.SpeakerOn = speaker;
    }

    private void OnInput(ReadOnlySpan<byte> report)
    {
        if (_channel is { } channel && DualSenseReports.ToUsbInput(report, _usbInput))
        {
            _headphones = DualSenseReports.HeadphonesPlugged(_usbInput);
            channel.WriteInput(_usbInput);
        }
    }

    // The speaker report's audio settings: what the game last set, else full volume on the speaker or the headphones
    // (whichever is plugged in) with DS5_Bridge's preamp.
    private bool SpeakerSetup(Span<byte> state)
    {
        bool headphones = _headphones;
        if (state.Length >= DualSenseReports.AudioStateLength)
            DualSenseReports.AudioState(
                (byte)(_headphoneVolume is >= 0 and var h ? h : 0x7F),
                (byte)(_speakerVolume is >= 0 and var s ? s : 0x64),
                (byte)(_audioControl is >= 0 and var a ? a : headphones ? 0x00 : 0x30),
                (byte)(_audioControl2 is >= 0 and var a2 ? a2 : 0x02),
                state);
        return headphones;
    }

    private void NoteAudioFields(ReadOnlySpan<byte> usb)
    {
        byte flag0 = usb[1], flag1 = usb[2];
        if ((flag0 & 0x10) != 0) _headphoneVolume = usb[1 + 4];
        if ((flag0 & 0x20) != 0) _speakerVolume = usb[1 + 5];
        if ((flag0 & 0x80) != 0) _audioControl = usb[1 + 7];
        if ((flag1 & 0x80) != 0) _audioControl2 = usb[1 + 37];
    }

    private void PumpOutputs()
    {
        timeBeginPeriod(1);
        try
        {
            var bt = new byte[DualSenseReports.BtOutputLength];
            while (!_cts.IsCancellationRequested && _channel is { } channel)
            {
                channel.ReadOutputs(usb =>
                {
                    if (!DualSenseReports.ToBtOutput(usb, _sequence++, bt)) return;
                    NoteAudioFields(usb);
                    _pad.Write(bt);
                });
                Thread.Sleep(1);
            }
        }
        finally { timeEndPeriod(1); }
    }

    private void CreateSection(byte[] feature05, byte[] feature20)
    {
        // SYSTEM and admins full; LOCAL SERVICE (WUDFHost, where the driver runs) read/write.
        const string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;LS)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr sd, IntPtr.Zero))
            throw new IOException($"bad security descriptor ({Marshal.GetLastWin32Error()})");
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd };
            _sectionHandle = CreateFileMappingW(new IntPtr(-1), ref sa, 0x04 /* PAGE_READWRITE */, 0, VirtualPad.SectionSize, VirtualPad.SectionName);
            if (_sectionHandle.IsInvalid) throw new IOException($"CreateFileMapping failed ({Marshal.GetLastWin32Error()})");
        }
        finally { LocalFree(sd); }
        _channel = new VirtualPadChannel(MemoryMappedFile.OpenExisting(VirtualPad.SectionName, MemoryMappedFileRights.ReadWrite));
        _channel.Initialize(feature05, feature20, NeutralInput);
    }

    private void CreateDevice()
    {
        static string MultiSz(params string[] items) => string.Join("\0", items) + "\0\0";
        var container = VirtualPad.ContainerId;
        var id = new StringBuilder(200);
        int hr = PhPadCreate(Enumerator, Instance,
            MultiSz("HapticLink_DualSense", @"USB\VID_054C&PID_0CE6&REV_0100&MI_03", @"USB\VID_054C&PID_0CE6&MI_03"),
            // A USB token first: native PlayStation code tells a wired pad from a Bluetooth one by the parent's compatible ids.
            MultiSz(@"USB\VID_054C&PID_0CE6&MI_03", @"USB\Class_03&SubClass_00&Prot_00", @"USB\Class_03"),
            ref container, "HapticLink Virtual DualSense", "0", 10_000, out _device, id, id.Capacity);
        if (hr != 0) throw new IOException($"couldn't create the virtual controller (0x{hr:X8}); is the driver installed?");
        Log.Write($"virtual controller created: {id}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _haptics?.Dispose();
        _haptics = null;
        if (_device != IntPtr.Zero) { PhPadClose(_device); _device = IntPtr.Zero; }
        _speaker?.Hide();
        var channel = _channel;
        _channel = null;
        _pump?.Join(500);
        if (_pad is not null)
        {
            _pad.Report -= OnInput;
            _pad.Dispose();
        }
        if (_hidHideUndo is not null) // after closing the pad: restoring restarts it
        {
            HidHideControl.Restore(_hidHideUndo);
            _hidHideUndo = null;
            try { File.Delete(LeftoverFile); } catch (IOException) { }
        }
        channel?.Dispose();
        _sectionHandle?.Dispose();
        Log.Write("bridge stopped");
    }

    /// <summary>A session the service didn't get to end (crash, power cut): unhide the real pad again.</summary>
    public static void UndoLeftovers()
    {
        try
        {
            if (!File.Exists(LeftoverFile)) return;
            if (HidHideControl.Undo.Deserialize(File.ReadAllText(LeftoverFile)) is { } undo)
            {
                Log.Write("undoing a session that wasn't ended");
                DeviceNode.Enable(undo.InstanceId); // in case it ended halfway through restarting the pad
                HidHideControl.Restore(undo);
            }
            File.Delete(LeftoverFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"leftover undo: {ex.Message}"); }
    }

    // Input report 0x01 at rest (sticks centred, hat neutral, 1 g up, touch lifted); same as the driver's default.
    private static readonly byte[] NeutralInput = MakeNeutral();
    private static byte[] MakeNeutral()
    {
        var r = new byte[64];
        r[0] = 0x01; r[1] = r[2] = r[3] = r[4] = 0x80; r[8] = 0x08; r[24] = 0x10; r[25] = 0x27;
        r[32] = 0x14; r[33] = 0x80; r[37] = 0x80; r[42] = r[43] = 0x09; r[53] = 0x2A; r[54] = 0x18;
        return r;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }

    [DllImport("HapticLinkPadHost.dll", CharSet = CharSet.Unicode)]
    private static extern int PhPadCreate(string enumerator, string instanceId, string hardwareIds, string compatibleIds,
        ref Guid containerId, string description, string location, uint timeoutMs, out IntPtr device, StringBuilder instanceIdOut, int instanceIdChars);
    [DllImport("HapticLinkPadHost.dll")] private static extern void PhPadClose(IntPtr device);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileMappingW(IntPtr file, ref SecurityAttributes sa, uint protect, uint sizeHigh, uint sizeLow, string name);
    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(int period);
    [DllImport("winmm.dll")] private static extern int timeEndPeriod(int period);
}
