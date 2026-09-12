using System.Runtime.InteropServices;
using MusicAssistant.Api;

namespace MusicAssistant;

/// <summary>
/// Notification-area (tray) icon with its right-click menu.
///
/// Plain Win32, the way the Shell documents it: Shell_NotifyIcon adds the icon
/// (NOTIFYICON_VERSION_4, so the shell sends WM_CONTEXTMENU with the anchor
/// point), a window subclass on the main window receives its messages, and the
/// menu is a TrackPopupMenuEx popup. The menu is placed at the point the shell
/// hands over, and the documented SetForegroundWindow / WM_NULL bracket makes
/// it close when the user clicks elsewhere. No helper windows, no packages.
/// </summary>
/// <remarks>
/// @link https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw
/// @link https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-trackpopupmenuex
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const uint TrayMessage = 0x8000 + 1;   // WM_APP + 1

    private const int  WM_NULL = 0x0000, WM_CONTEXTMENU = 0x007B, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_POPUP = 0x0010, MF_SEPARATOR = 0x0800, MF_BYCOMMAND = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RIGHTALIGN = 0x0008, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    private const int  SM_MENUDROPALIGNMENT = 40;

    // Menu command ids
    private const uint IdOpen = 1, IdPlayPause = 2, IdNext = 3, IdPrevious = 4, IdExit = 9, IdPlayerBase = 100;

    private readonly IntPtr hwnd;
    private readonly IntPtr icon;
    private readonly SubclassProc subclass;   // kept alive for the native callback
    private readonly Action open;
    private readonly Action exit;
    private bool added;

    public TrayIcon(IntPtr hwnd, string iconPath, Action open, Action exit)
    {
        this.hwnd = hwnd;
        this.open = open;
        this.exit = exit;

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
                    ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    break;
            }
            return IntPtr.Zero;
        }
        return DefSubclassProc(h, msg, wParam, lParam);
    }

    /// <summary>Menu built fresh each time so labels and enabled state match the player.</summary>
    private void ShowMenu(int x, int y)
    {
        var player  = App.ActivePlayer;
        var playing = player?.IsPlaying == true;
        var enabled = player is null ? MF_GRAYED : 0;
        var players = App.Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name).ToList();

        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, IdOpen, "Open Music Assistant");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING | enabled, IdPlayPause, playing ? "Pause" : "Play");
        AppendMenu(menu, MF_STRING | enabled, IdNext,      "Next");
        AppendMenu(menu, MF_STRING | enabled, IdPrevious,  "Previous");
        AppendMenu(menu, MF_SEPARATOR, 0, null);

        // Speaker submenu: every visible player, the active one radio-checked
        var speakers = CreatePopupMenu();
        for (var i = 0; i < players.Count; i++)
        {
            AppendMenu(speakers, MF_STRING, IdPlayerBase + (uint)i, players[i].IsPlaying ? $"{players[i].Name}  ▶" : players[i].Name);
        }
        if (players.Count == 0)
        {
            AppendMenu(speakers, MF_STRING | MF_GRAYED, 0, "No players available");
        }
        var active = players.FindIndex(p => p.PlayerId == player?.PlayerId);
        if (active >= 0)
        {
            CheckMenuRadioItem(speakers, IdPlayerBase, IdPlayerBase + (uint)players.Count - 1, IdPlayerBase + (uint)active, MF_BYCOMMAND);
        }
        AppendMenu(menu, MF_POPUP, (uint)speakers, player is null ? "Speaker" : $"Speaker: {player.Name}");

        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, IdExit, "Exit");

        // Documented bracket: the window must be foreground for the menu to dismiss on an outside click,
        // and WM_NULL afterwards lets the menu close cleanly when the window goes back to the background
        var align   = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : 0;
        SetForegroundWindow(hwnd);
        var command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | align, x, y, hwnd, IntPtr.Zero);
        PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);   // destroys the submenu with it

        switch (command)
        {
            case IdOpen:      open(); break;
            case IdExit:      exit(); break;
            case IdPlayPause: Send(player, "play_pause"); break;
            case IdNext:      Send(player, "next"); break;
            case IdPrevious:  Send(player, "previous"); break;
            case >= IdPlayerBase when command - IdPlayerBase < players.Count:
                App.SetActivePlayer(players[(int)(command - IdPlayerBase)].PlayerId);
                break;
        }
    }

    private static void Send(Player? player, string command)
    {
        if (player is null) return;
        _ = App.Client.PlayerCommandAsync(player.PlayerId, command).ContinueWith(
            t => App.Dispatcher.TryEnqueue(() => App.Window.ShowMessage(t.Exception!.InnerException?.Message ?? "Command failed")),
            TaskContinuationOptions.OnlyOnFaulted);
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
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr hMenu, uint flags, uint idNewItem, string? newItem);
    [DllImport("user32.dll")] private static extern bool CheckMenuRadioItem(IntPtr hMenu, uint first, uint last, uint check, uint flags);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int  GetSystemMetrics(int index);
}
