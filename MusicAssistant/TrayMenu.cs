using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MusicAssistant;

/// <summary>
/// Fluent (WinUI) context menu for the tray icon.
///
/// A MenuFlyout needs a visible XAML root, and the main window may be hidden,
/// so the menu is shown from a one-pixel anchor window that is kept fully
/// transparent (layered, alpha 0) and click-through. The anchor stays shown
/// for the life of the app: hiding it after each menu and re-showing it left
/// the next flyout without a usable root, and the window itself painted a
/// black box before its first frame.
/// </summary>
public sealed class TrayMenu
{
    private readonly Window     window;
    private readonly Grid       anchor;
    private          MenuFlyout? current;

    public TrayMenu()
    {
        anchor = new Grid { Width = 1, Height = 1 };
        window = new Window { Content = anchor, SystemBackdrop = null };
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.Title = "";

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop  = true;
            presenter.IsResizable    = false;
            presenter.IsMaximizable  = false;
            presenter.IsMinimizable  = false;
            presenter.PreferredMinimumWidth  = 1;
            presenter.PreferredMinimumHeight = 1;
        }

        // Invisible and click-through; a tool window never shows in the taskbar or Alt+Tab
        var hwnd  = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT));
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);

        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        window.AppWindow.Show(false);
    }

    /// <summary>Show the given items as a flyout at screen position (x, y) in physical pixels.</summary>
    public void Show(int x, int y, IEnumerable<MenuFlyoutItemBase> items)
    {
        current?.Hide();   // a second right-click while the menu is open replaces it

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        foreach (var item in items) flyout.Items.Add(item);
        flyout.Closed += (_, _) => { if (current == flyout) current = null; };
        current = flyout;

        window.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        window.Activate();   // foreground so the flyout light-dismisses and takes keyboard input
        try
        {
            flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedRight });
        }
        catch (Exception ex)
        {
            App.Log("Tray menu: " + ex.Message);   // a failed menu must not take down the window procedure
        }
    }

    // Win32

    private const int  GWL_EXSTYLE       = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW  = 0x00000080;
    private const long WS_EX_LAYERED     = 0x00080000;
    private const uint LWA_ALPHA         = 0x2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
}
