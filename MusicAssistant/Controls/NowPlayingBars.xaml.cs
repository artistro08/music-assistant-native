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
        Loaded   += (_, _) => Pulse.Begin();
        Unloaded += (_, _) => Pulse.Stop();
    }
}
