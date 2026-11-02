using System.Diagnostics;
using System.Security.Cryptography;

namespace HapticLink.Service;

/// <summary>
/// Installs HidHide (Nefarius, MIT) during setup when the user agreed to it in the app: downloads the pinned release from
/// Nefarius's GitHub, checks its SHA-256, and runs it silently. The installer is an Advanced Installer exe around an MSI
/// (signed by Nefarius Software Solutions e.U.), so <c>/exenoui /qn /norestart</c> installs without any window. No reboot
/// is needed for the pad: the bridge restarts it, which attaches HidHide's filter (and says so when it didn't).
/// </summary>
internal static class HidHideInstaller
{
    private const string Url = "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe";
    private const string Sha256 = "F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6";

    /// <summary>Installs HidHide unless it's there already. Never throws: HapticLink works without it (games see both pads).</summary>
    public static void EnsureInstalled()
    {
        if (HidHide.FindCli() is not null) { Log.Write("setup: HidHide is already installed"); return; }
        // Next to the service, in %ProgramFiles%: only admins can write there, so nobody can swap the file after the check.
        var folder = Path.Combine(Path.GetDirectoryName(Setup.InstallDir)!, "HidHide-setup");
        var file = Path.Combine(folder, Path.GetFileName(Url));
        try
        {
            Log.Write("setup: downloading HidHide");
            byte[] data;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                data = http.GetByteArrayAsync(Url).GetAwaiter().GetResult();
            var hash = Convert.ToHexString(SHA256.HashData(data));
            if (hash != Sha256) { Log.Write($"setup: HidHide download didn't match (SHA-256 {hash}); not installing it"); return; }
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(file, data);

            using var p = Process.Start(new ProcessStartInfo(file, "/exenoui /qn /norestart") { UseShellExecute = false, CreateNoWindow = true })!;
            if (!p.WaitForExit(TimeSpan.FromMinutes(5))) { Log.Write("setup: HidHide's installer didn't finish in 5 minutes"); return; }
            // 3010/1641: installed, Windows wants a restart (the pad itself doesn't need one).
            Log.Write(p.ExitCode is 0 or 3010 or 1641 && HidHide.FindCli() is not null
                ? $"setup: HidHide installed (exit {p.ExitCode})"
                : $"setup: HidHide's installer failed (exit {p.ExitCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Write($"setup: couldn't install HidHide ({ex.Message})");
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
