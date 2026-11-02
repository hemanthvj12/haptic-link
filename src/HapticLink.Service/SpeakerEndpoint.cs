using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace HapticLink.Service;

/// <summary>
/// The virtual pad's speaker: games play DualSense haptics as 4-channel audio (channels 3-4 drive the voice coils) into
/// the sound device that shares the pad's container id. Windows has no such device for a software pad, so, like
/// punktfunk (pf-audio/src/windows/pad_endpoint.rs, MIT OR Apache-2.0), we add one more instance of Valve's signed
/// "Steam Streaming Speakers" driver (installed by Steam) and stamp it as the pad's speaker: name "Wireless Controller",
/// the pad's container id, 4 ch / 48 kHz. The app records it (WASAPI loopback) and relays channels 3-4 to the real pad.
/// The devnode is permanent (made once, removed by uninstall); the endpoint is hidden while no pad runs, because an
/// idle visible DualSense speaker can stall games.
/// </summary>
internal sealed class SpeakerEndpoint
{
    private const string SteamHardwareId = @"ROOT\SteamStreamingSpeakers";
    private const string MarkerValue = "HapticLinkPadIndex";
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");

    private readonly object _gate = new();
    private Task<string?>? _ensure;
    private volatile string? _problem;

    /// <summary>Why there's no speaker (so no haptics), for the status message; null when it's ready.</summary>
    public string? Problem => _problem;

    public void EnsureInBackground()
    {
        lock (_gate) _ensure ??= Task.Run(() =>
        {
            try
            {
                var id = Ensure();
                _problem = id is null ? "Steam's streaming speaker driver isn't installed" : null;
                return id;
            }
            catch (Exception ex)
            {
                Log.Write($"speaker endpoint: {ex}");
                _problem = $"couldn't set up the controller's speaker ({ex.Message})";
                return null;
            }
        });
    }

    /// <summary>Shows the endpoint for a starting pad; null when there's none (no Steam driver: no haptics).</summary>
    public string? Show()
    {
        EnsureInBackground();
        Task<string?> ensure;
        lock (_gate) ensure = _ensure!;
        if (!ensure.Wait(TimeSpan.FromSeconds(30)) || ensure.Result is not { } id)
        {
            lock (_gate) if (_ensure == ensure && ensure.IsCompleted) _ensure = null; // try again next start
            return null;
        }
        string? before = Audio.DefaultRenderId();
        Audio.SetVisible(id, true);
        for (int i = 0; i < 20 && Audio.State(id) != 1; i++) Thread.Sleep(100);
        Audio.EnsureFormat(id, Pcm16, Float32);
        Audio.KeepDefault(before, id);
        return id;
    }

    public void Hide()
    {
        Task<string?>? ensure;
        lock (_gate) ensure = _ensure;
        if (ensure is { IsCompletedSuccessfully: true, Result: { } id }) Audio.SetVisible(id, false);
    }

    /// <summary>Finds or makes the devnode, binds Steam's driver, stamps the endpoint; returns its id.</summary>
    private static string? Ensure()
    {
        string? inf = FindSteamInf();
        if (inf is null)
        {
            Log.Write("speaker endpoint: Steam's streaming speaker driver isn't installed (Steam Remote Play installs it); no haptics");
            return null;
        }
        string? before = Audio.DefaultRenderId();
        string instance = Devnodes.FindMarked(MediaClass, MarkerValue, 0) ?? Devnodes.CreateRootDevnode(MediaClass, "MEDIA",
            "HapticLink Pad Audio", SteamHardwareId, MarkerValue, 0);
        string? endpoint = FindEndpoint(instance);
        if (endpoint is null)
        {
            Devnodes.BindDriver(SteamHardwareId, inf);
            for (int i = 0; i < 40 && (endpoint = FindEndpoint(instance)) is null; i++) Thread.Sleep(250);
            if (endpoint is null) throw new IOException($"no sound endpoint appeared for {instance}");
        }

        bool served = false;
        for (int attempt = 0; attempt < 5 && !served; attempt++)
        {
            Stamp(endpoint);
            Thread.Sleep(1200); // the endpoint builder reverts fresh writes now and then; check after it settles
            served = Stamps.All(s => Audio.Served(endpoint, s));
        }
        if (!served)
        {
            Log.Write("speaker endpoint: stamps stored but not served; restarting Windows Audio once");
            RestartAudio();
            served = Stamps.All(s => Audio.Served(endpoint, s));
        }
        Audio.SetVisible(endpoint, false);
        Audio.KeepDefault(before, endpoint);
        Log.Write($"speaker endpoint ready: {endpoint} ({instance}), stamps served: {served}");
        return endpoint;
    }

    /// <summary>The render endpoint whose store links to <paramref name="instance"/> ("{1}.&lt;instance id&gt;").</summary>
    private static string? FindEndpoint(string instance)
    {
        using var root = Registry.LocalMachine.OpenSubKey(RenderKey);
        if (root is null) return null;
        string want = "{1}." + instance;
        foreach (var name in root.GetSubKeyNames())
        {
            // A removed devnode's endpoint lingers as "not present" (0x4) and a new devnode can get the same instance id:
            // matching it skipped binding the new devnode's driver, so it never got an endpoint (2026-11-02).
            using (var endpointKey = root.OpenSubKey(name))
                if (endpointKey?.GetValue("DeviceState") is int state && (state & 0x4) != 0) continue;
            using var props = root.OpenSubKey(name + @"\Properties");
            if (props?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},2") is string link && link.Equals(want, StringComparison.OrdinalIgnoreCase))
                return "{0.0.0.00000000}." + name;
        }
        return null;
    }

    /// <summary>Steam's installed SteamStreamingSpeakers INF (oemNN.inf), from a devnode using it or the INF folder.</summary>
    private static string? FindSteamInf()
    {
        if (Devnodes.InfOfHardwareId(MediaClass, SteamHardwareId) is { } inf) return inf;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (var file in Directory.EnumerateFiles(folder, "oem*.inf"))
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                var text = bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
                if (text.Contains(SteamHardwareId, StringComparison.OrdinalIgnoreCase)) return file;
            }
            catch (IOException) { }
        }
        return null;
    }

    // ---- stamping ----------------------------------------------------------------------------

    internal sealed record StampItem(string Label, Guid Format, int Id, object Value);

    // 4 ch / 48 kHz WAVEFORMATEXTENSIBLE, channel mask 0x33 (FL FR BL BR): 16-bit PCM and 32-bit float.
    private static readonly byte[] Pcm16 =
    [
        0xFE, 0xFF, 0x04, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x00, 0xDC, 0x05, 0x00, 0x08, 0x00, 0x10, 0x00, 0x16, 0x00, 0x10, 0x00,
        0x33, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
    ];
    private static readonly byte[] Float32 =
    [
        0xFE, 0xFF, 0x04, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x00, 0xB8, 0x0B, 0x00, 0x10, 0x00, 0x20, 0x00, 0x16, 0x00, 0x20, 0x00,
        0x33, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
    ];

    internal static readonly StampItem[] Stamps =
    [
        new("description", new("a45c254e-df1c-4efd-8020-67d146a850e0"), 2, "Wireless Controller"),
        new("device name", new("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6, "DualSense Wireless Controller"),
        new("container", new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2, VirtualPad.ContainerId),
        new("device format", new("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0, Pcm16),
        new("mix format 2", new("3d6e1656-2e50-4c4c-8d85-d0acae3c6c68"), 2, Float32),
        new("mix format 3", new("3d6e1656-2e50-4c4c-8d85-d0acae3c6c68"), 3, Float32),
        new("host format", new("e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04"), 0, Float32),
    ];

    /// <summary>Writes what isn't served yet: through the property store first, the registry for what it refuses.</summary>
    private static void Stamp(string endpoint)
    {
        var pending = Stamps.Where(s => !Audio.Served(endpoint, s)).ToList();
        if (pending.Count == 0) return;
        var refused = Audio.Write(endpoint, pending);
        if (refused.Count > 0) RegistryStamp(endpoint, refused);
    }

    /// <summary>
    /// MMDevices' Properties keys deny writes even to SYSTEM, which owns them: as owner it may change the DACL, so give
    /// SYSTEM full control first, then write the serialized values (VT_CLSID / VT_BLOB as an 8-byte header + payload).
    /// </summary>
    private static void RegistryStamp(string endpoint, List<StampItem> items)
    {
        string guid = endpoint[(endpoint.LastIndexOf('{'))..];
        string path = $@"{RenderKey}\{guid}\Properties";
        using (var key = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryRights.ReadPermissions | RegistryRights.ChangePermissions) ?? throw new IOException($"no {path}"))
        {
            var acl = key.GetAccessControl();
            acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            key.SetAccessControl(acl);
        }
        using var writable = Registry.LocalMachine.OpenSubKey(path, writable: true) ?? throw new IOException($"can't write {path}");
        foreach (var s in items)
        {
            string name = $"{{{s.Format}}},{s.Id}";
            switch (s.Value)
            {
                case string text: writable.SetValue(name, text, RegistryValueKind.String); break;
                case Guid g: writable.SetValue(name, (byte[])[0x48, 0, 0, 0, 1, 0, 0, 0, .. g.ToByteArray()], RegistryValueKind.Binary); break;
                case byte[] blob: writable.SetValue(name, (byte[])[0x41, 0, 0, 0, 1, 0, 0, 0, .. blob], RegistryValueKind.Binary); break;
            }
        }
        Log.Write($"speaker endpoint: stamped through the registry: {string.Join(", ", items.Select(i => i.Label))}");
    }

    private static void RestartAudio()
    {
        try
        {
            using var audio = new ServiceController("Audiosrv");
            using var builder = new ServiceController("AudioEndpointBuilder");
            if (audio.Status != ServiceControllerStatus.Stopped) { audio.Stop(); audio.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); }
            if (builder.Status != ServiceControllerStatus.Stopped) { builder.Stop(); builder.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); }
            builder.Start(); builder.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            audio.Start(); audio.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            Thread.Sleep(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException) { Log.Write($"audio restart: {ex.Message}"); }
    }

    /// <summary>Removes the speaker devnode (uninstall).</summary>
    public static void RemoveDevnode()
    {
        if (Devnodes.FindMarked(MediaClass, MarkerValue, 0) is { } instance)
        {
            var (exit, output) = Setup.Run("pnputil.exe", $"/remove-device \"{instance}\"");
            Log.Write($"speaker devnode {instance} removed: {exit} {output.Trim()}");
        }
    }
}
