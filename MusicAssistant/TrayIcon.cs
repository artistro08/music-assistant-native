using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using MusicAssistant.Api;

namespace MusicAssistant;

/// <summary>
/// Notification-area (tray) icon with a native right-click menu.
///
/// Plain Win32: Shell_NotifyIcon for the icon, a Fluent MenuFlyout for the actions (see TrayMenu),
/// and a window subclass on the main window to receive the icon's messages.
/// No packages.
/// </summary>
/// <remarks>
/// @link https://learn.microsoft.com/windows/win32/shell/notification-area
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const uint  TrayMessage    = 0x8000 + 1;   // WM_APP + 1
    private const int   WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_LBUTTONUP = 0x0202, WM_COMMAND = 0x0111;
    private const uint  NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint  NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    private readonly IntPtr hwnd;
    private readonly IntPtr icon;
    private readonly SubclassProc subclass;   // kept alive for the native callback
    private readonly Action open;
    private readonly Action exit;
    private readonly TrayMenu menu = new();
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
        data.uVersion = 4;
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
        if (msg == TrayMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    open();
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            return IntPtr.Zero;
        }
        return DefSubclassProc(h, msg, wParam, lParam);
    }

    /// <summary>Fluent menu built fresh each time so labels and enabled state match the player.</summary>
    private void ShowMenu()
    {
        var player  = App.ActivePlayer;
        var playing = player?.IsPlaying == true;
        var enabled = player is not null;

        GetCursorPos(out var pt);
        menu.Show(pt.X, pt.Y,
        [
            Item("Open Music Assistant", "\uE8A7", open),
            new MenuFlyoutSeparator(),
            Item(playing ? "Pause" : "Play", playing ? "\uE769" : "\uE768", () => Send(player, "play_pause"), enabled),
            Item("Next",     "\uE893", () => Send(player, "next"),     enabled),
            Item("Previous", "\uE892", () => Send(player, "previous"), enabled),
            new MenuFlyoutSeparator(),
            PlayerSubMenu(player),
            new MenuFlyoutSeparator(),
            Item("Exit", "\uE7E8", exit),
        ]);
    }

    /// <summary>"Speaker" submenu listing every visible player; the active one is checked.</summary>
    private static MenuFlyoutItemBase PlayerSubMenu(Player? active)
    {
        var sub = new MenuFlyoutSubItem
        {
            Text = active is null ? "Speaker" : $"Speaker: {active.Name}",
            Icon = new FontIcon { Glyph = "\uE7F5" },
        };

        foreach (var player in App.Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name))
        {
            var entry = new ToggleMenuFlyoutItem
            {
                Text      = player.IsPlaying ? $"{player.Name}  \u25B6" : player.Name,
                IsChecked = player.PlayerId == active?.PlayerId,
            };
            entry.Click += (_, _) => App.SetActivePlayer(player.PlayerId);
            sub.Items.Add(entry);
        }

        if (sub.Items.Count == 0) sub.Items.Add(new MenuFlyoutItem { Text = "No players available", IsEnabled = false });
        return sub;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr refData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point pt);
}
