using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HapticLink.Service;

/// <summary>
/// The real DualSense over Bluetooth: finding it, one continuous reader (a second pending read would swallow reports),
/// output writes (Windows wants them padded to the output report length) and feature reads.
/// </summary>
internal sealed class PadConnection : IDisposable
{
    private const string BluetoothHid = "{00001124-0000-1000-8000-00805f9b34fb}";
    private readonly FileStream _stream;
    private readonly object _writeLock = new();
    private readonly byte[] _writeBuffer;
    private readonly int _inputLength;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _reader;

    public string Path { get; }
    /// <summary>The HID instance id (HidHide names devices by it), upper case as Windows lists them.</summary>
    public string InstanceId { get; }
    /// <summary>The controller's Bluetooth address (its HID serial), 12 lower-case hex digits, or "".</summary>
    public string Mac { get; }
    public bool IsEdge { get; }
    public bool IsClosed { get; private set; }
    public DateTime LastReportAt { get; private set; } = DateTime.UtcNow;
    /// <summary>Each input report; the span is only valid during the call.</summary>
    public event VirtualPadChannel.ReportHandler? Report;

    private PadConnection(string path, FileStream stream, int inputLength, int outputLength, string mac, bool edge)
    {
        Path = path;
        _stream = stream;
        _inputLength = inputLength;
        _writeBuffer = new byte[outputLength];
        Mac = mac;
        IsEdge = edge;
        var p = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        int guid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0) p = p[..guid];
        InstanceId = p.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>HID paths of DualSense / DualSense Edge pads connected over Bluetooth.</summary>
    public static List<string> FindBluetoothPads() => HidPaths().Where(p =>
    {
        var l = p.ToLowerInvariant();
        return l.Contains(BluetoothHid) && (l.Contains("_vid&0002054c_pid&0ce6") || l.Contains("_vid&0002054c_pid&0df2"));
    }).ToList();

    public static PadConnection? TryOpen(string path)
    {
        var handle = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /* overlapped */, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        if (!HidD_GetPreparsedData(handle, out var pp)) { handle.Dispose(); return null; }
        var caps = new byte[64];
        HidP_GetCaps(pp, caps);
        HidD_FreePreparsedData(pp);
        int input = BitConverter.ToUInt16(caps, 4), output = BitConverter.ToUInt16(caps, 6);
        var serial = new byte[256];
        string mac = HidD_GetSerialNumberString(handle, serial, serial.Length)
            ? new string(System.Text.Encoding.Unicode.GetString(serial).TrimEnd('\0').Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant()
            : "";
        if (mac.Length != 12) mac = "";
        bool edge = path.Contains("pid&0df2", StringComparison.OrdinalIgnoreCase);
        return new PadConnection(path, new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true), input, output, mac, edge);
    }

    public void Start()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pad reader", Priority = ThreadPriority.AboveNormal };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[Math.Max(_inputLength, 1)];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = _stream.ReadAsync(buffer, _cts.Token).AsTask().GetAwaiter().GetResult();
                if (n <= 0) break;
                LastReportAt = DateTime.UtcNow;
                Report?.Invoke(new ReadOnlySpan<byte>(buffer, 0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException) { }
        IsClosed = true;
    }

    /// <summary>Sends an output report, padded to the output report length; false when the write failed.</summary>
    public bool Write(ReadOnlySpan<byte> report)
    {
        if (IsClosed) return false;
        lock (_writeLock)
        {
            report.CopyTo(_writeBuffer);
            _writeBuffer.AsSpan(report.Length).Clear();
            try { _stream.Write(_writeBuffer, 0, _writeBuffer.Length); return true; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { return false; }
        }
    }

    /// <summary>Reads a feature report (null on failure). Reading 0x05 also switches the pad to full reports.</summary>
    public byte[]? GetFeature(byte id, int length)
    {
        var buffer = new byte[length];
        buffer[0] = id;
        try { return HidD_GetFeature(_stream.SafeFileHandle, buffer, buffer.Length) ? buffer : null; }
        catch (ObjectDisposedException) { return null; }
    }

    public void Dispose()
    {
        IsClosed = true;
        _cts.Cancel();
        _stream.Dispose();
        _reader?.Join(1000);
        _cts.Dispose();
    }

    private static List<string> HidPaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return paths;
        try
        {
            var data = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, 8);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(detail + 4)!);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, byte[] caps);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
