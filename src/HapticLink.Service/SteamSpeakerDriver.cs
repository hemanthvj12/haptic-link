using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

namespace HapticLink.Service;

/// <summary>
/// Adds Valve's "Steam Streaming Speakers" driver to Windows' driver store from the user's own Steam install, for people
/// who have Steam but never used Remote Play (which is when Steam installs it). Steam keeps its drivers in
/// <c>bin\drivers.exe</c> ("Steam Drivers Archive", signed by Valve): a zip written as a stream (each entry deflated and
/// followed by a data descriptor, no central directory), so the entries are found by their local headers and inflated
/// until their deflate stream ends. The driver package is signed by Microsoft for Valve; pnputil checks that signature.
/// HapticLink doesn't ship the driver itself: it's Valve's, so without Steam there's nothing to install it from.
/// </summary>
internal static class SteamSpeakerDriver
{
    private const string Folder = "drivers/Windows10/x64/";
    private static readonly string[] Files = ["SteamStreamingSpeakers.inf", "SteamStreamingSpeakers.sys", "steamstreamingspeakers.cat"];

    /// <summary>Why it couldn't be added (null when it was): shown as the reason there are no haptics.</summary>
    public static string? TryAdd()
    {
        if (FindSteam() is not { } steam) return "Steam isn't installed (haptics use its speaker driver): install Steam and start it once";
        var archive = Path.Combine(steam, "bin", "drivers.exe");
        if (!File.Exists(archive)) return "Steam's driver archive is missing: start Steam once so it finishes installing";

        // Next to the service, in %ProgramFiles%: only admins can write there.
        var target = Path.Combine(Path.GetDirectoryName(Setup.InstallDir)!, "SteamDriver");
        try
        {
            var data = File.ReadAllBytes(archive);
            Directory.CreateDirectory(target);
            foreach (var name in Files)
            {
                if (Extract(data, Folder + name) is not { } bytes) { Log.Write($"Steam driver: {name} not found in {archive}"); return "Steam's speaker driver wasn't found in Steam's files"; }
                File.WriteAllBytes(Path.Combine(target, name), bytes);
            }
            var (exit, output) = Setup.Run("pnputil.exe", $"/add-driver \"{Path.Combine(target, Files[0])}\"");
            if (exit != 0 && exit != 259) { Log.Write($"Steam driver: pnputil failed ({exit}): {output.Trim()}"); return $"Windows refused Steam's speaker driver (pnputil {exit})"; }
            Log.Write($"Steam driver: added Steam Streaming Speakers to the driver store from {archive}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Write($"Steam driver: {ex.Message}");
            return $"couldn't install Steam's speaker driver ({ex.Message})";
        }
        finally
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Steam's folder: its registry entry (64-bit view, then 32-bit), else the default; null when not installed.</summary>
    private static string? FindSteam()
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
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return File.Exists(Path.Combine(fallback, "steam.exe")) ? fallback : null;
    }

    /// <summary>Inflates the entry named <paramref name="name"/> (case-insensitive) from a streamed zip; null when absent.</summary>
    internal static byte[]? Extract(byte[] zip, string name)
    {
        for (int i = 0; i + 30 <= zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B || zip[i + 2] != 3 || zip[i + 3] != 4) continue;
            int method = BitConverter.ToUInt16(zip, i + 8);
            int nameLength = BitConverter.ToUInt16(zip, i + 26), extraLength = BitConverter.ToUInt16(zip, i + 28);
            if (i + 30 + nameLength > zip.Length) break;
            if (!Encoding.ASCII.GetString(zip, i + 30, nameLength).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            int start = i + 30 + nameLength + extraLength;
            using var source = new MemoryStream(zip, start, zip.Length - start, writable: false);
            using var result = new MemoryStream();
            if (method == 8) { using var inflate = new DeflateStream(source, CompressionMode.Decompress); inflate.CopyTo(result); }
            else if (method == 0) return zip.AsSpan(start, (int)BitConverter.ToUInt32(zip, i + 22)).ToArray(); // stored (size in the header)
            else return null;
            return result.ToArray();
        }
        return null;
    }
}
