using static HapticLink.Service.AudioDevices;

namespace HapticLink.Service;

/// <summary>
/// The virtual pad's speaker: games play DualSense haptics as 4-channel audio (channels 3-4 drive the voice coils) into
/// the sound device that shares the pad's container id. Windows has no such device for a software pad, so, like
/// punktfunk (pf-audio/src/windows/pad_endpoint.rs, MIT OR Apache-2.0), we add one more instance of Valve's signed
/// "Steam Streaming Speakers" driver (installed by Steam) and stamp it as the pad's speaker: name "DualSense Controller (HapticLink)",
/// the pad's container id, 4 ch / 48 kHz. The app records it (WASAPI loopback) and relays channels 3-4 to the real pad.
/// The devnode is permanent (made once, removed by uninstall); the endpoint is hidden while no pad runs, because an
/// idle visible DualSense speaker can stall games.
/// </summary>
internal sealed class SpeakerEndpoint
{
    private const string SteamHardwareId = @"ROOT\SteamStreamingSpeakers";
    private const string MarkerValue = "HapticLinkPadIndex";

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
                var id = Ensure(out var problem);
                _problem = problem;
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
        string? before = Audio.DefaultId(capture: false);
        Audio.SetVisible(id, true);
        for (int i = 0; i < 20 && Audio.State(id) != 1; i++) Thread.Sleep(100);
        Audio.EnsureFormat(id, Pcm16, Float32);
        Audio.KeepDefault(before, id, capture: false);
        return id;
    }

    public void Hide()
    {
        Task<string?>? ensure;
        lock (_gate) ensure = _ensure;
        if (ensure is { IsCompletedSuccessfully: true, Result: { } id }) Audio.SetVisible(id, false);
    }

    /// <summary>
    /// Finds or makes the devnode, binds Steam's driver (adding it from Steam's own files when Remote Play never installed
    /// it), stamps the endpoint; returns its id, or null with the reason.
    /// </summary>
    private static string? Ensure(out string? problem)
    {
        problem = null;
        string? inf = FindSteamInf(SteamHardwareId);
        if (inf is null)
        {
            Log.Write("speaker endpoint: Steam's streaming speaker driver isn't installed; adding it from Steam's files");
            var why = SteamSpeakerDriver.TryAdd();
            if (why is null) inf = FindSteamInf(SteamHardwareId);
            if (inf is null)
            {
                problem = why ?? "Steam's streaming speaker driver isn't installed";
                Log.Write($"speaker endpoint: {problem}; no haptics");
                return null;
            }
            // Devnodes left without it (ours, Steam's, other apps') get it back before ours is looked for.
            Devnodes.BindDriver(SteamHardwareId, inf);
        }
        string? before = Audio.DefaultId(capture: false);
        string instance = Devnodes.FindMarked(MediaClass, MarkerValue, 0) ?? Readopt() ?? Devnodes.CreateRootDevnode(MediaClass, "MEDIA",
            "HapticLink Pad Audio", SteamHardwareId, MarkerValue, 0);
        Remember(instance);
        string? endpoint = FindEndpoint(instance, capture: false);
        if (endpoint is null)
        {
            Devnodes.BindDriver(SteamHardwareId, inf);
            for (int i = 0; i < 40 && (endpoint = FindEndpoint(instance, capture: false)) is null; i++) Thread.Sleep(250);
            if (endpoint is null) throw new IOException($"no sound endpoint appeared for {instance}");
        }

        bool served = StampAll(endpoint, Stamps, "speaker endpoint");
        if (Devnodes.SyncDescription(@"SWD\MMDEVAPI\" + endpoint) is { } old)
            Log.Write($"speaker endpoint: Sound settings' name brought up to date (was \"{old}\")");
        Audio.SetVisible(endpoint, false);
        Audio.KeepDefault(before, endpoint, capture: false);
        Log.Write($"speaker endpoint ready: {endpoint} ({instance}), stamps served: {served}");
        return endpoint;
    }

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
        // The name in Sound settings (owner's choice, 2026-11-02; was "Wireless Controller", the real pad's name, which some
        // games may look for by name: if one loses haptics or speaker sound, this is the first thing to suspect).
        Description("DualSense Controller (HapticLink)"),
        // The second line in Sound settings: Steam's own driver name (owner's choice, 2026-11-02), so it's clear whose driver
        // this is. An earlier stamp wrote "DualSense Wireless Controller" here; stamping this puts the original back.
        DeviceName("Steam Streaming Speakers"),
        new("container", new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2, VirtualPad.ContainerId),
        DeviceFormat(Pcm16),
        MixFormat2(Float32),
        MixFormat3(Float32),
        HostFormat(Float32),
    ];

    /// <summary>Removes the speaker devnode (uninstall).</summary>
    public static void RemoveDevnode()
    {
        if (Devnodes.FindMarked(MediaClass, MarkerValue, 0) is null) Readopt();
        AudioDevices.RemoveDevnode(MarkerValue, "speaker");
    }

    // The devnode's instance id is kept in HapticLink's own key too: the marker lives in Device Parameters, which go when
    // Steam's driver package is uninstalled (Steam uninstalled, or removed by hand), and without it a new devnode was made
    // while the old one stayed behind as a visible plain "Speakers" device (2026-11-02).
    private const string Key = @"SOFTWARE\HapticLink";
    private const string InstanceValue = "SpeakerDevnode";

    private static string? Readopt()
    {
        string? id;
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key)) id = key?.GetValue(InstanceValue) as string;
        if (id is null || !Devnodes.Remark(id, SteamHardwareId, MarkerValue, 0)) return null;
        Log.Write($"speaker endpoint: marked {id} as ours again (its driver had been uninstalled)");
        return id;
    }

    private static void Remember(string instance)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(Key);
        if (key.GetValue(InstanceValue) as string != instance) key.SetValue(InstanceValue, instance);
    }
}
