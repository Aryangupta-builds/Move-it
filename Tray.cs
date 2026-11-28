using System.Runtime.InteropServices;
namespace MoveIt;

// Optional notification-area (system tray) icon. Plain Win32: WinUI 3 has no tray API.
// Menu: Open MoveIt / current status (read only) / Settings / Exit.
// There is no "Pause / Resume" item on purpose: the current transfer engine cannot pause, and a fake button would be dishonest.
public sealed class TrayIcon : IDisposable
{
    const int WM_APP_TRAY = 0x8001, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_DESTROY = 0x2;
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint MF_STRING = 0, MF_GRAYED = 1, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    delegate IntPtr SubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA d);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr i);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr m, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int r, IntPtr h, IntPtr rc);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);   // a real struct: a ValueTuple cannot be marshalled (crash on right-click)
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    readonly IntPtr hwnd; readonly string iconPath;
    readonly Func<string> status; readonly Action open, settings, exit;
    readonly SubclassProc proc; IntPtr hIcon; bool added;

    public TrayIcon(IntPtr hwnd, string iconPath, Func<string> status, Action open, Action settings, Action exit)
    {
        this.hwnd = hwnd; this.iconPath = iconPath; this.status = status; this.open = open; this.settings = settings; this.exit = exit;
        proc = WndProc;
    }

    public bool Visible => added;

    public void Show()
    {
        if (added) return;   // never two icons
        hIcon = LoadImageW(IntPtr.Zero, iconPath, 1, 16, 16, 0x10);   // IMAGE_ICON, LR_LOADFROMFILE
        var d = Data(); d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP; d.uCallbackMessage = WM_APP_TRAY; d.hIcon = hIcon; d.szTip = "MOVE IT";
        SetWindowSubclass(hwnd, proc, (UIntPtr)1, UIntPtr.Zero);
        added = Shell_NotifyIconW(NIM_ADD, ref d);
        if (!added) RemoveWindowSubclass(hwnd, proc, (UIntPtr)1);
    }

    public void Hide()
    {
        if (!added) return;
        var d = Data(); Shell_NotifyIconW(NIM_DELETE, ref d); added = false;
        RemoveWindowSubclass(hwnd, proc, (UIntPtr)1);
        if (hIcon != IntPtr.Zero) { DestroyIcon(hIcon); hIcon = IntPtr.Zero; }
    }

    NOTIFYICONDATA Data() => new() { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "" };

    IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)(l.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP) open();
            else if (ev == WM_RBUTTONUP) Menu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(h, msg, w, l);
    }

    void Menu()
    {
        var m = CreatePopupMenu();
        AppendMenuW(m, MF_STRING, (UIntPtr)1, "Open MOVE IT");
        AppendMenuW(m, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(m, MF_STRING | MF_GRAYED, (UIntPtr)99, status());
        AppendMenuW(m, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(m, MF_STRING, (UIntPtr)2, "Settings");
        AppendMenuW(m, MF_STRING, (UIntPtr)3, "Exit");
        GetCursorPos(out var p); SetForegroundWindow(hwnd);
        int cmd = TrackPopupMenu(m, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(m);
        if (cmd == 1) open(); else if (cmd == 2) settings(); else if (cmd == 3) exit();
    }

    public void Dispose() => Hide();
}
