using System.Diagnostics;
using Microsoft.Win32;

namespace HapticLink.Service;

/// <summary>HidHide (the driver DS4Windows and DSX use to hide controllers from games) through its command-line tool.</summary>
internal static class HidHide
{
    private const string RegistryKey = @"SOFTWARE\Nefarius Software Solutions e.U.\HidHide";

    /// <summary>Full path of HidHideCLI.exe, or null when HidHide isn't installed.</summary>
    public static string? FindCli()
    {
        string? root = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
            root = key?.GetValue("Path") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide");
        var cli = Path.Combine(root, "x64", "HidHideCLI.exe");
        return File.Exists(cli) ? cli : null;
    }

    /// <summary>
    /// Runs HidHideCLI with no window. Its stdin is closed right away: with no console attached it would otherwise wait
    /// for more commands.
    /// </summary>
    public static (int ExitCode, string Output) RunCli(string cli, string arguments, int timeoutMs = 10_000)
    {
        var psi = new ProcessStartInfo(cli, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return (-1, "timed out");
            }
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message); }
    }
}
