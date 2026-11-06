namespace HapticLink.App;

/// <summary>
/// HapticLink.Setup.exe: the setup window, started by the tray (HapticLink.exe) when it's needed, or by hand.
///   (no arguments)             the window, with HapticLink's service already set up or not
///   --required                 the service isn't set up (or is out of date): the secondary button says "Exit setup"
///   --why &lt;reason&gt;            the running service found something missing (HidHide, Steam's driver, double input)
///   --write-icon &lt;file.ico&gt; [idle]   writes an icon: Assets\app.ico (also the tray's "on" icon), or with idle tray-off.ico
/// Exits with 0 when setup (or the repair) was completed, 1 otherwise; the tray uses it to decide whether to keep running.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--write-icon", var path, ..]) { IconMaker.WriteIco(path, active: args is not [_, _, "idle"]); return 0; }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => AppLog.Write($"unhandled: {e.Exception}");
        AppLog.CatchUnhandled();
        var window = args switch
        {
            ["--required"] => new SetupWindow(required: true),
            ["--why", var why] => new SetupWindow(required: false, why),
            _ => new SetupWindow(required: false),
        };
        Application.Run(window);
        return window.Completed ? 0 : 1;
    }
}
