using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace HapticLink.Service;

/// <summary>
/// One-time setup and removal, run elevated by the HapticLink app (one admin prompt each).
/// The app ships a "service" folder (this exe, HapticLinkPadHost.dll, driver\HapticLinkPad.inf/.dll/.cat/.cer). Setup
/// copies it to %ProgramFiles%\HapticLink\Service (admins-only: a SYSTEM service runs from it; also outside the app's
/// per-user folder that updates replace), trusts the driver's certificate, adds the driver to Windows' driver store,
/// puts the service on HidHide's allowed list, adds Steam's speaker driver when Steam has it, and
/// installs + starts the service.
/// </summary>
internal static class Setup
{
    public const string ServiceName = "HapticLink";
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HapticLink", "Service");
    private static string ServiceExe => Path.Combine(InstallDir, "HapticLink.Service.exe");

    /// <summary>
    /// The steps, in order, as the app's setup window shows them. With <paramref name="progressFile"/> each step's start and
    /// end is appended there as "step|state|message" (state: run, ok, fail, skip), which the app reads while this runs
    /// elevated: an elevated process started with ShellExecute can't hand its output back otherwise.
    /// HidHide and Steam are the user's to install (the app shows whether they are); setup only sets up what's there.
    /// </summary>
    public static int Install(string? progressFile = null)
    {
        Log.Write("setup: starting");
        var progress = new Progress(progressFile);

        // 1. HapticLink's files and its virtual controller driver.
        progress.Start("driver");
        StopService();
        CopyFiles(AppContext.BaseDirectory, InstallDir);
        var cer = new X509Certificate2(Path.Combine(InstallDir, "driver", "HapticLinkPad.cer"));
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(cer);
        }
        Log.Write($"setup: trusted the driver certificate {cer.Thumbprint}");
        var (exit, output) = Run("pnputil.exe", $"/add-driver \"{Path.Combine(InstallDir, "driver", "HapticLinkPad.inf")}\" /install");
        if (exit != 0 && exit != 259)
        {
            Log.Write($"setup: pnputil failed ({exit}): {output}");
            return progress.Fail("driver", $"Windows didn't accept the driver (pnputil {exit})", 3);
        }
        Log.Write("setup: driver added to the driver store");
        progress.Ok("driver", "Installed");

        // 2. HidHide: let the service see the controller it hides, and check HidHide answers.
        progress.Start("hidhide");
        if (HidHide.FindCli() is not { } cli) progress.Skip("hidhide", "HidHide isn't installed");
        else
        {
            var (regExit, regOutput) = HidHide.RunCli(cli, $"--app-reg \"{ServiceExe}\"");
            var (stateExit, _) = HidHide.RunCli(cli, "--cloak-state");
            if (regExit == 0 && stateExit == 0) progress.Ok("hidhide", "HapticLink may use the controller HidHide hides");
            else
            {
                Log.Write($"setup: HidHide didn't answer ({regExit}/{stateExit}): {regOutput.Trim()}");
                progress.Fail("hidhide", "HidHide didn't answer: restart Windows, then run setup again");
            }
        }

        // 3. The controller speaker haptics go through: Steam's driver, added from Steam's own files if Remote Play never did.
        progress.Start("speaker");
        if (AudioDevices.FindSteamInf(@"ROOT\SteamStreamingSpeakers") is not null) progress.Ok("speaker", "Steam's speaker driver is ready");
        else if (SteamSpeakerDriver.TryAdd() is { } why) progress.Fail("speaker", Capitalize(why));
        else progress.Ok("speaker", "Added Steam's speaker driver from your Steam folder");

        // 4. The service, which does the work while the tray app runs.
        progress.Start("service");
        bool exists = ServiceController.GetServices().Any(s => s.ServiceName == ServiceName);
        string binPath = $"\\\"{ServiceExe}\\\" service";
        (exit, output) = exists
            ? Run("sc.exe", $"config {ServiceName} binPath= \"{binPath}\" start= auto")
            : Run("sc.exe", $"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"HapticLink for DualSense\"");
        if (exit != 0)
        {
            Log.Write($"setup: sc failed ({exit}): {output}");
            return progress.Fail("service", $"Windows didn't accept the service (sc {exit})", 4);
        }
        Run("sc.exe", $"description {ServiceName} \"Gives games a virtual wired DualSense driven by your DualSense over Bluetooth, with haptics, adaptive triggers, rumble and lights.\"");
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000//");
        try
        {
            using var service = new ServiceController(ServiceName);
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            Log.Write($"setup: the service didn't start: {ex.Message}");
            return progress.Fail("service", "The service didn't start; details in %ProgramData%\\HapticLink\\service.log", 5);
        }
        Log.Write("setup: done, service running");
        progress.Ok("service", "Running");
        return 0;
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Appends "step|state|message" lines for the app's setup window (nothing without a file).</summary>
    private sealed class Progress(string? file)
    {
        public void Start(string step) => Write(step, "run", "");
        public void Ok(string step, string message) => Write(step, "ok", message);
        public void Skip(string step, string message) => Write(step, "skip", message);
        public void Fail(string step, string message) => Write(step, "fail", message);
        public int Fail(string step, string message, int exitCode) { Write(step, "fail", message); return exitCode; }

        private void Write(string step, string state, string message)
        {
            if (file is null) return;
            try { File.AppendAllText(file, $"{step}|{state}|{message.Replace('|', '/').Replace('\n', ' ')}\n"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"setup: progress file: {ex.Message}"); }
        }
    }

    public static int Remove()
    {
        Log.Write("remove: starting");
        StopService();
        if (ServiceController.GetServices().Any(s => s.ServiceName == ServiceName)) Run("sc.exe", $"delete {ServiceName}");
        Bridge.UndoLeftovers();
        SpeakerEndpoint.RemoveDevnode();
        if (HidHide.FindCli() is { } cli) HidHide.RunCli(cli, $"--app-unreg \"{ServiceExe}\"");

        var (_, drivers) = Run("pnputil.exe", "/enum-drivers");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(drivers,
                     @"(?m)^.*?:\s+(oem\d+\.inf)\s*\r?\n.*?:\s+hapticlinkpad\.inf", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            Run("pnputil.exe", $"/delete-driver {m.Groups[1].Value} /uninstall /force");

        foreach (var dir in new[] { InstallDir, AppContext.BaseDirectory })
        {
            var cerPath = Path.Combine(dir, "driver", "HapticLinkPad.cer");
            if (!File.Exists(cerPath)) continue;
            var cer = new X509Certificate2(cerPath);
            foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                // This certificate and any earlier one with the same name: the release certificate was replaced once
                // (2026-11-02), and setup leaves the old one trusted since installed drivers may still carry its signature.
                foreach (var found in store.Certificates.Find(X509FindType.FindByThumbprint, cer.Thumbprint, false)
                             .Concat(store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, cer.Subject, false)))
                    store.Remove(found);
            }
            break;
        }
        try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\HapticLink", throwOnMissingSubKey: false); } catch (UnauthorizedAccessException) { }

        // Running from the folder being deleted: let cmd delete it once we've exited. Otherwise delete it now: a delayed
        // delete once wiped the files a setup run straight afterwards had copied (2026-11-02).
        var root = Path.GetDirectoryName(InstallDir)!;
        bool runningFromIt = AppContext.BaseDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(root))
        {
            if (runningFromIt)
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rd /s /q \"{root}\"") { CreateNoWindow = true, UseShellExecute = false });
            else
                try { Directory.Delete(root, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"remove: couldn't delete {root}: {ex.Message}"); }
        }
        Log.Write("remove: done");
        return 0;
    }

    private static void StopService()
    {
        if (!ServiceController.GetServices().Any(s => s.ServiceName == ServiceName)) return;
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Stopped) return;
        try
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException) { Log.Write($"stopping the service: {ex.Message}"); }
        // "Stopped" is reported a moment before the process exits; copying over its exe fails until it has.
        string installed = Path.Combine(InstallDir, "HapticLink.Service.exe");
        foreach (var p in Process.GetProcessesByName("HapticLink.Service").Where(p => p.Id != Environment.ProcessId))
        {
            try { if (string.Equals(p.MainModule?.FileName, installed, StringComparison.OrdinalIgnoreCase)) p.WaitForExit(10_000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Copies the service folder into an admins-only folder (SYSTEM and Administrators full, everyone else read).</summary>
    private static void CopyFiles(string source, string target)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        var dir = new DirectoryInfo(target);
        if (dir.Exists) dir.SetAccessControl(security); else dir.Create(security);

        if (Path.GetFullPath(source).TrimEnd('\\').Equals(Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var to = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            for (int attempt = 1; ; attempt++)
            {
                try { File.Copy(file, to, overwrite: true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(250); } // a file still held for a moment
            }
        }
        Log.Write($"setup: copied {source} to {target}");
    }

    public static (int ExitCode, string Output) Run(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, output);
    }
}
