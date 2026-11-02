
namespace HapticLink.Service;

/// <summary>
/// Hides the real controller from games with HidHide while its virtual twin runs (else games see two DualSense pads,
/// may drive both, and their commands fight: the triggers felt wrong until it was hidden, 2026-11-01). Only what this
/// session changed is undone: a device that was already on HidHide's list stays there, and cloaking that was already
/// on (DSX turns it on whenever it runs) stays on.
/// </summary>
internal static class HidHideControl
{
    /// <summary>What this session changed: the device put on the list, cloaking turned on, inverse mode turned off.</summary>
    internal sealed record Undo(string InstanceId, bool Unhide, bool CloakOff, bool InverseOn = false)
    {
        /// <summary>Whether the pad's visibility changed (whatever already had it open keeps seeing it until it restarts).</summary>
        public bool Changed => Unhide || CloakOff || InverseOn;

        public string Serialize() => $"{InstanceId}\n{(Unhide ? 1 : 0)}\n{(CloakOff ? 1 : 0)}\n{(InverseOn ? 1 : 0)}\n";

        public static Undo? Deserialize(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.TrimEntries);
            return lines.Length >= 3 && lines[0].Length > 0
                ? new Undo(lines[0], lines[1] == "1", lines[2] == "1", lines.Length > 3 && lines[3] == "1")
                : null;
        }
    }

    /// <summary>The outcome of <see cref="Hide"/>: what to undo, and why games still see the pad (null when it's hidden).</summary>
    internal sealed record Result(Undo Undo, string? Problem);

    /// <summary>
    /// Hides <paramref name="instanceId"/> (the real pad's HID node) and turns cloaking on, keeping
    /// <paramref name="allowedExes"/> (this service, which reads the real pad, and apps such as PeripheralHub that show its
    /// battery) on the allowed list. In inverse mode the list names the apps that may NOT see hidden devices, so games
    /// would still see the pad: inverse mode is turned off for the session. Hiding only stops new opens: whatever already
    /// has the pad open (Windows' own game controller input, which browsers and most games read, opens it as it connects)
    /// keeps it until the pad restarts, which the caller does when <see cref="Undo.Changed"/> (GitHub issue #1: two
    /// controllers in a gamepad tester, the real one frozen).
    /// </summary>
    public static Result Hide(string instanceId, IEnumerable<string> allowedExes)
    {
        if (HidHide.FindCli() is not { } cli)
        {
            Log.Write("HidHide isn't installed: games will see the real controller too");
            return new Result(new Undo(instanceId, false, false), "HidHide isn't installed, so games see two controllers (get it from github.com/nefarius/HidHide)");
        }
        var (exit, state) = HidHide.RunCli(cli, "--cloak-state --app-list --dev-list");
        if (exit != 0) throw new IOException($"couldn't read HidHide's settings ({state.Trim()})");
        var lines = state.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // Asked on its own: a version from before inverse mode would reject the whole line.
        bool inverse = HidHide.RunCli(cli, "--inv-state").Output.Contains("--inv-on", StringComparison.Ordinal);

        if (inverse) Run(cli, "--inv-off");
        foreach (var exe in allowedExes) Allow(cli, lines, exe);

        bool listed = lines.Any(l => l.StartsWith("--dev-hide ", StringComparison.Ordinal) &&
                                     string.Equals(l[11..].Trim('"'), instanceId, StringComparison.OrdinalIgnoreCase));
        if (!listed) Run(cli, $"--dev-hide \"{instanceId}\"");
        bool cloakWasOff = lines.Contains("--cloak-off");
        if (cloakWasOff) Run(cli, "--cloak-on");
        Log.Write($"HidHide: hid {instanceId} (already listed: {listed}, cloaking was {(cloakWasOff ? "off" : "on")}{(inverse ? ", inverse mode was on" : "")})");
        return new Result(new Undo(instanceId, !listed, cloakWasOff, inverse), null);
    }

    /// <summary>
    /// Undoes what <see cref="Hide"/> changed and restarts the pad when it's still connected: Windows' game controller
    /// input couldn't open it while hidden, so games wouldn't see it again until it reconnects.
    /// </summary>
    public static void Restore(Undo undo)
    {
        if (!undo.Changed) return;
        if (HidHide.FindCli() is not { } cli) return;
        try
        {
            if (undo.Unhide) Run(cli, $"--dev-unhide \"{undo.InstanceId}\"");
            if (undo.CloakOff) Run(cli, "--cloak-off");
            if (undo.InverseOn) Run(cli, "--inv-on");
            Log.Write($"HidHide: restored ({undo.InstanceId})");
        }
        catch (IOException ex) { Log.Write($"HidHide restore failed: {ex.Message}"); return; }
        if (DeviceNode.Restart(undo.InstanceId) is { } error) Log.Write($"HidHide: controller not restarted ({error})");
        else Log.Write("HidHide: restarted the controller so games see it again");
    }

    /// <summary>Puts an app on HidHide's allowed list (e.g. PeripheralHub connecting while the pad is already hidden).</summary>
    public static void Allow(string exe)
    {
        if (HidHide.FindCli() is not { } cli) return;
        var (exit, state) = HidHide.RunCli(cli, "--app-list");
        if (exit == 0) Allow(cli, state.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), exe);
    }

    private static void Allow(string cli, string[] lines, string exe)
    {
        if (exe.Length == 0 || lines.Any(l => l.StartsWith("--app-reg ", StringComparison.Ordinal) &&
                                              string.Equals(l[10..].Trim('"'), exe, StringComparison.OrdinalIgnoreCase))) return;
        Run(cli, $"--app-reg \"{exe}\"");
        Log.Write($"HidHide: allowed {exe}");
    }

    private static void Run(string cli, string arguments)
    {
        var (exit, output) = HidHide.RunCli(cli, arguments);
        if (exit != 0) throw new IOException($"HidHide {arguments.Split(' ')[0]} failed ({output.Trim()})");
    }
}
