using System.Runtime.InteropServices;
using MusicAssistant.Api;

namespace MusicAssistant;

/// <summary>
/// Notification-area (tray) icon with a Fluent right-click menu.
///
/// Shell_NotifyIcon adds the icon (NOTIFYICON_VERSION_4, so the shell sends
/// WM_CONTEXTMENU with the anchor point), a window subclass on the main window
/// receives its messages, and TrayMenu shows the WinUI menu at that point.
/// </summary>
/// <remarks>
/// @link https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const uint TrayMessage = 0x8000 + 1;   // WM_APP + 1

    private const int  WM_CONTEXTMENU = 0x007B, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    private readonly IntPtr hwnd;
    private readonly IntPtr icon;
    private readonly SubclassProc subclass;   // kept alive for the native callback
    private readonly Action open;
    private readonly TrayMenu menu;
    private bool added;

    public TrayIcon(IntPtr hwnd, string iconPath, Action open, Action exit)
    {
        this.hwnd = hwnd;
        this.open = open;
        menu = new TrayMenu(hwnd, open, exit);

        icon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, 16, 16, 0x10 /* LR_LOADFROMFILE */);
        subclass = WndProc;
        SetWindowSubclass(hwnd, subclass, 1, IntPtr.Zero);

        var data = NewData();
        data.uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = TrayMessage;
        data.hIcon            = icon;
        data.szTip            = "Music Assistant";
        added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = 4;   // NOTIFYICON_VERSION_4: WM_CONTEXTMENU / NIN_SELECT with coordinates
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    /// <summary>Tooltip shows what is playing.</summary>
    public void SetTip(string tip)
    {
        if (!added) return;
        var data = NewData();
        data.uFlags = NIF_TIP;
        data.szTip  = tip.Length > 127 ? tip[..127] : tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        menu.Dispose();
        if (added)
        {
            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            added = false;
        }
        RemoveWindowSubclass(hwnd, subclass, 1);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    private NotifyIconData NewData() => new() { cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1 };

    // Messages

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        // A second launch of the app asks the running copy to show itself
        if (msg == SingleInstance.ActivateMessage)
        {
            open();
            return IntPtr.Zero;
        }

        if (msg == TrayMessage)
        {
            // Version 4: low word of lParam is the event, wParam carries the anchor point
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    open();
                    break;
                case WM_CONTEXTMENU:
                    menu.Show((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    break;
            }
            return IntPtr.Zero;
        }
        return DefSubclassProc(h, msg, wParam, lParam);
    }

    // =========================================================================
    // WIN32
    // =========================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public uint   uFlags;
        public uint   uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint   dwState;
        public uint   dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint   uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint   dwInfoFlags;
        public Guid   guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr refData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}
