using System.Runtime.InteropServices;
using System.Windows.Interop;
using Tickshift.Core;

namespace Tickshift.App.Infrastructure;

/// <summary>
/// Notification-area icon using Shell_NotifyIcon directly — avoids loading WinForms (tens of MB) just
/// for a tray icon. Provides open/exit/Trading Mode menu items and Windows notification balloons,
/// which Windows 10/11 show as standard toast notifications.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;
    private const uint NiifInfo = 1, NiifWarning = 2;
    private const uint MfString = 0, MfSeparator = 0x800, MfChecked = 0x8;
    private const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100, TpmBottomAlign = 0x20;
    private const int CmdOpen = 1, CmdTrading = 2, CmdExit = 3;

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreatedMessage;
    private bool _added;
    private bool _disposed;

    public TrayIcon()
    {
        _window = new HwndSource(new HwndSourceParameters("TickshiftTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadAppIcon();
        Add();
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? TradingModeToggleRequested;

    public bool TradingModeActive { get; set; }

    public void ShowBalloon(string title, string text, bool warning)
    {
        if (!_added) return;
        var data = CreateData(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = warning ? NiifWarning : NiifInfo;
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void SetTooltip(string text)
    {
        if (!_added) return;
        var data = CreateData(NifTip);
        data.szTip = Truncate(text, 127);
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void Add()
    {
        var data = CreateData(NifMessage | NifIcon | NifTip);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = $"{Brand.ProductName} — {Brand.Tagline}";
        _added = Shell_NotifyIcon(NimAdd, ref data);
        data.uVersionOrTimeout = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: re-add the icon.
            Add();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage) return IntPtr.Zero;
        var evt = (int)(lParam.ToInt64() & 0xFFFF); // NOTIFYICON_VERSION_4 puts the event in LOWORD(lParam)
        switch (evt)
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
            case WmRButtonUp:
            case WmContextMenu:
                ShowMenu();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MfString, CmdOpen, $"Open {Brand.ProductName}");
            AppendMenu(menu, MfString | (TradingModeActive ? MfChecked : 0), CmdTrading, TradingModeActive ? "End Trading Mode" : "Start Trading Mode…");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CmdExit, "Exit");
            GetCursorPos(out var pt);
            SetForegroundWindow(_window.Handle);
            var cmd = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmBottomAlign, pt.X, pt.Y, _window.Handle, IntPtr.Zero);
            switch (cmd)
            {
                case CmdOpen: OpenRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdTrading: TradingModeToggleRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdExit: ExitRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr LoadAppIcon()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            var small = new IntPtr[1];
            if (ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero) return small[0];
        }

        return LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = CreateData(0);
            Shell_NotifyIcon(NimDelete, ref data);
        }

        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
