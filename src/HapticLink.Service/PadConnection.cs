using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HapticLink.Service;

/// <summary>
/// The real DualSense over Bluetooth: finding it, one continuous reader (a second pending read would swallow reports),
/// output writes (Windows wants them padded to the output report length) and feature reads.
/// Reads and writes use the overlapped handle directly with one reused OVERLAPPED each: through a FileStream every read
/// and write allocated (hundreds a second), which grew the service by ~18 MB of garbage before the GC's first collection.
/// </summary>
internal sealed unsafe class PadConnection : IDisposable
{
    private const string BluetoothHid = "{00001124-0000-1000-8000-00805f9b34fb}";
    private readonly SafeFileHandle _handle;
    private readonly OverlappedIo _readIo = new(), _writeIo = new();
    private readonly object _writeLock = new();
    private readonly byte[] _writeBuffer;
    private readonly int _inputLength;
    private volatile bool _closing;
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

    private PadConnection(string path, SafeFileHandle handle, int inputLength, int outputLength, string mac, bool edge)
    {
        Path = path;
        _handle = handle;
        _inputLength = inputLength;
        _writeBuffer = new byte[outputLength];
        Mac = mac;
        IsEdge = edge;
        InstanceId = InstanceIdOf(path);
    }

    /// <summary>The HID instance id of a device path, upper case as Windows lists them.</summary>
    public static string InstanceIdOf(string path)
    {
        var p = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        int guid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0) p = p[..guid];
        return p.Replace('#', '\\').ToUpperInvariant();
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
        return new PadConnection(path, handle, input, output, mac, edge);
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
            // Ends when the pad goes away or Dispose cancels the pending read (CancelIoEx).
            while (!_closing)
            {
                int n = _readIo.Transfer(_handle, buffer, buffer.Length, write: false);
                if (n <= 0) break;
                LastReportAt = DateTime.UtcNow;
                Report?.Invoke(new ReadOnlySpan<byte>(buffer, 0, n));
            }
        }
        catch (ObjectDisposedException) { }
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
            try { return _writeIo.Transfer(_handle, _writeBuffer, _writeBuffer.Length, write: true) == _writeBuffer.Length; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    /// <summary>Reads a feature report (null on failure). Reading 0x05 also switches the pad to full reports.</summary>
    public byte[]? GetFeature(byte id, int length)
    {
        var buffer = new byte[length];
        buffer[0] = id;
        try { return HidD_GetFeature(_handle, buffer, buffer.Length) ? buffer : null; }
        catch (ObjectDisposedException) { return null; }
    }

    public void Dispose()
    {
        IsClosed = true;
        _closing = true;
        try { CancelIoEx(_handle, IntPtr.Zero); } catch (ObjectDisposedException) { }
        _reader?.Join(1000);
        lock (_writeLock) _handle.Dispose();
        _readIo.Dispose();
        _writeIo.Dispose();
    }

    /// <summary>
    /// One OVERLAPPED (in native memory, so it never moves) and its event, reused for every read or write in one
    /// direction; each call waits for its transfer to finish, so only one is ever pending per instance.
    /// </summary>
    private sealed class OverlappedIo : IDisposable
    {
        private readonly NativeOverlapped* _overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        private readonly IntPtr _event = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);

        /// <summary>Bytes transferred, or -1 when the transfer failed (pad gone, cancelled).</summary>
        public int Transfer(SafeFileHandle handle, byte[] buffer, int length, bool write)
        {
            *_overlapped = default;
            // The event's low bit set keeps the completion from being queued to a completion port.
            _overlapped->EventHandle = _event | 1;
            fixed (byte* data = buffer)
            {
                bool done = write ? WriteFile(handle, data, length, IntPtr.Zero, _overlapped)
                                  : ReadFile(handle, data, length, IntPtr.Zero, _overlapped);
                if (!done && Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */) return -1;
                // Waits here, with the buffer still pinned, until the transfer ends.
                return GetOverlappedResult(handle, _overlapped, out int n, true) ? n : -1;
            }
        }

        public void Dispose()
        {
            CloseHandle(_event);
            NativeMemory.Free(_overlapped);
        }
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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle h, byte* buffer, int length, IntPtr read, NativeOverlapped* overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte* buffer, int length, IntPtr written, NativeOverlapped* overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* overlapped, out int transferred, bool wait);
    [DllImport("kernel32.dll")] private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateEventW(IntPtr security, bool manualReset, bool initialState, IntPtr name);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
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
