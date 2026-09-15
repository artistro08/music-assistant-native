using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MusicAssistant.Api;

namespace MusicAssistant;

/// <summary>
/// Fluent (WinUI) context menu for the tray icon.
///
/// WinUI menus need a XAML window to live in and the main window may be
/// hidden, so the menu is hosted by an invisible tool window. Like the
/// Windows shell's own tray flyouts, the menu is placed off the taskbar edge:
/// the icon's rectangle (Shell_NotifyIconGetRect) and the monitor's work area
/// tell which edge the taskbar is on, the host window is parked in the work
/// area corner next to the icon, and the flyout opens from that corner
/// towards the screen. The host is brought to the foreground and the menu
/// closes when it loses activation, which is what an outside click does.
/// </summary>
/// <remarks>
/// @link https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shell_notifyicongetrect
/// @link https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getmonitorinfow
/// </remarks>
public sealed class TrayMenu
{
    // Physical pixels; room for the menu at any scale, clamped to the work area.
    private const int HostWidth  = 600;
    private const int HostHeight = 800;

    private readonly IntPtr     owner;
    private readonly Window     window;
    private readonly IntPtr     hwnd;
    private readonly Grid       root;

    /// <summary>The flyout opens from this corner of the host.</summary>
    private readonly Grid       target = new() { Width = 1, Height = 1 };

    private readonly MenuFlyout flyout = new();

    // Persistent items, relabeled per show.
    private readonly MenuFlyoutItem    openItem     = Item("Open Music Assistant", "\uE8A7");
    private readonly MenuFlyoutItem    playItem     = Item("Play", "\uE768");
    private readonly MenuFlyoutItem    nextItem     = Item("Next", "\uE893");
    private readonly MenuFlyoutItem    previousItem = Item("Previous", "\uE892");
    private readonly MenuFlyoutSubItem speakerItem  = new() { Text = "Speaker", Icon = new FontIcon { Glyph = "\uE7F5" } };
    private readonly MenuFlyoutItem    exitItem     = Item("Exit", "\uE7E8");

    private bool visible;

    /// <summary>Builds the hidden host window and the menu, and loads its XAML tree once so the first show is reliable.</summary>
    /// <param name="owner">Handle of the main window that owns the tray icon; used to look up the icon's rectangle.</param>
    /// <param name="open">Called when Open Music Assistant is picked.</param>
    /// <param name="exit">Called when Exit is picked.</param>
    public TrayMenu(IntPtr owner, Action open, Action exit)
    {
        this.owner = owner;
        root   = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.Children.Add(target);
        window = new Window { Content = root, SystemBackdrop = null };
        hwnd   = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.Title = "";

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop  = true;
            presenter.IsResizable    = false;
            presenter.IsMaximizable  = false;
            presenter.IsMinimizable  = false;
        }

        // Tool window (no taskbar entry), fully transparent. Deliberately not owned by the main window:
        // activating an owned window drags its owner to the front, and the menu must not raise the app.
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style | WS_EX_LAYERED | WS_EX_TOOLWINDOW));
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);

        foreach (MenuFlyoutItemBase item in new MenuFlyoutItemBase[] { openItem, new MenuFlyoutSeparator(), playItem, nextItem, previousItem, new MenuFlyoutSeparator(), speakerItem, new MenuFlyoutSeparator(), exitItem })
        {
            flyout.Items.Add(item);
        }
        openItem.Click += (_, _) => open();
        exitItem.Click += (_, _) => exit();
        playItem.Click     += (_, _) => Send("play_pause");
        nextItem.Click     += (_, _) => Send("next");
        previousItem.Click += (_, _) => Send("previous");

        flyout.Closed    += (_, _) => Close();
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Close();
        };

        // Warm-up: load the XAML tree once (a first ShowAt on a never-shown window is not reliable), then hide.
        window.Activate();
        ShowWindow(hwnd, SW_HIDE);
    }

    /// <summary>Show the menu for the tray icon; (x, y) is the shell's anchor point in physical pixels, used when the icon rectangle is unavailable.</summary>
    /// <param name="x">Horizontal anchor point in physical pixels.</param>
    /// <param name="y">Vertical anchor point in physical pixels.</param>
    public void Show(int x, int y)
    {
        if (visible) Close();
        Refresh();

        // Icon rectangle and the work area of its monitor.
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = owner, uID = 1 };
        if (Shell_NotifyIconGetRect(ref id, out RECT icon) != 0) icon = new RECT { Left = x, Top = y, Right = x + 1, Bottom = y + 1 };
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromRect(ref icon, MONITOR_DEFAULTTONEAREST), ref info);
        RECT work = info.rcWork;

        // The taskbar is on the monitor edge the icon sits against.
        int toTop    = icon.Top  - info.rcMonitor.Top;
        int toBottom = info.rcMonitor.Bottom - icon.Bottom;
        int toLeft   = icon.Left - info.rcMonitor.Left;
        int toRight  = info.rcMonitor.Right  - icon.Right;
        Edge edge = Math.Min(Math.Min(toTop, toBottom), Math.Min(toLeft, toRight)) switch
        {
            var m when m == toBottom => Edge.Bottom,
            var m when m == toTop    => Edge.Top,
            var m when m == toLeft   => Edge.Left,
            _                        => Edge.Right,
        };

        // Park the host in the work-area corner next to the icon; the flyout grows from that corner into the screen.
        int w = Math.Min(HostWidth,  work.Right - work.Left);
        int h = Math.Min(HostHeight, work.Bottom - work.Top);
        int right  = Math.Clamp(icon.Right,  work.Left + w, work.Right);
        int bottom = Math.Clamp(icon.Bottom, work.Top + h,  work.Bottom);
        RECT host = edge switch
        {
            Edge.Bottom => new RECT { Left = right - w, Top = work.Bottom - h, Right = right, Bottom = work.Bottom },
            Edge.Top    => new RECT { Left = right - w, Top = work.Top, Right = right, Bottom = work.Top + h },
            Edge.Left   => new RECT { Left = work.Left, Top = bottom - h, Right = work.Left + w, Bottom = bottom },
            _           => new RECT { Left = work.Right - w, Top = bottom - h, Right = work.Right, Bottom = bottom },
        };
        target.HorizontalAlignment = edge == Edge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        target.VerticalAlignment   = edge == Edge.Top  ? VerticalAlignment.Top    : VerticalAlignment.Bottom;
        flyout.Placement = edge switch
        {
            Edge.Bottom => FlyoutPlacementMode.TopEdgeAlignedRight,
            Edge.Top    => FlyoutPlacementMode.BottomEdgeAlignedRight,
            Edge.Left   => FlyoutPlacementMode.RightEdgeAlignedBottom,
            _           => FlyoutPlacementMode.LeftEdgeAlignedBottom,
        };

        visible = true;
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(host.Left, host.Top, host.Right - host.Left, host.Bottom - host.Top));
        ShowWindow(hwnd, SW_SHOWNORMAL);
        SetForegroundWindow(hwnd);
        if (!flyout.IsOpen) flyout.ShowAt(target, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient, Placement = flyout.Placement });
    }

    private enum Edge
    {
        Bottom,
        Top,
        Left,
        Right,
    }

    private void Close()
    {
        if (!visible) return;
        visible = false;
        flyout.Hide();
        ShowWindow(hwnd, SW_HIDE);
    }

    /// <summary>Tear the host window down on exit; a live window would keep the process running after the main window closed.</summary>
    public void Dispose()
    {
        Close();
        window.Close();
    }

    /// <summary>Labels and enabled state follow the active player; the Speaker submenu lists every visible player.</summary>
    private void Refresh()
    {
        Player? player  = App.ActivePlayer;
        bool enabled = player is not null;

        playItem.Text = player?.IsPlaying == true ? "Pause" : "Play";
        ((FontIcon)playItem.Icon).Glyph = player?.IsPlaying == true ? "\uE769" : "\uE768";
        playItem.IsEnabled = nextItem.IsEnabled = previousItem.IsEnabled = enabled;

        speakerItem.Text = player is null ? "Speaker" : $"Speaker: {player.DisplayName}";
        speakerItem.Items.Clear();
        foreach (Player? candidate in App.Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name))
        {
            var entry = new ToggleMenuFlyoutItem
            {
                Text      = candidate.IsPlaying ? $"{candidate.DisplayName}  ▶" : candidate.DisplayName,
                IsChecked = candidate.PlayerId == player?.PlayerId,
            };
            entry.Click += (_, _) => App.SetActivePlayer(candidate.PlayerId);
            speakerItem.Items.Add(entry);
        }
        if (speakerItem.Items.Count == 0) speakerItem.Items.Add(new MenuFlyoutItem { Text = "No players available", IsEnabled = false });
    }

    private static MenuFlyoutItem Item(string text, string glyph) => new() { Text = text, Icon = new FontIcon { Glyph = glyph } };

    private static void Send(string command)
    {
        if (App.ActivePlayer is { } player) App.SendPlayerCommand(player, command);
    }

    // =========================================================================
    // WIN32
    // =========================================================================

    private const int  GWL_EXSTYLE              = -20;
    private const int  SW_HIDE                  = 0;
    private const int  SW_SHOWNORMAL            = 1;
    private const long WS_EX_TOOLWINDOW         = 0x00000080;
    private const long WS_EX_LAYERED            = 0x00080000;
    private const uint LWA_ALPHA                = 0x2;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public Guid   guidItem;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);
}
