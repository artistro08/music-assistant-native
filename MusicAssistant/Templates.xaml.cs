using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicAssistant.Api;

namespace MusicAssistant;

/// <summary>Shared item templates plus the context-menu handlers they reference.</summary>
public sealed partial class Templates : ResourceDictionary
{
    public Templates()
    {
        InitializeComponent();
    }

    /// <summary>
    /// x:Bind helper: a null or unusable URL yields no image instead of a binding crash.
    /// Images are decoded at the size they are shown (logical pixels) to keep memory low.
    /// </summary>
    public static ImageSource? ToImage(string? url) => Decode(url, 184);
    public static ImageSource? ToLargeImage(string? url) => Decode(url, 480);
    public static ImageSource? ToThumb(string? url) => Decode(url, 48);

    public static BitmapImage? Decode(string? url, int logicalWidth)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "data")) return null;
        return new BitmapImage(uri) { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = logicalWidth };
    }

    // Queue row menu

    private static QueueItem? QueueItemOf(object sender) => (sender as FrameworkElement)?.DataContext as QueueItem;

    private async void OnQueuePlayHere(object sender, RoutedEventArgs e)
    {
        if (QueueItemOf(sender) is not { } item) return;
        try { await App.Client.QueueCommandAsync(item.QueueId, "play_index", new { index = item.QueueItemId }); }
        catch (Exception ex) { App.Window.ShowMessage(ex.Message); }
    }

    private void OnQueuePlayNext(object sender, RoutedEventArgs e)
        => QueueAction(sender, item => App.Client.QueueCommandAsync(item.QueueId, "move_item", new { queue_item_id = item.QueueItemId, pos_shift = 0 }));

    private void OnQueueMoveEnd(object sender, RoutedEventArgs e)
        => QueueAction(sender, item => App.Client.QueueCommandAsync(item.QueueId, "move_item_end", new { queue_item_id = item.QueueItemId }));

    private void OnQueueRemove(object sender, RoutedEventArgs e)
        => QueueAction(sender, item => App.Client.QueueCommandAsync(item.QueueId, "delete_item", new { item_id_or_index = item.QueueItemId }));

    private static async void QueueAction(object sender, Func<QueueItem, Task> action)
    {
        if (QueueItemOf(sender) is not { } item) return;
        try { await action(item); }
        catch (Exception ex) { App.Window.ShowMessage(ex.Message); }
    }

    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Artwork starts transparent and fades in once decoded, so cards never pop.</summary>
    public static void FadeIn(UIElement element)
    {
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(300)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => FadeIn((UIElement)sender);

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Content is MediaItem item) _ = App.OpenAsync(item);
    }

    /// <summary>
    /// Keeps a card's artwork area square, with or without an image: the first
    /// child of the card is the art box and its height follows the card width.
    /// Driven by the card's width, so the art box's own height change cannot loop.
    /// </summary>
    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5) return;
        if (((Panel)sender).Children[0] is FrameworkElement art) art.Height = e.NewSize.Width;
    }

    /// <summary>Accent outline for the active player card, subtle stroke otherwise.</summary>
    public static Brush PlayerBorder(string playerId)
        => (Brush)Application.Current.Resources[playerId == App.Settings.ActivePlayerId ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];

    private static MediaItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as MediaItem;

    private async void OnPlayNow(object sender, RoutedEventArgs e)        { if (ItemOf(sender) is { } item) await App.PlayAsync(item, "play"); }
    private async void OnPlayNext(object sender, RoutedEventArgs e)       { if (ItemOf(sender) is { } item) await App.PlayAsync(item, "next"); }
    private async void OnAddToQueue(object sender, RoutedEventArgs e)     { if (ItemOf(sender) is { } item) await App.PlayAsync(item, "add"); }
    private async void OnToggleFavorite(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) await App.ToggleFavoriteAsync(item); }
}
