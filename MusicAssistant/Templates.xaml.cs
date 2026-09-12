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

    /// <summary>
    /// Starting opacity for a card image: 1 when the bitmap is already decoded (cached), so it shows at once,
    /// 0 when it still has to load, so ImageOpened can fade it in. Also resets a recycled container's image
    /// to hidden before its new bitmap renders, which is what stopped the "visible, gone, fade in" flash.
    /// </summary>
    public static double Ready(string? url) => Decode(url, 184) is { PixelWidth: > 0 } ? 1 : 0;
    public static double ReadyLarge(string? url) => Decode(url, 480) is { PixelWidth: > 0 } ? 1 : 0;
    public static double ReadyThumb(string? url) => Decode(url, 48) is { PixelWidth: > 0 } ? 1 : 0;

    /// <summary>Code-behind counterpart of the template bindings: set an Image's source from the cache with the matching starting opacity.</summary>
    public static void Show(Image image, string? url, int logicalWidth)
    {
        var bitmap    = Decode(url, logicalWidth);
        image.Source  = bitmap;
        image.Opacity = bitmap is { PixelWidth: > 0 } ? 1 : 0;
    }

    // Decoded bitmaps are kept per URL and size (most recent 200), so a card scrolled back into view, a
    // recycled container or another page showing the same art reuses the decoded bitmap instead of
    // fetching and decoding again. Artwork URLs from the server change when the art changes.
    private const int CacheSize = 200;
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Bitmap)>> cache = [];
    private static readonly LinkedList<(string Key, BitmapImage Bitmap)> recent = [];

    public static BitmapImage? Decode(string? url, int logicalWidth)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "data")) return null;

        var key = $"{logicalWidth}|{url}";
        if (cache.TryGetValue(key, out var node))
        {
            recent.Remove(node);
            recent.AddFirst(node);
            return node.Value.Bitmap;
        }

        var bitmap = new BitmapImage(uri) { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = logicalWidth };
        cache[key] = recent.AddFirst((key, bitmap));
        if (recent.Count > CacheSize)
        {
            cache.Remove(recent.Last!.Value.Key);
            recent.RemoveLast();
        }
        return bitmap;
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

    /// <summary>
    /// Artwork that still had to load starts transparent and fades in once decoded, so cards never pop.
    /// Already visible artwork (cached bitmap, opacity set to 1 up front) is left alone. The animation
    /// releases the property when done, so a later local Opacity (recycled container) is honored.
    /// </summary>
    public static void FadeIn(UIElement element)
    {
        if (element.Opacity >= 1) return;

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(300)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => element.Opacity = 1;
        element.Opacity = 1;   // final value underneath the animation, so the frame after Stop does not flash to 0
        storyboard.Begin();
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => FadeIn((UIElement)sender);

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Content is MediaItem item) _ = App.OpenAsync(item);
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
