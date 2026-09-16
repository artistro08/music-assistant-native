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
    /// <summary>WM_APP + 1.</summary>
    private const uint TrayMessage = 0x8000 + 1;

    private const int  WM_SETTINGCHANGE = 0x001A;
    private const int  WM_CONTEXTMENU   = 0x007B;
    private const int  WM_LBUTTONUP     = 0x0202;
    private const int  WM_LBUTTONDBLCLK = 0x0203;
    private const int  NIN_SELECT       = 0x0400;
    private const int  NIN_KEYSELECT    = 0x0401;
    private const uint NIM_ADD          = 0;
    private const uint NIM_MODIFY       = 1;
    private const uint NIM_DELETE       = 2;
    private const uint NIM_SETVERSION   = 4;
    private const uint NIF_MESSAGE      = 1;
    private const uint NIF_ICON         = 2;
    private const uint NIF_TIP          = 4;
    private const int  SM_CXSMICON      = 49;
    private const int  SM_CYSMICON      = 50;

    private readonly IntPtr hwnd;

    /// <summary>Asked for the icon file every time it is (re)loaded, so a settings or theme change picks a different one.</summary>
    private readonly Func<string> iconPath;

    private IntPtr icon;

    /// <summary>Kept alive for the native callback.</summary>
    private readonly SubclassProc subclass;

    private readonly Action open;
    private readonly TrayMenu menu;
    private bool added;

    /// <summary>Loads the tray icon, hooks the main window's messages and adds the icon when it should be visible.</summary>
    /// <param name="hwnd">Handle of the main window that receives the tray and single-instance messages.</param>
    /// <param name="iconPath">Returns the path of the .ico file to show; called again on every reload.</param>
    /// <param name="open">Called to bring the main window back (icon click or a second launch).</param>
    /// <param name="exit">Called when Exit is picked in the tray menu.</param>
    /// <param name="visible">Whether to add the icon to the notification area right away.</param>
    public TrayIcon(IntPtr hwnd, Func<string> iconPath, Action open, Action exit, bool visible)
    {
        this.hwnd     = hwnd;
        this.open     = open;
        this.iconPath = iconPath;
        menu = new TrayMenu(hwnd, open, exit);

        icon = LoadCurrentIcon();

        // The window subclass carries the notification-area messages AND the single-instance "show yourself" signal, so
        // it is installed even when the icon is hidden: a second launch must still reopen the window with no tray icon.
        subclass = WndProc;
        SetWindowSubclass(hwnd, subclass, 1, IntPtr.Zero);

        if (visible) Show();
    }

    /// <summary>Add the icon to the notification area (idempotent).</summary>
    public void Show()
    {
        if (added) return;
        NotifyIconData data = NewData();
        data.uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = TrayMessage;
        data.hIcon            = icon;
        data.szTip            = "Music Assistant";
        added = Shell_NotifyIcon(NIM_ADD, ref data);

        // NOTIFYICON_VERSION_4: WM_CONTEXTMENU / NIN_SELECT with coordinates.
        data.uVersion = 4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    /// <summary>Remove the icon from the notification area, keeping the subclass alive (idempotent).</summary>
    public void Hide()
    {
        if (!added) return;
        NotifyIconData data = NewData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        added = false;
    }

    /// <summary>Shows or hides the notification-area icon to match the tray setting.</summary>
    /// <param name="visible">Whether the icon should be in the notification area.</param>
    public void SetVisible(bool visible)
    {
        if (visible) Show();
        else Hide();
    }

    /// <summary>Reload the icon file, after the monochrome setting changed or Windows switched between the light and dark theme.</summary>
    public void Refresh()
    {
        IntPtr loaded = LoadCurrentIcon();

        // A failed load keeps the icon already showing, rather than blanking the notification area.
        if (loaded == IntPtr.Zero) return;

        IntPtr previous = icon;
        icon = loaded;

        if (added)
        {
            NotifyIconData data = NewData();
            data.uFlags = NIF_ICON;
            data.hIcon  = icon;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        if (previous != IntPtr.Zero) DestroyIcon(previous);
    }

    /// <summary>Tooltip shows what is playing.</summary>
    /// <param name="tip">The tooltip text; cut to the shell's 127-character limit.</param>
    public void SetTip(string tip)
    {
        if (!added) return;
        NotifyIconData data = NewData();
        data.uFlags = NIF_TIP;
        data.szTip  = tip.Length > 127 ? tip[..127] : tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Closes the tray menu, removes the icon, unhooks the main window and frees the icon handle.</summary>
    public void Dispose()
    {
        menu.Dispose();
        Hide();
        RemoveWindowSubclass(hwnd, subclass, 1);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    private NotifyIconData NewData() => new() { cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1 };

    // 1 is IMAGE_ICON and 0x10 is LR_LOADFROMFILE. The size is the shell's small-icon metric, which already
    // follows the system scaling (20px at 125%, 24 at 150%): ask for 16 and the shell stretches it, blurry.
    private IntPtr LoadCurrentIcon() =>
        LoadImage(IntPtr.Zero, iconPath(), 1, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0x10);

    // Messages

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        // A second launch of the app asks the running copy to show itself.
        if (msg == SingleInstance.ActivateMessage)
        {
            open();
            return IntPtr.Zero;
        }

        // Windows switched between the light and dark theme; the monochrome icon follows the taskbar.
        if (msg == WM_SETTINGCHANGE && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet") Refresh();

        if (msg == TrayMessage)
        {
            // Version 4: low word of lParam is the event, wParam carries the anchor point.
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint   dwState;
        public uint   dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint   uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint   dwInfoFlags;
        public Guid   guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
