using System.Runtime.InteropServices;

namespace HapticLink.App;

/// <summary>The Win32 calls the tray needs: a hidden window for messages, the notification icon and a popup menu.</summary>
internal static unsafe class Native
{
    public const uint WM_NULL = 0x0000, WM_TIMER = 0x0113, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_APP = 0x8000;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    public const int SM_CXSMICON = 49, SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State, StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowClass
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public Point Pt;
        public uint Private;
    }

    [DllImport("user32.dll")] public static extern ushort RegisterClassExW(WindowClass* wc);
    [DllImport("user32.dll")] public static extern IntPtr CreateWindowExW(uint exStyle, char* className, char* name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessageW(Msg* msg, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(Msg* msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(Msg* msg);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern nuint SetTimer(IntPtr window, nuint id, uint ms, IntPtr func);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr window, nuint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(Point* point);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr CreateIconFromResourceEx(byte* data, uint size, int icon, uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll")] public static extern bool Shell_NotifyIconW(uint message, NotifyIconData* data);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    /// <summary>
    /// Lets popup menus follow Windows' dark mode, as Explorer's own tray menus do: uxtheme's SetPreferredAppMode(AllowDark)
    /// and FlushMenuThemes, exported by ordinal only (135, 136) since Windows 10 1903. Without them menus stay light.
    /// </summary>
    public static void AllowDarkMenus()
    {
        var uxtheme = LoadLibraryW("uxtheme.dll");
        if (uxtheme == IntPtr.Zero) return;
        var setMode = GetProcAddress(uxtheme, 135);
        var flush = GetProcAddress(uxtheme, 136);
        if (setMode == IntPtr.Zero || flush == IntPtr.Zero) return;
        ((delegate* unmanaged<int, int>)setMode)(1 /* AllowDark */);
        ((delegate* unmanaged<void>)flush)();
    }

    /// <summary>The frame of an .ico (embedded resource) nearest to <paramref name="size"/>, as an icon handle.</summary>
    public static IntPtr LoadIcon(string resource, int size)
    {
        using var stream = typeof(Native).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"missing resource {resource}");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        int count = BitConverter.ToUInt16(bytes, 4), best = -1, bestSize = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + 16 * i, s = bytes[entry] == 0 ? 256 : bytes[entry];
            // The smallest frame at least as big as wanted, else the biggest there is.
            bool better = best < 0 || (s >= size ? bestSize < size || s < bestSize : bestSize < size && s > bestSize);
            if (better) { best = entry; bestSize = s; }
        }
        int length = BitConverter.ToInt32(bytes, best + 8), offset = BitConverter.ToInt32(bytes, best + 12);
        fixed (byte* p = bytes)
            return CreateIconFromResourceEx(p + offset, (uint)length, 1, 0x00030000, size, size, 0);
    }

    /// <summary>Copies text into a fixed char buffer, cut to fit and null-terminated.</summary>
    public static void Copy(string text, char* buffer, int capacity)
    {
        int n = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, n).CopyTo(new Span<char>(buffer, capacity));
        buffer[n] = '\0';
    }
}
