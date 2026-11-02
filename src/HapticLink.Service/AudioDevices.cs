using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace HapticLink.Service;

/// <summary>
/// What the pad's sound devices (<see cref="SpeakerEndpoint"/>; a microphone on the mic-wip branch) share: one more instance of a
/// signed Steam streaming audio driver on a ROOT\MEDIA devnode we mark as ours, and its endpoints stamped with our names
/// and formats (after punktfunk's pf-audio/src/windows, MIT OR Apache-2.0).
/// </summary>
internal static class AudioDevices
{
    public static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string CaptureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    public const string RenderPrefix = "{0.0.0.00000000}.", CapturePrefix = "{0.0.1.00000000}.";

    internal sealed record StampItem(string Label, Guid Format, int Id, object Value);

    public static StampItem Description(string text) => new("description", new("a45c254e-df1c-4efd-8020-67d146a850e0"), 2, text);
    public static StampItem DeviceName(string text) => new("device name", new("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6, text);
    public static StampItem DeviceFormat(byte[] format) => new("device format", new("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0, format);
    public static StampItem MixFormat2(byte[] format) => new("mix format 2", new("3d6e1656-2e50-4c4c-8d85-d0acae3c6c68"), 2, format);
    public static StampItem MixFormat3(byte[] format) => new("mix format 3", new("3d6e1656-2e50-4c4c-8d85-d0acae3c6c68"), 3, format);
    public static StampItem HostFormat(byte[] format) => new("host format", new("e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04"), 0, format);

    /// <summary>Steam's installed INF (oemNN.inf) for a hardware id: from a devnode using it, else by scanning the INF folder.</summary>
    public static string? FindSteamInf(string hardwareId)
    {
        if (Devnodes.InfOfHardwareId(MediaClass, hardwareId) is { } inf) return inf;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (var file in Directory.EnumerateFiles(folder, "oem*.inf"))
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                var text = bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
                // Whole id only, so a longer id that starts the same doesn't match.
                int at = text.IndexOf(hardwareId, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && (at + hardwareId.Length == text.Length || !char.IsLetterOrDigit(text[at + hardwareId.Length]))) return file;
            }
            catch (IOException) { }
        }
        return null;
    }

    /// <summary>The render or capture endpoint whose store links to <paramref name="instance"/> ("{1}.&lt;instance id&gt;").</summary>
    public static string? FindEndpoint(string instance, bool capture)
    {
        using var root = Registry.LocalMachine.OpenSubKey(capture ? CaptureKey : RenderKey);
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
                return (capture ? CapturePrefix : RenderPrefix) + name;
        }
        return null;
    }

    /// <summary>
    /// Stamps until the property store serves every item (the endpoint builder reverts fresh writes now and then, so
    /// check after it settles), restarting Windows Audio once if they're stored but not served. True when served.
    /// </summary>
    public static bool StampAll(string endpoint, IReadOnlyList<StampItem> items, string what)
    {
        bool served = false;
        for (int attempt = 0; attempt < 5 && !served; attempt++)
        {
            Stamp(endpoint, items, what);
            Thread.Sleep(1200);
            served = items.All(s => Audio.Served(endpoint, s));
        }
        if (!served)
        {
            Log.Write($"{what}: stamps stored but not served; restarting Windows Audio once");
            RestartAudio();
            served = items.All(s => Audio.Served(endpoint, s));
        }
        return served;
    }

    /// <summary>Writes what isn't served yet: through the property store first, the registry for what it refuses.</summary>
    private static void Stamp(string endpoint, IReadOnlyList<StampItem> items, string what)
    {
        var pending = items.Where(s => !Audio.Served(endpoint, s)).ToList();
        if (pending.Count == 0) return;
        var refused = Audio.Write(endpoint, pending, what);
        if (refused.Count > 0) RegistryStamp(endpoint, refused, what);
    }

    /// <summary>
    /// MMDevices' Properties keys deny writes even to SYSTEM, which owns them: as owner it may change the DACL, so give
    /// SYSTEM full control first, then write the serialized values (VT_CLSID / VT_BLOB as an 8-byte header + payload).
    /// </summary>
    private static void RegistryStamp(string endpoint, List<StampItem> items, string what)
    {
        string guid = endpoint[(endpoint.LastIndexOf('{'))..];
        string path = $@"{(endpoint.StartsWith(CapturePrefix, StringComparison.Ordinal) ? CaptureKey : RenderKey)}\{guid}\Properties";
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
        Log.Write($"{what}: stamped through the registry: {string.Join(", ", items.Select(i => i.Label))}");
    }

    public static void RestartAudio()
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

    /// <summary>Removes our devnode with this marker (uninstall).</summary>
    public static void RemoveDevnode(string marker, string what)
    {
        if (Devnodes.FindMarked(MediaClass, marker, 0) is { } instance)
        {
            var (exit, output) = Setup.Run("pnputil.exe", $"/remove-device \"{instance}\"");
            Log.Write($"{what} devnode {instance} removed: {exit} {output.Trim()}");
        }
    }
}
