using System.Runtime.InteropServices;

namespace HapticLink.Service;

/// <summary>Core Audio calls the speaker endpoint needs: state, visibility, default device, property store.</summary>
internal static class Audio
{
    private const int Render = 0;

    public static string? DefaultRenderId()
    {
        var enumerator = (IMMDeviceEnumerator)ComObjects.Create(ComObjects.MMDeviceEnumerator);
        if (enumerator.GetDefaultAudioEndpoint(Render, 1 /* multimedia */, out var device) != 0) return null;
        return device.GetId(out var id) == 0 ? id : null;
    }

    public static int State(string id)
    {
        var enumerator = (IMMDeviceEnumerator)ComObjects.Create(ComObjects.MMDeviceEnumerator);
        if (enumerator.GetDevice(id, out var device) != 0) return 0;
        return device.GetState(out int state) == 0 ? state : 0;
    }

    /// <summary>Shows or hides an endpoint (hidden = disabled: not listed, can't be opened; no PnP change).</summary>
    public static void SetVisible(string id, bool visible)
    {
        int hr = ((IPolicyConfig)ComObjects.Create(ComObjects.PolicyConfigClient)).SetEndpointVisibility(id, visible);
        if (hr != 0) Log.Write($"SetEndpointVisibility({visible}) on {id}: 0x{hr:X8}");
    }

    private const int AudclntEUnsupportedFormat = unchecked((int)0x88890008);

    /// <summary>
    /// Makes sure a loopback recording of the endpoint's 4-channel mix is accepted. On a freshly made endpoint the
    /// stamped formats are served but the audio engine keeps the driver's old one, so recording fails with
    /// AUDCLNT_E_UNSUPPORTED_FORMAT (seen 2026-11-02 on a new endpoint). Setting the format the way Sound settings does
    /// makes the engine apply it at once, and it stays.
    /// </summary>
    public static void EnsureFormat(string id, byte[] deviceFormat, byte[] mixFormat)
    {
        int hr = LoopbackCheck(id);
        if (hr != AudclntEUnsupportedFormat) return;
        IntPtr device = Marshal.AllocHGlobal(deviceFormat.Length), mix = Marshal.AllocHGlobal(mixFormat.Length);
        try
        {
            Marshal.Copy(deviceFormat, 0, device, deviceFormat.Length);
            Marshal.Copy(mixFormat, 0, mix, mixFormat.Length);
            int set = ((IPolicyConfig)ComObjects.Create(ComObjects.PolicyConfigClient)).SetDeviceFormat(id, device, mix);
            Thread.Sleep(500);
            Log.Write($"speaker endpoint: the audio engine refused its format; set it again (0x{set:X8}), recording now 0x{LoopbackCheck(id):X8}");
        }
        finally
        {
            Marshal.FreeHGlobal(device);
            Marshal.FreeHGlobal(mix);
        }
    }

    /// <summary>The HRESULT of opening a loopback recording in the endpoint's mix format (0 = fine); nothing is kept open.</summary>
    private static int LoopbackCheck(string id)
    {
        var enumerator = (IMMDeviceEnumerator)ComObjects.Create(ComObjects.MMDeviceEnumerator);
        if (enumerator.GetDevice(id, out var device) is not 0 and var hr0) return hr0;
        var iid = typeof(IAudioClient).GUID;
        if (device.Activate(ref iid, 0x17, IntPtr.Zero, out var o) is not 0 and var hr1) return hr1;
        var client = (IAudioClient)o;
        try
        {
            if (client.GetMixFormat(out IntPtr format) is not 0 and var hr2) return hr2;
            try { return client.Initialize(0, 0x00020000 /* loopback */, 300_000, 0, format, IntPtr.Zero); }
            finally { Marshal.FreeCoTaskMem(format); }
        }
        finally { Marshal.ReleaseComObject(client); }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
    }

    /// <summary>A newly shown endpoint may grab the default; put <paramref name="before"/> back if it did.</summary>
    public static void KeepDefault(string? before, string endpoint)
    {
        if (before is null || before == endpoint || DefaultRenderId() != endpoint) return;
        var policy = (IPolicyConfig)ComObjects.Create(ComObjects.PolicyConfigClient);
        for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(before, role);
        Log.Write("the pad speaker had become the default playback device; put the previous one back");
    }

    /// <summary>Whether the endpoint's property store already returns this value.</summary>
    public static bool Served(string id, SpeakerEndpoint.StampItem item)
    {
        var enumerator = (IMMDeviceEnumerator)ComObjects.Create(ComObjects.MMDeviceEnumerator);
        if (enumerator.GetDevice(id, out var device) != 0 || device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return false;
        var key = new PropertyKey { Format = item.Format, Id = item.Id };
        if (store.GetValue(ref key, out var v) != 0) return false;
        try
        {
            return item.Value switch
            {
                string text => v.Type == 31 && Marshal.PtrToStringUni(v.Pointer) == text,
                Guid g => v.Type == 72 && v.Pointer != IntPtr.Zero && Marshal.PtrToStructure<Guid>(v.Pointer) == g,
                byte[] blob => v.Type == 65 && v.BlobSize == blob.Length && Bytes(v.BlobPointer, blob.Length).SequenceEqual(blob),
                _ => false,
            };
        }
        finally { PropVariantClear(ref v); }
    }

    /// <summary>Writes through the property store; returns the items it refused (or all, if it can't be opened writable).</summary>
    public static List<SpeakerEndpoint.StampItem> Write(string id, List<SpeakerEndpoint.StampItem> items)
    {
        var enumerator = (IMMDeviceEnumerator)ComObjects.Create(ComObjects.MMDeviceEnumerator);
        if (enumerator.GetDevice(id, out var device) != 0 || device.OpenPropertyStore(2 /* STGM_READWRITE */, out var store) != 0)
            return items;
        var refused = new List<SpeakerEndpoint.StampItem>();
        var written = new List<string>();
        foreach (var item in items)
        {
            var key = new PropertyKey { Format = item.Format, Id = item.Id };
            var v = new PropVariant();
            IntPtr memory = IntPtr.Zero;
            switch (item.Value)
            {
                case string text: v.Type = 31; memory = Marshal.StringToCoTaskMemUni(text); v.Pointer = memory; break;
                case Guid g: v.Type = 72; memory = Marshal.AllocCoTaskMem(16); Marshal.Copy(g.ToByteArray(), 0, memory, 16); v.Pointer = memory; break;
                case byte[] blob: v.Type = 65; memory = Marshal.AllocCoTaskMem(blob.Length); Marshal.Copy(blob, 0, memory, blob.Length); v.BlobSize = blob.Length; v.BlobPointer = memory; break;
            }
            int hr = store.SetValue(ref key, ref v);
            Marshal.FreeCoTaskMem(memory);
            if (hr == 0) written.Add(item.Label); else refused.Add(item);
        }
        if (written.Count > 0 && store.Commit() != 0) return items;
        if (written.Count > 0) Log.Write($"speaker endpoint: stamped through the property store: {string.Join(", ", written)}");
        return refused;
    }

    private static byte[] Bytes(IntPtr p, int length)
    {
        var b = new byte[length];
        Marshal.Copy(p, b, 0, length);
        return b;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public int Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] public int BlobSize;
        [FieldOffset(16)] public IntPtr BlobPointer;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant v);


    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }
}
