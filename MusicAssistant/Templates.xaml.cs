using System.Runtime.InteropServices.WindowsRuntime;
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

    // =========================================================================
    // ARTWORK CACHE
    // =========================================================================
    //
    // Two layers. Bytes are fetched once with our own HTTP client (retries, timeout, no dependence on the XAML image
    // loader, which drops requests under load) and kept on disk under %LOCALAPPDATA%\MusicAssistant\art, keyed by the
    // server-side image identity so the same file serves local and remote sessions and every display size. Art does
    // not change, so a file is never re-fetched, except playlist covers (marked with a #playlist fragment by the
    // model), which are refreshed when older than a day. Decoded bitmaps are kept in memory per URL and size (most
    // recent 200) so a card scrolled back into view, a recycled container or another page reuses the decoded bitmap.
    // All of it runs on the UI thread.
    private const int  CacheSize    = 200;
    private const long MaxArtBytes  = 500L * 1024 * 1024;
    private const long TrimArtBytes = 400L * 1024 * 1024;
    private static readonly int[]    RetryDelaysSeconds = [1, 3, 8];
    private static readonly TimeSpan PlaylistMaxAge     = TimeSpan.FromDays(1);
    private static readonly string   ArtDir             = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicAssistant", "art");
    private static readonly HttpClient    http      = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim downloads = new(6);   // a fast scroll through a big library must not flood the server's image resizer
    private static readonly Dictionary<string, Task<string?>> inflight = [];   // file path -> its running download
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Bitmap)>> cache = [];
    private static readonly LinkedList<(string Key, BitmapImage Bitmap)> recent = [];

    /// <summary>Raised when the cache is dropped so live controls re-resolve their art (e.g. the transport switched between local and remote, changing every image URL). UI thread.</summary>
    public static event Action? ImagesInvalidated;

    /// <summary>
    /// Drop every decoded bitmap and tell controls to re-bind. Call when the image base URL changes underneath us:
    /// remote-mode art is served by a per-session loopback proxy, so on a switch to (or from) remote the cached
    /// bitmaps point at an address that no longer answers and would stay blank until each card happened to re-bind.
    /// </summary>
    public static void InvalidateImages()
    {
        cache.Clear();
        recent.Clear();
        ImagesInvalidated?.Invoke();
    }

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

        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = logicalWidth };
        cache[key] = recent.AddFirst((key, bitmap));
        if (recent.Count > CacheSize)
        {
            cache.Remove(recent.Last!.Value.Key);
            recent.RemoveLast();
        }

        if (uri.Scheme == "data") bitmap.UriSource = uri;   // inline image, nothing to fetch or store
        else _ = LoadAsync(bitmap, uri, key);
        return bitmap;
    }

    /// <summary>
    /// Fill a bitmap from the disk cache, downloading first when needed. A bitmap that cannot be filled is evicted so
    /// the next bind starts over instead of reusing a blank.
    /// </summary>
    private static async Task LoadAsync(BitmapImage bitmap, Uri uri, string key)
    {
        if (await ArtFileAsync(uri) is not { } file)
        {
            Evict(key, bitmap);
            return;
        }

        try
        {
            // Delete sharing lets a playlist refresh replace the file while this read is open
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        }
        catch (Exception ex)
        {
            App.Debug($"Art: {uri} failed: {ex.Message}");
            try { File.Delete(file); } catch (Exception) { }   // a corrupt file must not poison every later load
            Evict(key, bitmap);
        }
    }

    /// <summary>
    /// Path of the cached file for an image URL, downloading it (or refreshing a stale playlist cover) first. Null when
    /// the art cannot be had or the URL is not http(s). Also used for the Windows media overlay thumbnail.
    /// </summary>
    public static Task<string?> ArtFileAsync(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? ArtFileAsync(uri) : Task.FromResult<string?>(null);

    private static Task<string?> ArtFileAsync(Uri uri)
    {
        var file  = Path.Combine(ArtDir, FileNameFor(uri));
        var fresh = File.Exists(file) && (uri.Fragment != "#playlist" || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < PlaylistMaxAge);
        if (fresh) return Task.FromResult<string?>(file);

        // One download per file: a second caller (another display size, a re-bind mid-download) waits for the first
        if (inflight.TryGetValue(file, out var running)) return running;
        var task = DownloadAsync(uri, file);
        inflight[file] = task;
        return task;
    }

    private static async Task<string?> DownloadAsync(Uri uri, string file)
    {
        await Task.Yield();   // never finish synchronously, so the inflight entry exists before the finally removes it
        try
        {
            var bytes = await FetchAsync(new UriBuilder(uri) { Fragment = "" }.Uri);
            if (bytes is null) return File.Exists(file) ? file : null;   // a stale playlist cover that failed to refresh keeps its old file

            Directory.CreateDirectory(ArtDir);
            var temp = file + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, file, overwrite: true);   // readers never see a half-written file
            return file;
        }
        catch (Exception ex)
        {
            App.Debug($"Art: saving {uri} failed: {ex.Message}");
            return File.Exists(file) ? file : null;
        }
        finally
        {
            inflight.Remove(file);
        }
    }

    /// <summary>GET the bytes with a few retries for network and server errors; null after the last failure or on a 4xx, which retrying cannot fix.</summary>
    private static async Task<byte[]?> FetchAsync(Uri uri)
    {
        for (var attempt = 0; ; attempt++)
        {
            await downloads.WaitAsync();
            try
            {
                using var response = await http.GetAsync(uri);
                if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync();
                App.Debug($"Art: {uri} -> {(int)response.StatusCode}");
                if ((int)response.StatusCode is >= 400 and < 500) return null;
            }
            catch (Exception ex)
            {
                App.Debug($"Art: {uri} -> {ex.Message}");
            }
            finally
            {
                downloads.Release();
            }

            if (attempt >= RetryDelaysSeconds.Length) return null;
            await Task.Delay(TimeSpan.FromSeconds(RetryDelaysSeconds[attempt]));

            // A dropped connection gets up to two minutes to come back instead of burning the retries on it
            for (var waited = 0; waited < 60 && !App.Client.IsConnected; waited++) await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Disk name from the image identity, not the address: server-proxied art keys on its /imageproxy path and query
    /// (the same behind the LAN address or the remote loopback proxy), anything else on its full URL. The decode width
    /// is not part of it; every display size decodes from the same bytes.
    /// </summary>
    private static string FileNameFor(Uri uri)
    {
        var pathAndQuery = uri.PathAndQuery;
        var proxyAt      = pathAndQuery.IndexOf("/imageproxy", StringComparison.Ordinal);
        var identity     = proxyAt >= 0 ? pathAndQuery[proxyAt..] : uri.GetLeftPart(UriPartial.Query);
        var hash         = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 16)) + ".img";
    }

    /// <summary>
    /// Keep the art folder under 500 MB and clear leftover partial downloads. Runs once at launch, off the UI thread,
    /// while the app is still connecting; a card that loses its file to the trim re-downloads it on its next bind.
    /// </summary>
    public static void TrimArtCache() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(ArtDir)) return;
            var files = new DirectoryInfo(ArtDir).GetFiles();
            foreach (var partial in files.Where(f => f.Extension == ".tmp")) partial.Delete();

            var art   = files.Where(f => f.Extension == ".img").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = art.Sum(f => f.Length);
            if (total <= MaxArtBytes) return;

            // ponytail: oldest download goes first, not least recently shown; track reads if a big library keeps evicting favorites
            foreach (var file in art)
            {
                if (total <= TrimArtBytes) break;
                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            App.Debug("Art: trim failed: " + ex.Message);
        }
    });

    private static void Evict(string key, BitmapImage bitmap)
    {
        if (cache.TryGetValue(key, out var current) && ReferenceEquals(current.Value.Bitmap, bitmap))
        {
            recent.Remove(current);
            cache.Remove(key);
        }
    }

    // Queue row menu

    private static QueueItem? QueueItemOf(object sender) => (sender as FrameworkElement)?.DataContext as QueueItem;

    private async void OnQueuePlayHere(object sender, RoutedEventArgs e)
    {
        if (QueueItemOf(sender) is { } item) await App.PlayQueueItemAsync(item);
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
    public static Visibility VisNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

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

    /// <summary>Ease a scale transform to a uniform size from wherever it is now, taking over any animation still running on it.</summary>
    public static void ScaleTo(ScaleTransform target, double to, int milliseconds, EasingMode easing)
        => AnimateBothAxes(target, () => new DoubleAnimation
        {
            To             = to,
            Duration       = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = easing },
        });

    /// <summary>Run one animation on both axes of a scale transform; <paramref name="make"/> builds the timeline for each axis.</summary>
    public static void AnimateBothAxes(ScaleTransform target, Func<Timeline> make)
    {
        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var timeline = make();
            Storyboard.SetTarget(timeline, target);
            Storyboard.SetTargetProperty(timeline, property);
            storyboard.Children.Add(timeline);
        }
        storyboard.Begin();
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => FadeIn((UIElement)sender);

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Content is MediaItem item) _ = App.OpenAsync(item);
    }

    /// <summary>Check mark visibility for the selected player in pickers.</summary>
    public static Visibility ActiveVis(string playerId) => Vis(playerId == App.Settings.ActivePlayerId);

    /// <summary>The active player card is filled with the accent color; its text and bars switch to the on-accent brushes.</summary>
    public static Brush PlayerBackground(string playerId) => PlayerBrush(playerId, "AccentFillColorDefaultBrush", "CardBackgroundFillColorDefaultBrush");
    public static Brush PlayerForeground(string playerId) => PlayerBrush(playerId, "TextOnAccentFillColorPrimaryBrush", "TextFillColorPrimaryBrush");
    public static Brush PlayerSecondary(string playerId)  => PlayerBrush(playerId, "TextOnAccentFillColorSecondaryBrush", "TextFillColorSecondaryBrush");
    public static Brush PlayerBars(string playerId)       => PlayerBrush(playerId, "TextOnAccentFillColorPrimaryBrush", "AccentTextFillColorPrimaryBrush");

    private static Brush PlayerBrush(string playerId, string activeKey, string idleKey)
        => (Brush)Application.Current.Resources[playerId == App.Settings.ActivePlayerId ? activeKey : idleKey];

    private static MediaItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as MediaItem;

    /// <summary>
    /// Track row menu (right-click or the "..." button): rebuilt for the row's track each time it opens. The page the row
    /// sits on is found by walking up from the row, so an album or playlist page can offer "play from here".
    /// </summary>
    private void OnTrackMenuOpening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || (menu.Target as FrameworkElement)?.DataContext is not MediaItem track) return;

        MediaItem? parent = null;
        for (DependencyObject? node = menu.Target; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Pages.ItemPage page) { parent = page.Item; break; }
        }
        Controls.TrackMenu.Populate(menu, track, parent);
    }

    private async void OnPlayNow(object sender, RoutedEventArgs e)        { if (ItemOf(sender) is { } item) await App.PlayAsync(item, "play"); }
    private async void OnPlayNext(object sender, RoutedEventArgs e)       { if (ItemOf(sender) is { } item) await App.PlayAsync(item, "next"); }
    private async void OnAddToQueue(object sender, RoutedEventArgs e)     { if (ItemOf(sender) is { } item) await App.PlayAsync(item, "add"); }
    private async void OnToggleFavorite(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) await App.ToggleFavoriteAsync(item); }
}
