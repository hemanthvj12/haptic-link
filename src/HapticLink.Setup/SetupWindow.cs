using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HapticLink.App;

/// <summary>
/// Step-by-step setup (owner, 2026-11-02): first the two apps HapticLink needs, each with a green tick or a red cross and a
/// link to get it (HapticLink doesn't install them); with either missing the user can check again or exit setup. With both
/// there, Install runs setup elevated (one admin prompt) and each of its steps turns into a tick or a cross as setup
/// reports it. Shown on first start, after an update that brings a newer service, and from the tray menu.
/// </summary>
internal sealed class SetupWindow : Form
{
    private readonly bool _required;
    private readonly Palette _p = Palette.Current();
    private readonly PictureBox _logo = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _title = new() { Text = "Set up HapticLink", AutoSize = false };
    private readonly Label _subtitle = new()
    {
        Text = "HapticLink needs two free apps. With both installed, it sets itself up with one administrator prompt.",
        AutoSize = false,
    };
    private readonly Label _needsHeading = new() { Text = "Apps HapticLink needs", AutoSize = false };
    private readonly Label _installHeading = new() { Text = "Install", AutoSize = false };
    private readonly Card _needsCard, _installCard;
    private readonly StepRow _hidHide, _steam;
    private readonly Dictionary<string, StepRow> _steps;
    private readonly Label _footer = new() { AutoSize = false };
    private readonly FlatButton _primary, _secondary;
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 16 };
    private bool _missing, _busy, _installing, _checkedOnce, _repairNeeded;
    // Opened because the running service found something missing: HapticLink itself is installed, so its rows show their
    // state instead of an install. A missing app: once both have a tick, HapticLink turns back on by itself. Double input
    // (HidHide isn't hiding the controller): "HidHide set-up" has a cross and Repair hides it again.
    private readonly string? _whyOff, _doubleInput;

    /// <summary>Setup finished: the service is installed and running.</summary>
    public bool Completed { get; private set; }

    /// <param name="required">The service isn't set up (or is out of date), so the secondary button says "Exit setup".</param>
    /// <param name="whyOff">Why the installed service won't turn on (e.g. "HidHide isn't installed"), or null.</param>
    public SetupWindow(bool required, string? whyOff = null)
    {
        _required = required;
        _whyOff = required ? null : whyOff;
        _doubleInput = _whyOff is { } w && w.StartsWith(ServiceClient.HidHideBroken, StringComparison.Ordinal)
            ? w[ServiceClient.HidHideBroken.Length..] : null;
        if (_doubleInput is not null)
            _subtitle.Text = "HapticLink is off because games could see your controller twice (double input). Repair hides the real controller again.";
        else if (_whyOff is not null)
            _subtitle.Text = $"HapticLink is off: {_whyOff}. It turns back on by itself once both apps below have a green tick.";
        Text = "HapticLink setup";
        Icon = IconMaker.Create(32, active: true);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None; // laid out in code for the current DPI
        BackColor = _p.Back;
        ForeColor = _p.Text;
        Font = new Font("Segoe UI", 9f);

        _hidHide = new StepRow(_p, "HidHide", "Checking…");
        _steam = new StepRow(_p, "Steam", "Checking…");
        _steps = new()
        {
            ["driver"] = new StepRow(_p, "Virtual controller driver", "The wired DualSense games will see"),
            ["hidhide"] = new StepRow(_p, "HidHide set-up", "Lets HapticLink use the controller HidHide hides from games"),
            ["speaker"] = new StepRow(_p, "Controller speaker", "Steam's speaker driver, which haptics play through"),
            ["service"] = new StepRow(_p, "HapticLink service", "Runs the controller while the HapticLink app is open"),
        };
        _needsCard = new Card(_p, _hidHide, _steam);
        _installCard = new Card(_p, [.. _steps.Values]);

        _title.Font = new Font("Segoe UI Semibold", 15f);
        _subtitle.ForeColor = _p.SubText;
        foreach (var heading in new[] { _needsHeading, _installHeading }) heading.Font = new Font("Segoe UI Semibold", 10.5f);
        _footer.ForeColor = _p.SubText;
        _primary = new FlatButton(_p, primary: true);
        _secondary = new FlatButton(_p, primary: false) { Text = _required ? "Exit setup" : "Close" };
        _primary.Click += async (_, _) => await PrimaryAsync();
        _secondary.Click += (_, _) => Close();
        AcceptButton = _primary;
        CancelButton = _secondary;

        Controls.AddRange([_logo, _title, _subtitle, _needsHeading, _needsCard, _installHeading, _installCard, _footer, _primary, _secondary]);
        if (_whyOff is not null) ShowInstalledState();
        foreach (var row in Rows()) row.HeightChanged += LayOut;
        _spin.Tick += (_, _) => { foreach (var row in Rows()) row.Spin(); };
        _spin.Start();
        LayOut();
    }

    private IEnumerable<StepRow> Rows() => _steps.Values.Prepend(_steam).Prepend(_hidHide);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = _p.Dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await CheckAsync();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayOut();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing) { e.Cancel = true; return; } // setup is running elevated; let it finish
        _spin.Stop();
        base.OnFormClosing(e);
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private void LayOut()
    {
        int width = S(560), margin = S(24), inner = width - 2 * margin;
        _logo.Image?.Dispose();
        _logo.Image = IconMaker.Draw(S(48), active: true);
        _logo.SetBounds(margin, S(24), S(48), S(48));
        int textLeft = margin + S(48) + S(16), textWidth = width - textLeft - margin;
        _title.SetBounds(textLeft, S(22), textWidth, S(30));
        int subtitleHeight = Measure(_subtitle.Text, _subtitle.Font, textWidth);
        _subtitle.SetBounds(textLeft, S(54), textWidth, subtitleHeight);

        int y = Math.Max(S(24) + S(48), S(54) + subtitleHeight) + S(24);
        _needsHeading.SetBounds(margin, y, inner, S(22));
        y += S(28);
        _needsCard.LayOut(margin, y, inner);
        y = _needsCard.Bottom + S(20);
        _installHeading.SetBounds(margin, y, inner, S(22));
        y += S(28);
        _installCard.LayOut(margin, y, inner);
        y = _installCard.Bottom + S(16);

        int footerHeight = Math.Max(S(20), Measure(_footer.Text, _footer.Font, inner));
        _footer.SetBounds(margin, y, inner, footerHeight);
        y += footerHeight + S(16);
        int buttonWidth = S(120), buttonHeight = S(34);
        _secondary.SetBounds(width - margin - buttonWidth, y, buttonWidth, buttonHeight);
        _primary.SetBounds(_secondary.Left - S(8) - buttonWidth, y, buttonWidth, buttonHeight);
        _primary.Visible = _primary.Text.Length > 0;
        ClientSize = new Size(width, y + buttonHeight + S(24));
    }

    private static int Measure(string text, Font font, int width) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height;

    private void SetFooter(string text, Color? color = null)
    {
        _footer.Text = text;
        _footer.ForeColor = color ?? _p.SubText;
        LayOut();
    }

    private async Task PrimaryAsync()
    {
        if (_busy) return;
        if (Completed) { Close(); return; }
        if (_missing) await CheckAsync();
        else if (_repairNeeded) await RepairAsync();
        else await InstallAsync();
    }

    /// <summary>HapticLink is installed already: its rows show what's set up and, with a cross, what isn't.</summary>
    private void ShowInstalledState()
    {
        _installHeading.Text = "HapticLink";
        _steps["driver"].Set(StepState.Ok, "Installed");
        _steps["service"].Set(StepState.Ok, "Installed");
        bool hidHideMissing = _whyOff == "HidHide isn't installed";
        if (_doubleInput is not null) _steps["hidhide"].Set(StepState.Bad, Capitalize(_doubleInput));
        else if (hidHideMissing) _steps["hidhide"].Set(StepState.Bad, "Needs HidHide");
        else _steps["hidhide"].Set(StepState.Ok, "Hiding the real controller from games");
        if (_doubleInput is null && !hidHideMissing) _steps["speaker"].Set(StepState.Bad, Capitalize(_whyOff!));
        else _steps["speaker"].Set(StepState.Ok, "Steam's speaker driver is ready");
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ---- checking the apps HapticLink needs -------------------------------------------------------

    private async Task CheckAsync()
    {
        _busy = true;
        _primary.Enabled = false;
        _hidHide.Set(StepState.Running, "Checking…");
        _steam.Set(StepState.Running, "Checking…");
        SetFooter("");
        bool again = _checkedOnce;
        _checkedOnce = true;
        var (hidHide, steam) = await Task.Run(async () =>
        {
            if (again) ServiceClient.Recheck(); // the service looks again too (and adds Steam's driver if it can)
            await Task.Delay(400); // long enough to see it check (pressing Check again should visibly do something)
            return (Requirements.HidHide(), Requirements.Steam());
        });
        ShowRequirement(_hidHide, hidHide, "Get HidHide", Requirements.HidHideUrl);
        ShowRequirement(_steam, steam, "Get Steam", Requirements.SteamUrl);
        AppLog.Write($"setup: HidHide {(hidHide.Ok ? "found" : "missing")}, Steam {(steam.Ok ? "found" : "missing")}");

        _missing = !hidHide.Ok || !steam.Ok;
        _busy = false;
        _primary.Enabled = true;
        if (_missing)
        {
            var names = new[] { hidHide.Ok ? null : "HidHide", steam.Ok ? null : "Steam" }.OfType<string>().ToArray();
            _primary.Text = "Check again";
            SetFooter($"{string.Join(" and ", names)} {(names.Length == 1 ? "isn't" : "aren't")} installed. Install {(names.Length == 1 ? "it" : "them")} " +
                      "with the link above, then choose Check again." +
                      (_required ? " You can also exit setup; it opens again the next time HapticLink starts."
                                 : " You can also close this and come back to it from the tray icon."), _p.Bad);
        }
        else if (_doubleInput is not null)
        {
            _repairNeeded = true;
            _primary.Text = "Repair";
            SetFooter("HidHide is installed, but it isn't hiding your controller, so games see it twice. Choose Repair to hide it " +
                      "again; the controller disconnects for a moment.", _p.Bad);
        }
        else if (_whyOff is not null)
        {
            // HapticLink itself is installed; the service notices within seconds (Check again also told it to look now).
            await Task.Run(ServiceClient.Recheck);
            _steps["hidhide"].Set(StepState.Ok, "Hiding the real controller from games");
            _steps["speaker"].Set(StepState.Ok, "Steam's speaker driver is ready");
            Completed = true;
            _primary.Text = "Done";
            _secondary.Visible = false;
            SetFooter("Both apps are installed. HapticLink turns back on by itself in a few seconds.", _p.Ok);
        }
        else
        {
            _primary.Text = "Install";
            SetFooter("Everything HapticLink needs is installed. Choose Install; Windows asks for administrator permission once.");
        }
        _primary.Focus();
    }

    private static void ShowRequirement(StepRow row, Requirement r, string linkText, string url)
    {
        row.Set(r.Ok ? StepState.Ok : StepState.Bad, r.Detail);
        row.SetLink(r.Ok ? null : linkText, url);
    }

    // ---- repairing double input --------------------------------------------------------------------

    /// <summary>
    /// The service hides the real controller again and restarts it; then this checks Windows' game controller list (what
    /// gamepad testers and most games read) no longer has it.
    /// </summary>
    private async Task RepairAsync()
    {
        _busy = true;
        _primary.Enabled = _secondary.Enabled = false;
        var row = _steps["hidhide"];
        row.Set(StepState.Running, "Hiding the real controller again and restarting it…");
        SetFooter("Repairing. Your controller disconnects for a moment.");

        string? problem = await Task.Run(async () =>
        {
            if (!ServiceClient.Repair()) return "HapticLink's service isn't running";
            DateTime? onSince = null;
            for (var until = DateTime.UtcNow.AddSeconds(40); DateTime.UtcNow < until; await Task.Delay(500))
            {
                var s = ServiceClient.Status();
                if (s is null) continue;
                if (s.State == "waiting") return null; // no controller connected: it's hidden as soon as it connects
                if (s.Message.StartsWith(ServiceClient.NeedsSetup, StringComparison.Ordinal))
                {
                    var what = s.Message[ServiceClient.NeedsSetup.Length..].Split(". Open HapticLink's setup", 2)[0];
                    // Right after REPAIR the old state can still show for a moment.
                    if (DateTime.UtcNow < until.AddSeconds(-38)) continue;
                    return what.StartsWith(ServiceClient.HidHideBroken, StringComparison.Ordinal) ? what[ServiceClient.HidHideBroken.Length..] : what;
                }
                if (s.State != "on") { onSince = null; continue; }
                onSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - onSince < TimeSpan.FromSeconds(3)) continue; // let Windows settle after the restart
                if (!DoubleInput.RealPadVisible()) return null;
                ServiceClient.ReportDoubleInput();
                return "Windows still lists your real controller. Turn the controller off and on, then choose Repair again; if that doesn't help, restart Windows";
            }
            return "HapticLink didn't turn back on; details in %ProgramData%\\HapticLink\\service.log";
        });
        _busy = false;
        _primary.Enabled = _secondary.Enabled = true;
        AppLog.Write($"setup window: repair {(problem is null ? "worked" : $"didn't work: {problem}")}");
        if (problem is null)
        {
            row.Set(StepState.Ok, "Hiding the real controller from games");
            Completed = true;
            _repairNeeded = false;
            _primary.Text = "Done";
            _secondary.Visible = false;
            SetFooter("Repaired: games see only HapticLink's controller again.", _p.Ok);
        }
        else
        {
            row.Set(StepState.Bad, Capitalize(problem));
            _primary.Text = "Repair";
            SetFooter("The repair didn't work. The HidHide set-up line above says what to try.", _p.Bad);
        }
        _primary.Focus();
    }

    // ---- installing --------------------------------------------------------------------------------

    private async Task InstallAsync()
    {
        _busy = true;
        _installing = true;
        _primary.Enabled = _secondary.Enabled = false;
        foreach (var row in _steps.Values) row.Set(StepState.Pending, row.Description);
        SetFooter("Installing. Windows asks for administrator permission first.");

        string? running = null;
        var (ok, error) = await ServiceSetup.RunAsync((step, state, message) =>
        {
            if (!_steps.TryGetValue(step, out var row)) return;
            switch (state)
            {
                case "run": running = step; row.Set(StepState.Running, row.Description); break;
                case "ok": row.Set(StepState.Ok, message); break;
                case "skip": row.Set(StepState.Skipped, message); break;
                case "fail": row.Set(StepState.Bad, message); break;
            }
            if (state != "run" && running == step) running = null;
            if (state == "run") SetFooter("Installing…");
        });
        _busy = false;
        _installing = false;
        _primary.Enabled = _secondary.Enabled = true;
        AppLog.Write($"setup window: {(ok ? "done" : error ?? "cancelled")}");

        if (ok)
        {
            Completed = true;
            _primary.Text = "Done";
            _secondary.Visible = false;
            bool partial = _steps.Values.Any(r => r.State is StepState.Bad or StepState.Skipped);
            SetFooter(partial
                ? "HapticLink is set up, but a step above didn't work; it says what to do. Turn on your DualSense (paired over Bluetooth) to start."
                : "HapticLink is set up. Turn on your DualSense (paired over Bluetooth); HapticLink takes over from there.",
                partial ? _p.Bad : _p.Ok);
        }
        else if (error is null)
        {
            foreach (var row in _steps.Values) row.Set(StepState.Pending, row.Description);
            _primary.Text = "Install";
            SetFooter("Setup needs administrator permission to install the driver and the service. Choose Install to try again.", _p.Bad);
        }
        else
        {
            if (running is not null) _steps[running].Set(StepState.Bad, "Didn't finish; details in %ProgramData%\\HapticLink\\service.log");
            _primary.Text = "Try again";
            SetFooter($"Setup failed: {error}.", _p.Bad);
        }
        _primary.Focus();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal enum StepState { Pending, Running, Ok, Bad, Skipped }

/// <summary>Colours in the style of Windows 11, light or dark as the user's apps are set.</summary>
internal sealed record Palette(bool Dark, Color Back, Color Card, Color Border, Color Text, Color SubText, Color Accent,
    Color OnAccent, Color Ok, Color Bad, Color Ring, Color Hover)
{
    public static Palette Current()
    {
        bool dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return dark
            ? new(true, C(0x202020), C(0x2B2B2B), C(0x3A3A3A), C(0xFFFFFF), C(0xC5C5C5), C(0x60CDFF), C(0x000000), C(0x6CCB5F), C(0xFF99A4), C(0x9A9A9A), C(0x3A3A3A))
            : new(false, C(0xF3F3F3), C(0xFFFFFF), C(0xE5E5E5), C(0x1A1A1A), C(0x5F5F5F), C(0x005FB8), C(0xFFFFFF), C(0x0F7B0F), C(0xC42B1C), C(0x8A8A8A), C(0xF0F0F0));
    }

    private static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

/// <summary>A rounded card holding rows, with a hairline between them.</summary>
internal sealed class Card : Control
{
    private readonly Palette _p;
    private readonly StepRow[] _rows;

    public Card(Palette p, params StepRow[] rows)
    {
        _p = p;
        _rows = rows;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Controls.AddRange(rows);
    }

    public void LayOut(int x, int y, int width)
    {
        int s1 = Math.Max(1, DeviceDpi / 96), top = 0;
        foreach (var row in _rows)
        {
            row.SetBounds(s1, top + s1, width - 2 * s1, row.PreferredHeight(width - 2 * s1));
            top = row.Bottom;
        }
        SetBounds(x, y, width, top + s1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int r = DeviceDpi * 8 / 96;
        using (var path = FlatButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), r))
        {
            using var fill = new SolidBrush(_p.Card);
            g.FillPath(fill, path);
            using var pen = new Pen(_p.Border);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = SmoothingMode.None;
        using var line = new Pen(_p.Border);
        for (int i = 1; i < _rows.Length; i++) g.DrawLine(line, _rows[i].Left + DeviceDpi * 52 / 96, _rows[i].Top, _rows[i].Right - DeviceDpi * 16 / 96, _rows[i].Top);
    }
}

/// <summary>One row: a status circle (tick, cross, spinner or ring), a title, a detail line and an optional link.</summary>
internal sealed class StepRow : Control
{
    private readonly Palette _p;
    private readonly LinkLabel _link = new() { AutoSize = true, Visible = false };
    private readonly Font _titleFont = new("Segoe UI Semibold", 10f);
    private readonly Font _detailFont = new("Segoe UI", 9f);
    private string _detail;
    private string? _url;
    private float _angle;

    public string Title { get; }
    /// <summary>What the step does, shown while it hasn't run.</summary>
    public string Description { get; }
    public StepState State { get; private set; } = StepState.Pending;
    public event Action? HeightChanged;

    public StepRow(Palette p, string title, string description)
    {
        _p = p;
        Title = title;
        Description = _detail = description;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = p.Card;
        _link.LinkColor = _link.ActiveLinkColor = p.Accent;
        _link.VisitedLinkColor = p.Accent;
        _link.LinkBehavior = LinkBehavior.HoverUnderline;
        _link.Font = new Font("Segoe UI", 9f);
        _link.BackColor = p.Card;
        _link.LinkClicked += (_, _) => { if (_url is not null) Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); };
        Controls.Add(_link);
        AccessibleRole = AccessibleRole.StaticText;
        UpdateAccessibleName();
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    private int TextLeft => S(52);
    private int TextWidth(int width) => width - TextLeft - S(16) - (_link.Visible ? _link.PreferredWidth + S(16) : 0);

    public int PreferredHeight(int width)
    {
        int title = TextRenderer.MeasureText(Title, _titleFont).Height;
        int detail = TextRenderer.MeasureText(_detail, _detailFont, new Size(Math.Max(1, TextWidth(width)), int.MaxValue), TextFormatFlags.WordBreak).Height;
        return Math.Max(S(56), S(12) + title + S(2) + detail + S(12));
    }

    public void Set(StepState state, string detail)
    {
        bool taller = detail != _detail;
        State = state;
        _detail = detail;
        UpdateAccessibleName();
        if (taller) HeightChanged?.Invoke();
        Invalidate();
    }

    public void SetLink(string? text, string url)
    {
        _url = url;
        _link.Text = text ?? "";
        _link.Visible = text is not null;
        HeightChanged?.Invoke();
        PlaceLink();
    }

    public void Spin()
    {
        if (State != StepState.Running) return;
        _angle = (_angle + 6) % 360;
        Invalidate(new Rectangle(S(12), 0, S(32), Height));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PlaceLink();
    }

    private void PlaceLink()
    {
        if (_link.Visible) _link.Location = new Point(Width - S(16) - _link.PreferredWidth, (Height - _link.PreferredHeight) / 2);
    }

    private void UpdateAccessibleName()
    {
        string state = State switch
        {
            StepState.Ok => "done", StepState.Bad => "problem", StepState.Running => "in progress",
            StepState.Skipped => "skipped", _ => "not started",
        };
        AccessibleName = $"{Title}: {state}. {_detail}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_p.Card);
        int title = TextRenderer.MeasureText(Title, _titleFont).Height;
        int width = Math.Max(1, TextWidth(Width));
        int detailHeight = TextRenderer.MeasureText(_detail, _detailFont, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height;
        int top = (Height - (title + S(2) + detailHeight)) / 2;
        TextRenderer.DrawText(g, Title, _titleFont, new Point(TextLeft, top), _p.Text, TextFormatFlags.NoPadding);
        var detailColor = State == StepState.Bad ? _p.Bad : _p.SubText;
        TextRenderer.DrawText(g, _detail, _detailFont, new Rectangle(TextLeft, top + title + S(2), width, detailHeight), detailColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = S(22);
        var circle = new RectangleF(S(16), (Height - d) / 2f, d, d);
        switch (State)
        {
            case StepState.Ok:
            {
                using var fill = new SolidBrush(_p.Ok);
                g.FillEllipse(fill, circle);
                using var pen = new Pen(_p.Dark ? _p.Card : Color.White, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float x = circle.X, y = circle.Y;
                g.DrawLines(pen, [new PointF(x + d * 0.28f, y + d * 0.52f), new PointF(x + d * 0.44f, y + d * 0.67f), new PointF(x + d * 0.73f, y + d * 0.36f)]);
                break;
            }
            case StepState.Bad:
            {
                using var fill = new SolidBrush(_p.Bad);
                g.FillEllipse(fill, circle);
                using var pen = new Pen(_p.Dark ? _p.Card : Color.White, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                float a = d * 0.33f, b = d * 0.67f;
                g.DrawLine(pen, circle.X + a, circle.Y + a, circle.X + b, circle.Y + b);
                g.DrawLine(pen, circle.X + b, circle.Y + a, circle.X + a, circle.Y + b);
                break;
            }
            case StepState.Running:
            {
                using var track = new Pen(_p.Border, S(2));
                using var arc = new Pen(_p.Accent, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var ring = RectangleF.Inflate(circle, -S(2), -S(2));
                g.DrawEllipse(track, ring);
                g.DrawArc(arc, ring, _angle, 100);
                break;
            }
            case StepState.Skipped:
            {
                using var pen = new Pen(_p.Ring, Math.Max(1, S(1.5f)));
                var ring = RectangleF.Inflate(circle, -S(1), -S(1));
                g.DrawEllipse(pen, ring);
                g.DrawLine(pen, circle.X + d * 0.32f, circle.Y + d / 2f, circle.X + d * 0.68f, circle.Y + d / 2f);
                break;
            }
            default:
            {
                using var pen = new Pen(_p.Ring, Math.Max(1, S(1.5f)));
                g.DrawEllipse(pen, RectangleF.Inflate(circle, -S(1), -S(1)));
                break;
            }
        }
    }

    private float S(float value) => value * DeviceDpi / 96f;
}

/// <summary>A Windows 11-style button: accent-filled (primary) or outlined, with rounded corners.</summary>
internal sealed class FlatButton : Button
{
    private readonly Palette _p;
    private readonly bool _primary;
    private bool _hover, _down;

    public FlatButton(Palette p, bool primary)
    {
        _p = p;
        _primary = primary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Font = new Font("Segoe UI", 9f);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = _primary ? _p.Accent : _p.Card;
        if (!Enabled) fill = _primary ? Blend(_p.Accent, _p.Back, 0.55f) : _p.Card;
        else if (_down) fill = Blend(fill, _p.Back, 0.25f);
        else if (_hover) fill = _primary ? Blend(_p.Accent, _p.Back, 0.12f) : _p.Hover;
        using (var path = Rounded(bounds, DeviceDpi * 4 / 96))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            if (!_primary) { using var pen = new Pen(_p.Border); g.DrawPath(pen, path); }
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(_p.Text, Math.Max(1, DeviceDpi / 96));
                using var ring = Rounded(Rectangle.Inflate(bounds, -2, -2), DeviceDpi * 3 / 96);
                g.DrawPath(focus, ring);
            }
        }
        var text = _primary ? _p.OnAccent : _p.Text;
        if (!Enabled) text = Blend(text, fill, 0.5f);
        TextRenderer.DrawText(g, Text, Font, bounds, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
