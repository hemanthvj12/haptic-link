using System.Runtime.InteropServices;
using System.Text;

namespace HapticLink.App;

/// <summary>
/// Whether games can see the real (Bluetooth) DualSense next to HapticLink's virtual one. Windows' game controller input
/// (raw input, which browsers and most games read) lists the HID devices it has open: while HidHide hides the real pad it
/// isn't there, and when it is, a gamepad tester shows two controllers (checked on the owner's PC, 2026-11-02: listed
/// with HapticLink off, gone with it on). Runs in the user's session, where that list is; the service can't see it.
/// </summary>
internal static class DoubleInput
{
    private const string BluetoothHid = "{00001124-0000-1000-8000-00805f9b34fb}";

    /// <summary>A Bluetooth DualSense or DualSense Edge is in the raw input device list.</summary>
    public static bool RealPadVisible()
    {
        uint count = 0, size = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return false;
        var devices = new RawInputDeviceList[count];
        if (GetRawInputDeviceList(devices, ref count, size) == uint.MaxValue) return false;
        foreach (var device in devices.Take((int)count))
        {
            if (device.Type != 2 /* RIM_TYPEHID */) continue;
            uint length = 0;
            GetRawInputDeviceInfoW(device.Device, 0x20000007 /* RIDI_DEVICENAME */, null, ref length);
            if (length == 0) continue;
            var name = new StringBuilder((int)length + 1);
            if (GetRawInputDeviceInfoW(device.Device, 0x20000007, name, ref length) == uint.MaxValue) continue;
            var n = name.ToString().ToLowerInvariant();
            if (n.Contains(BluetoothHid) && n.Contains("_vid&0002054c") && (n.Contains("_pid&0ce6") || n.Contains("_pid&0df2"))) return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList { public IntPtr Device; public uint Type; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? list, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, StringBuilder? data, ref uint size);
}
