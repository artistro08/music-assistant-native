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
/// hidden, so the menu is hosted by an invisible tool window that is sized to
/// the menu and moved to where a native tray menu would go (Win32
/// CalculatePopupWindowPosition, so it stays on the work area). The window is
/// brought to the foreground, the flyout fills it, and the menu closes when the
/// window loses activation, which is what an outside click does. Same approach
/// as H.NotifyIcon's second-window mode.
/// </summary>
/// <remarks>
/// @link https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-calculatepopupwindowposition
/// </remarks>
public sealed class TrayMenu
{
    private readonly Window     window;
    private readonly IntPtr     hwnd;
    private readonly Grid       root;
    private readonly MenuFlyout flyout = new() { Placement = FlyoutPlacementMode.Full };

    // Persistent items: relabeled per show, so their measured size is available before the window is placed
    private readonly MenuFlyoutItem    openItem     = Item("Open Music Assistant", "");
    private readonly MenuFlyoutItem    playItem     = Item("Play", "");
    private readonly MenuFlyoutItem    nextItem     = Item("Next", "");
    private readonly MenuFlyoutItem    previousItem = Item("Previous", "");
    private readonly MenuFlyoutSubItem speakerItem  = new() { Text = "Speaker", Icon = new FontIcon { Glyph = "" } };
    private readonly MenuFlyoutItem    exitItem     = Item("Exit", "");

    private bool visible;

    public TrayMenu(IntPtr owner, Action open, Action exit)
    {
        root   = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
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

        // Owned by the main window (same z-order family), tool window (no taskbar entry), fully transparent
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, owner);
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style | WS_EX_LAYERED | WS_EX_TOOLWINDOW));
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);

        foreach (var item in new MenuFlyoutItemBase[] { openItem, new MenuFlyoutSeparator(), playItem, nextItem, previousItem, new MenuFlyoutSeparator(), speakerItem, new MenuFlyoutSeparator(), exitItem })
        {
            flyout.Items.Add(item);
        }
        openItem.Click += (_, _) => open();
        exitItem.Click += (_, _) => exit();
        playItem.Click     += (_, _) => Send("play_pause");
        nextItem.Click     += (_, _) => Send("next");
        previousItem.Click += (_, _) => Send("previous");

        flyout.Closed    += (_, _) => Close();
        window.Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Close(); };

        // Warm-up: load the XAML tree and the menu items once so they can be measured later, then hide
        root.Loaded += (_, _) =>
        {
            flyout.ShowAt(root, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient });
            flyout.Hide();
            ShowWindow(hwnd, SW_HIDE);
        };
        window.Activate();
        ShowWindow(hwnd, SW_HIDE);
    }

    /// <summary>Show the menu for the tray icon at screen point (x, y) in physical pixels.</summary>
    public void Show(int x, int y)
    {
        if (visible) Close();
        Refresh();

        var scale = root.XamlRoot?.RasterizationScale ?? 1.0;
        var size  = Measure(scale);

        // Same placement rules as a native tray menu: on the work area, next to the icon, never over it
        var pad     = (int)Math.Round(36 * scale);
        var anchor  = new POINT { X = x, Y = y };
        var wanted  = new SIZE  { cx = size.Width, cy = size.Height };
        var exclude = new RECT  { Left = x - pad / 2, Top = y - pad / 2, Right = x + pad / 2, Bottom = y + pad / 2 };
        if (!CalculatePopupWindowPosition(ref anchor, ref wanted, TPM_BOTTOMALIGN | TPM_WORKAREA, ref exclude, out var rect))
        {
            rect = new RECT { Left = x - size.Width, Top = y - size.Height, Right = x, Bottom = y };
        }

        visible = true;
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        ShowWindow(hwnd, SW_SHOWNORMAL);
        SetForegroundWindow(hwnd);
        if (!flyout.IsOpen) flyout.ShowAt(root, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient });
    }

    private void Close()
    {
        if (!visible) return;
        visible = false;
        flyout.Hide();
        ShowWindow(hwnd, SW_HIDE);
    }

    /// <summary>Labels and enabled state follow the active player; the Speaker submenu lists every visible player.</summary>
    private void Refresh()
    {
        var player  = App.ActivePlayer;
        var enabled = player is not null;

        playItem.Text = player?.IsPlaying == true ? "Pause" : "Play";
        ((FontIcon)playItem.Icon).Glyph = player?.IsPlaying == true ? "" : "";
        playItem.IsEnabled = nextItem.IsEnabled = previousItem.IsEnabled = enabled;

        speakerItem.Text = player is null ? "Speaker" : $"Speaker: {player.Name}";
        speakerItem.Items.Clear();
        foreach (var candidate in App.Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name))
        {
            var entry = new ToggleMenuFlyoutItem
            {
                Text      = candidate.IsPlaying ? $"{candidate.Name}  ▶" : candidate.Name,
                IsChecked = candidate.PlayerId == player?.PlayerId,
            };
            entry.Click += (_, _) => App.SetActivePlayer(candidate.PlayerId);
            speakerItem.Items.Add(entry);
        }
        if (speakerItem.Items.Count == 0) speakerItem.Items.Add(new MenuFlyoutItem { Text = "No players available", IsEnabled = false });
    }

    /// <summary>Menu size in physical pixels from the items' desired sizes (they were loaded once at warm-up).</summary>
    private SIZE Measure(double scale)
    {
        double width = 0, height = 4;   // top and bottom margin
        foreach (var item in flyout.Items)
        {
            if (item is not MenuFlyoutSeparator) { item.Height = 32; item.Padding = new Thickness(11, 0, 11, 0); }
            item.Measure(new Windows.Foundation.Size(10000, 10000));
            width   = Math.Max(width, item.DesiredSize.Width);
            height += item.DesiredSize.Height;
        }
        if (width < 100) width = 260;   // not measurable yet: a sensible default so the menu still lands next to the icon
        return new SIZE { cx = (int)Math.Round(scale * width + 4), cy = (int)Math.Round(scale * height + 4) };
    }

    private static MenuFlyoutItem Item(string text, string glyph) => new() { Text = text, Icon = new FontIcon { Glyph = glyph } };

    private static void Send(string command)
    {
        if (App.ActivePlayer is not { } player) return;
        _ = App.Client.PlayerCommandAsync(player.PlayerId, command).ContinueWith(
            t => App.Dispatcher.TryEnqueue(() => App.Window.ShowMessage(t.Exception!.InnerException?.Message ?? "Command failed")),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // =========================================================================
    // WIN32
    // =========================================================================

    private const int  GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, SW_HIDE = 0, SW_SHOWNORMAL = 1;
    private const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x2, TPM_BOTTOMALIGN = 0x0020, TPM_WORKAREA = 0x10000;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int cx, cy; public int Width => cx; public int Height => cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool CalculatePopupWindowPosition(ref POINT anchor, ref SIZE size, uint flags, ref RECT exclude, out RECT position);
}
