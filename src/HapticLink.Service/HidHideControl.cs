
namespace HapticLink.Service;

/// <summary>
/// Hides the real controller from games with HidHide while its virtual twin runs (else games see two DualSense pads,
/// may drive both, and their commands fight: the triggers felt wrong until it was hidden, 2026-11-01). Only what this
/// session changed is undone: a device that was already on HidHide's list stays there, and cloaking that was already
/// on (DSX turns it on whenever it runs) stays on.
/// </summary>
internal static class HidHideControl
{
    internal sealed record Undo(string InstanceId, bool Unhide, bool CloakOff)
    {
        public string Serialize() => $"{InstanceId}\n{(Unhide ? 1 : 0)}\n{(CloakOff ? 1 : 0)}\n";

        public static Undo? Deserialize(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.TrimEntries);
            return lines.Length >= 3 && lines[0].Length > 0 ? new Undo(lines[0], lines[1] == "1", lines[2] == "1") : null;
        }
    }

    /// <summary>
    /// Hides <paramref name="instanceId"/> (the real pad's HID node) and turns cloaking on, keeping
    /// <paramref name="allowedExes"/> (this service, which reads the real pad, and apps such as PeripheralHub that show its
    /// battery) on the allowed list.
    /// </summary>
    public static Undo Hide(string instanceId, IEnumerable<string> allowedExes)
    {
        if (HidHide.FindCli() is not { } cli)
        {
            Log.Write("HidHide isn't installed: games will see the real controller too");
            return new Undo(instanceId, false, false);
        }
        var (exit, state) = HidHide.RunCli(cli, "--cloak-state --app-list --dev-list");
        if (exit != 0) throw new IOException($"couldn't read HidHide's settings ({state.Trim()})");
        var lines = state.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var exe in allowedExes) Allow(cli, lines, exe);

        bool listed = lines.Any(l => l.StartsWith("--dev-hide ", StringComparison.Ordinal) &&
                                     string.Equals(l[11..].Trim('"'), instanceId, StringComparison.OrdinalIgnoreCase));
        if (!listed) Run(cli, $"--dev-hide \"{instanceId}\"");
        bool cloakWasOff = lines.Contains("--cloak-off");
        if (cloakWasOff) Run(cli, "--cloak-on");
        Log.Write($"HidHide: hid {instanceId} (already listed: {listed}, cloaking was {(cloakWasOff ? "off" : "on")})");
        return new Undo(instanceId, !listed, cloakWasOff);
    }

    public static void Restore(Undo undo)
    {
        if (!undo.Unhide && !undo.CloakOff) return;
        if (HidHide.FindCli() is not { } cli) return;
        try
        {
            if (undo.Unhide) Run(cli, $"--dev-unhide \"{undo.InstanceId}\"");
            if (undo.CloakOff) Run(cli, "--cloak-off");
            Log.Write($"HidHide: restored ({undo.InstanceId})");
        }
        catch (IOException ex) { Log.Write($"HidHide restore failed: {ex.Message}"); }
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
