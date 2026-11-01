using System.ServiceProcess;

namespace HapticLink.Service;

/// <summary>
/// Entry point. The same exe is:
///   service   the Windows service (what the SCM starts)
///   console   the same in a console window, for development (run elevated)
///   setup     one-time setup, run elevated by the app (see Setup.cs)
///   remove    undoes the setup, run elevated by the app's uninstaller
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "service":
                    ServiceBase.Run(new WindowsService());
                    return 0;
                case "console":
                    using (var service = new HapticLinkService())
                    {
                        service.Start();
                        Console.WriteLine("HapticLink service running; press Enter to stop.");
                        Console.ReadLine();
                    }
                    return 0;
                case "setup":
                    return Setup.Install();
                case "remove":
                    return Setup.Remove();
                default:
                    Console.Error.WriteLine("usage: HapticLink.Service service | console | setup | remove");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"{args.FirstOrDefault()}: unhandled {ex}");
            return 10;
        }
    }
}

/// <summary>The SCM wrapper around <see cref="HapticLinkService"/>.</summary>
internal sealed class WindowsService : ServiceBase
{
    private HapticLinkService? _service;

    public WindowsService() => ServiceName = Setup.ServiceName;

    protected override void OnStart(string[] args)
    {
        _service = new HapticLinkService();
        _service.Start();
    }

    protected override void OnStop()
    {
        _service?.Dispose();
        _service = null;
    }
}

/// <summary>%ProgramData%\HapticLink\service.log (kept under ~1 MB).</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HapticLink");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, "service.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (Environment.UserInteractive) Console.WriteLine(message);
    }
}
