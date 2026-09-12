using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace MusicAssistant.Controls;

/// <summary>
/// Turns horizontal wheel / trackpad input into page steps for the pagers.
///
/// Trackpads deliver many tiny deltas, so movement is accumulated per control
/// and one step fires once a full notch (120) is reached, followed by a short
/// cooldown so a single swipe does not skip several pages.
/// </summary>
public static class Paging
{
    private const int    Notch    = 120;
    private const double Cooldown = 250;   // milliseconds

    private sealed class Progress { public int Accumulated; public DateTime LastStep; }

    // Weak keys: rows come and go with navigation and must not be kept alive by their scroll bookkeeping
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, Progress> state = new();

    /// <summary>+1 for next page, -1 for previous, 0 when the event is not a horizontal scroll or not yet a full step.</summary>
    public static int WheelStep(PointerRoutedEventArgs e, UIElement owner)
    {
        var point = e.GetCurrentPoint(owner);
        var props = point.Properties;
        var shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;

        if (!props.IsHorizontalMouseWheel && !shift) return 0;

        // Horizontal wheel: positive = right. Shift + vertical wheel: wheel down (negative) = next.
        var delta = props.IsHorizontalMouseWheel ? props.MouseWheelDelta : -props.MouseWheelDelta;

        var progress = state.GetOrCreateValue(owner);
        if ((DateTime.UtcNow - progress.LastStep).TotalMilliseconds < Cooldown)
        {
            progress.Accumulated = 0;
            return 0;
        }

        progress.Accumulated += delta;
        if (Math.Abs(progress.Accumulated) < Notch) return 0;

        var step = Math.Sign(progress.Accumulated);
        progress.Accumulated = 0;
        progress.LastStep    = DateTime.UtcNow;
        return step;
    }

    /// <summary>
    /// Slide the freshly filled page in from the side it came from (+1 = from the
    /// right for next, -1 = from the left for previous) with a short fade.
    /// </summary>
    public static void Slide(UIElement element, int direction)
    {
        if (direction == 0) return;

        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(280));

        var slide = new DoubleAnimation { From = direction * 80, To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
