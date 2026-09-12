using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MusicAssistant;

/// <summary>
/// Fluent (WinUI) context menu for the tray icon.
///
/// A MenuFlyout needs a XAML root, and the main window may be hidden, so the
/// menu is shown from a tiny invisible always-on-top window placed under the
/// cursor. The window hides again as soon as the flyout closes.
/// </summary>
public sealed class TrayMenu
{
    private readonly Window window;
    private readonly Grid   anchor;

    public TrayMenu()
    {
        anchor = new Grid { Width = 1, Height = 1, Background = null };
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
        }
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        window.AppWindow.Hide();
    }

    /// <summary>Show the given items as a flyout at screen position (x, y) in physical pixels.</summary>
    public void Show(int x, int y, IEnumerable<MenuFlyoutItemBase> items)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        foreach (var item in items) flyout.Items.Add(item);
        flyout.Closed += (_, _) => window.AppWindow.Hide();

        window.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        window.AppWindow.Show();
        window.Activate();
        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedRight });
    }
}
