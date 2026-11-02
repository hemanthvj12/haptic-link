using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HapticLink.Service;

/// <summary>SetupAPI plumbing for the root-enumerated speaker devnode.</summary>
internal static class Devnodes
{
    /// <summary>A ROOT devnode of <paramref name="classGuid"/> whose Device Parameters hold marker = value.</summary>
    public static string? FindMarked(Guid classGuid, string marker, int value)
    {
        // Flags 0 (not DIGCF_PRESENT): a created-but-not-started devnode from an interrupted setup is reused.
        IntPtr set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = NewData();
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var id = InstanceId(set, ref data);
                if (id is null || !id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) continue;
                using var key = OpenDeviceParameters(set, ref data, write: false);
                if (key is null) continue;
                if (ReadDword(key, marker) == value) return id;
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>
    /// Writes the marker again on <paramref name="instanceId"/> when that devnode still exists with
    /// <paramref name="hardwareId"/>; false otherwise. Uninstalling its driver package wipes Device Parameters, marker
    /// included (seen 2026-11-02 after `pnputil /delete-driver /uninstall` of Steam's speaker driver).
    /// </summary>
    public static bool Remark(string instanceId, string hardwareId, string marker, int value)
    {
        // No class filter: a devnode without a driver may not be in its old class any more.
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return false;
        try
        {
            var data = NewData();
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data))
            {
                Log.Write($"devnode {instanceId}: not found ({Marshal.GetLastWin32Error()})");
                return false;
            }
            if (!MultiSz(set, ref data, 1 /* SPDRP_HARDWAREID */).Any(h => h.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Write($"devnode {instanceId}: hardware id isn't {hardwareId} any more");
                return false;
            }
            using var key = OpenDeviceParameters(set, ref data, write: true);
            if (key is null)
            {
                Log.Write($"devnode {instanceId}: couldn't open Device Parameters ({Marshal.GetLastWin32Error()})");
                return false;
            }
            key.SetValue(marker, value, RegistryValueKind.DWord);
            return true;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>The installed INF (full path) of a devnode of this class carrying <paramref name="hardwareId"/>.</summary>
    public static string? InfOfHardwareId(Guid classGuid, string hardwareId)
    {
        IntPtr set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = NewData();
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!MultiSz(set, ref data, 1 /* SPDRP_HARDWAREID */).Any(h => h.Equals(hardwareId, StringComparison.OrdinalIgnoreCase))) continue;
                var key = new DevPropKey { Format = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), Id = 5 }; // DEVPKEY_Device_DriverInfPath
                var buffer = new byte[520];
                if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buffer, buffer.Length, out int size, 0)) continue;
                var inf = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, size - 2));
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf);
                if (inf.Length > 0 && File.Exists(path)) return path;
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>
    /// Creates ROOT\&lt;enumerator&gt;\NNNN with one hardware id and a marker in its Device Parameters; returns its instance id.
    /// Registered with SetupDiRegisterDeviceInfo: DIF_REGISTERDEVICE needs an interactive window station and fails from a
    /// service (punktfunk's finding).
    /// </summary>
    public static string CreateRootDevnode(Guid classGuid, string enumerator, string description, string hardwareId, string marker, int value)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var data = NewData();
            if (!SetupDiCreateDeviceInfoW(set, enumerator, ref classGuid, description, IntPtr.Zero, 1 /* DICD_GENERATE_ID */, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfo");
            var ids = System.Text.Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, 1 /* SPDRP_HARDWAREID */, ids, ids.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "set the hardware id");
            if (!SetupDiRegisterDeviceInfo(set, ref data, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiRegisterDeviceInfo");
            using (var key = OpenDeviceParameters(set, ref data, write: true) ?? throw new IOException("no Device Parameters key"))
                key.SetValue(marker, value, RegistryValueKind.DWord);
            var id = InstanceId(set, ref data) ?? throw new IOException("the new devnode has no instance id");
            Log.Write($"created devnode {id}");
            return id;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Installs <paramref name="inf"/> on devnodes with this hardware id that don't have it; nothing to do is fine.</summary>
    public static void BindDriver(string hardwareId, string inf)
    {
        if (UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, inf, 0, out _)) return;
        int error = Marshal.GetLastWin32Error();
        if (error == 0x103 /* ERROR_NO_MORE_ITEMS: all already bound */) return;
        throw new Win32Exception(error, $"installing {Path.GetFileName(inf)}");
    }

    /// <summary>
    /// Copies a device's friendly name into its description when they differ; returns the old description, or null when
    /// nothing changed. Windows keeps an audio endpoint's device (SWD\MMDEVAPI\…) description from when the endpoint first
    /// registered, and Sound settings shows its second line from it: renaming the endpoint updated the friendly name but
    /// left the description saying "Wireless Controller (4- DualSense Wireless Controller)" through audio and device
    /// restarts (2026-11-02). Writing it is what fixed the display.
    /// </summary>
    public static string? SyncDescription(string instanceId)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = NewData();
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data)) return null;
            var key = new DevPropKey { Format = new("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 }; // DEVPKEY_Device_FriendlyName
            var buffer = new byte[1024];
            if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buffer, buffer.Length, out int size, 0)) return null;
            string friendly = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, size - 2));
            string description = SetupDiGetDeviceRegistryPropertyW(set, ref data, 0 /* SPDRP_DEVICEDESC */, out _, buffer, buffer.Length, out size)
                ? System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, size - 2)) : "";
            if (friendly.Length == 0 || friendly == description) return null;
            var bytes = System.Text.Encoding.Unicode.GetBytes(friendly + "\0");
            return SetupDiSetDeviceRegistryPropertyW(set, ref data, 0, bytes, bytes.Length) ? description : null;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static SpDevinfoData NewData() => new() { Size = Marshal.SizeOf<SpDevinfoData>() };

    private static string? InstanceId(IntPtr set, ref SpDevinfoData data)
    {
        var buffer = new char[400];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Length, out int size)
            ? new string(buffer, 0, Math.Max(0, size - 1)) : null;
    }

    private static string[] MultiSz(IntPtr set, ref SpDevinfoData data, int property)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, buffer.Length, out int size)) return [];
        return System.Text.Encoding.Unicode.GetString(buffer, 0, size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static RegistryKey? OpenDeviceParameters(IntPtr set, ref SpDevinfoData data, bool write)
    {
        IntPtr handle = write
            ? SetupDiCreateDevRegKeyW(set, ref data, 1 /* DICS_FLAG_GLOBAL */, 0, 1 /* DIREG_DEV */, IntPtr.Zero, null)
            : SetupDiOpenDevRegKey(set, ref data, 1, 0, 1, 0x20019 /* KEY_READ */);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return null;
        return RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true));
    }

    private static int? ReadDword(RegistryKey key, string name) => key.GetValue(name) is int v ? v : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid Format; public int Id; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr parent);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr parent, int flags, ref SpDevinfoData data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SpDevinfoData data, char[] buffer, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData data, int property, out int type, byte[] buffer, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData data, int property, byte[] buffer, int size);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SpDevinfoData data, ref DevPropKey key, out int type, byte[] buffer, int size, out int required, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid classGuid, string description, IntPtr parent, int flags, ref SpDevinfoData data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiRegisterDeviceInfo(IntPtr set, ref SpDevinfoData data, int flags, IntPtr compare, IntPtr context, IntPtr duplicate);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SpDevinfoData data, int scope, int profile, int keyType, int access);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiCreateDevRegKeyW(IntPtr set, ref SpDevinfoData data, int scope, int profile, int keyType, IntPtr infHandle, string? infSection);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr parent, string hardwareId, string infPath, int flags, out bool rebootRequired);
}
