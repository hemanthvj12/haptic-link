using Microsoft.Win32;

namespace HapticLink.App;

/// <summary>%APPDATA%\HapticLink\logs\app.log (kept under ~1 MB).</summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HapticLink", "logs");
    public static string Version => typeof(AppLog).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void CatchUnhandled() =>
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"unhandled: {e.ExceptionObject}");
}

/// <summary>"Start with Windows": the per-user Run key, pointing at this exe.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HapticLink";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && IsThisExe(command);
        }
    }

    private static bool IsThisExe(string command) =>
        string.Equals(command.Trim().Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void RemoveIfThisExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string command && IsThisExe(command)) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

