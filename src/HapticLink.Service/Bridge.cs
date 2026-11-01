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

    private readonly PadConnection _pad;
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

    public bool HasHaptics => _haptics is not null;
    public string Mac => _pad.Mac;
    /// <summary>The pad is still connected and sending (a Bluetooth pad in full mode streams non-stop).</summary>
    public bool Alive => !_pad.IsClosed && DateTime.UtcNow - _pad.LastReportAt < TimeSpan.FromSeconds(4);

    private static string LeftoverFile => Path.Combine(Log.Folder, "session-undo.txt");

    private Bridge(PadConnection pad) => _pad = pad;

    public static Bridge Start(PadConnection pad, SpeakerEndpoint speaker, IReadOnlyCollection<string> allowedExes)
    {
        var bridge = new Bridge(pad);
        try
        {
            // Calibration and firmware replies (games read them from the virtual pad); reading 0x05 also switches the
            // real pad to full reports.
            var calibration = pad.GetFeature(0x05, 41) ?? throw new IOException("the controller didn't answer");
            var firmware = pad.GetFeature(0x20, 64) ?? [];
            bridge.CreateSection(calibration, firmware);
            pad.Report += bridge.OnInput;
            pad.Start();
            // Hide the real pad before the virtual one appears, so games starting now never see two.
            bridge._hidHideUndo = HidHideControl.Hide(pad.InstanceId, allowedExes);
            File.WriteAllText(LeftoverFile, bridge._hidHideUndo.Serialize());
            bridge.CreateDevice();

            var release = new byte[DualSenseReports.BtOutputLength];
            DualSenseReports.LightbarRelease(bridge._sequence++, release);
            pad.Write(release);
            bridge._pump = new Thread(bridge.PumpOutputs) { IsBackground = true, Name = "output pump", Priority = ThreadPriority.AboveNormal };
            bridge._pump.Start();

            if (speaker.Show() is { } endpoint)
            {
                bridge._speaker = speaker;
                bridge._haptics = new HapticsRelay(endpoint, report => pad.Write(report.Span), Log.Write);
                bridge._haptics.Start();
            }
            Log.Write($"bridge started: {pad.InstanceId}{(bridge.HasHaptics ? "" : " (no haptics: Steam's speaker driver isn't installed)")}");
            return bridge;
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
    }

    private void OnInput(ReadOnlySpan<byte> report)
    {
        if (_channel is { } channel && DualSenseReports.ToUsbInput(report, _usbInput)) channel.WriteInput(_usbInput);
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
                    if (DualSenseReports.ToBtOutput(usb, _sequence++, bt)) _pad.Write(bt);
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
        if (_hidHideUndo is not null)
        {
            HidHideControl.Restore(_hidHideUndo);
            _hidHideUndo = null;
            try { File.Delete(LeftoverFile); } catch (IOException) { }
        }
        _pad.Report -= OnInput;
        _pad.Dispose();
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
