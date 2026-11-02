using System.Runtime.InteropServices;

namespace HapticLink.Service;

/// <summary>A present device node by instance id: restarting it and reading its driver stack (Configuration Manager).</summary>
internal static class DeviceNode
{
    private const int CrSuccess = 0, CrRemoveVetoed = 0x17;
    private const uint DisableUiNotOk = 0x4; // fail instead of asking; not persistent, so a reboot clears it

    /// <summary>
    /// Disables and enables the device, so everything that had it open (apps, and Windows' own game controller input)
    /// opens it again. Null on success, else why not.
    /// </summary>
    public static string? Restart(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != CrSuccess) return "it isn't connected";
        int cr = CM_Disable_DevNode(node, DisableUiNotOk);
        if (cr != CrSuccess) return cr == CrRemoveVetoed ? "another app wouldn't let go of it" : $"disabling it failed (CR {cr:X})";
        for (int attempt = 0; ; attempt++)
        {
            cr = CM_Enable_DevNode(node, 0);
            if (cr == CrSuccess) return null;
            if (attempt == 4) return $"enabling it failed (CR {cr:X})";
            Thread.Sleep(200);
        }
    }

    /// <summary>Enables the device if present (undoes a restart cut short by a crash). Harmless when it's enabled.</summary>
    public static void Enable(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint node, instanceId, 0) == CrSuccess) CM_Enable_DevNode(node, 0);
    }

    /// <summary>
    /// Whether the driver (e.g. "HidHide") is in the device's stack, i.e. attached to it right now; null when the stack
    /// can't be read. A filter installed while the device was connected only attaches when it starts again.
    /// </summary>
    public static bool? HasDriver(string instanceId, string driver)
    {
        if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != CrSuccess) return null;
        var key = new DevPropKey { Fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2), Pid = 14 }; // DEVPKEY_Device_Stack
        uint size = 0;
        CM_Get_DevNode_PropertyW(node, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(node, ref key, out _, buffer, ref size, 0) != CrSuccess) return null;
        // A string list: "\Driver\HidHide\0\Driver\HidBth\0\0".
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).Split('\0')
            .Any(s => s.Equals(@"\Driver\" + driver, StringComparison.OrdinalIgnoreCase));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid Fmtid; public uint Pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Disable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Enable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);
}
