using System.IO.Pipes;
using System.Text;

namespace HapticLink.App;

/// <summary>What the service says about itself (STATUS).</summary>
/// <param name="State">off, waiting, starting, on, error.</param>
public sealed record ServiceStatus(string State, bool Enabled, bool Haptics, string ControllerMac, string Message);

/// <summary>
/// The service's control pipe "HapticLink" (one line per command; see HapticLinkService.Control in the service).
/// Each call connects, asks and hangs up: a handful of calls every few seconds.
/// </summary>
public static class ServiceClient
{
    private const string PipeName = "HapticLink";

    /// <summary>The service's version, or null when it isn't running.</summary>
    public static Version? Hello() =>
        Ask("HELLO HapticLink") is { } reply && Version.TryParse(reply.Split(' ')[0], out var v) ? v : null;

    public static ServiceStatus? Status()
    {
        if (Ask("STATUS") is not { } reply) return null;
        var f = reply.Split('|', 5);
        return f.Length == 5 ? new ServiceStatus(f[0], f[1] == "1", f[2] == "1", f[3], f[4]) : null;
    }

    public static bool SetEnabled(bool enabled) => Ask(enabled ? "ENABLE" : "DISABLE") is not null;

    /// <summary>The controller speaker switch, or null (service not running, or older than 1.1.0).</summary>
    public static bool? SpeakerOn() => Ask("FEATURES") is { } reply ? reply.Split(' ').Contains("speaker=1") : null;

    /// <summary>Turns a feature ("speaker") on or off.</summary>
    public static bool SetFeature(string name, bool on) => Ask($"SET {name} {(on ? 1 : 0)}") is not null;

    /// <summary>
    /// Holds an ATTACH connection open for as long as this app runs: the service only bridges while the app is running,
    /// so exiting (or a crash) turns everything off. Reconnects every 2 s when the service restarts or isn't there yet.
    /// </summary>
    public static IDisposable HoldAttached()
    {
        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.ConnectAsync(2000, cts.Token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                    writer.WriteLine("ATTACH");
                    reader.ReadLine();
                    // Nothing more is said: the read below only returns when the service goes away or the app exits.
                    var buffer = new byte[1];
                    while (pipe.ReadAsync(buffer, cts.Token).AsTask().GetAwaiter().GetResult() > 0) { }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { }
                cts.Token.WaitHandle.WaitOne(2000);
            }
        }) { IsBackground = true, Name = "service attach" };
        thread.Start();
        return cts;
    }

    private static string? Ask(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(500);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            writer.WriteLine(command);
            var reply = reader.ReadLine();
            return reply is not null && reply.StartsWith("OK", StringComparison.Ordinal) ? (reply.Length > 3 ? reply[3..] : "") : null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return null; }
    }
}
