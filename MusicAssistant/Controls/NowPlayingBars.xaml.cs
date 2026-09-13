using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MusicAssistant.Controls;

/// <summary>
/// Live "now playing" indicator: four accent bars pulsing at different rates.
///
/// Used wherever a player is shown as active (player cards). Animates only
/// while in the visual tree, so cards that scroll away or collapse cost nothing.
/// </summary>
public sealed partial class NowPlayingBars : UserControl
{
    public NowPlayingBars()
    {
        InitializeComponent();
        Loaded   += (_, _) => Sync();
        Unloaded += (_, _) => Pulse.Stop();
        // A collapsed instance (a row that is not the current track) must not keep animating in the background
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
    }

    private void Sync()
    {
        if (IsLoaded && Visibility == Microsoft.UI.Xaml.Visibility.Visible) Pulse.Begin();
        else Pulse.Stop();
    }
}
