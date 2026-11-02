using System.Text;
using Microsoft.Win32;

namespace HapticLink.App;

/// <summary>What one of the apps HapticLink needs looks like on this PC.</summary>
internal sealed record Requirement(bool Ok, string Detail);

/// <summary>
/// The two free apps HapticLink needs, checked without admin rights for the setup window. HapticLink doesn't install
/// either (owner, 2026-11-02): the user installs them, setup only sets up what's there.
///   HidHide (Nefarius): hides the real controller, so games use only HapticLink's.
///   Steam: its Streaming Speakers driver is the controller speaker haptics are played into.
/// </summary>
internal static class Requirements
{
    public const string HidHideUrl = "https://github.com/nefarius/HidHide/releases/latest";
    public const string SteamUrl = "https://store.steampowered.com/about/";

    public static Requirement HidHide()
    {
        string? root = null, version = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Nefarius Software Solutions e.U.\HidHide");
            root = key?.GetValue("Path") as string;
            version = key?.GetValue("Version") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide");
        return File.Exists(Path.Combine(root, "x64", "HidHideCLI.exe"))
            ? new(true, version is null ? "Installed" : $"Installed (version {version})")
            : new(false, "Not installed. Without it, games see your controller twice and the triggers feel wrong.");
    }

    public static Requirement Steam()
    {
        if (SteamFolder() is not { } steam)
            return new(false, "Not installed. Haptics play through Steam's speaker driver; you don't need to sign in or keep Steam running.");
        // Steam's drivers come in bin\drivers.exe, which Steam downloads the first time it starts.
        if (!SpeakerDriverInstalled() && !File.Exists(Path.Combine(steam, "bin", "drivers.exe")))
            return new(false, "Installed, but it hasn't finished setting up: start Steam once, then check again.");
        return new(true, "Installed");
    }

    private static string? SteamFolder()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (key?.GetValue("InstallPath") is string path && File.Exists(Path.Combine(path, "steam.exe"))) return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (user?.GetValue("SteamPath") is string path && File.Exists(Path.Combine(path, "steam.exe"))) return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    /// <summary>Steam's speaker driver is in Windows' driver store already (C:\Windows\INF is readable by everyone).</summary>
    private static bool SpeakerDriverInstalled()
    {
        const string id = @"ROOT\SteamStreamingSpeakers";
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"), "oem*.inf"))
            {
                var bytes = File.ReadAllBytes(file);
                var text = bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
                if (text.Contains(id, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }
}
