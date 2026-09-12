using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicAssistant.Api;

namespace MusicAssistant.Pages;

/// <summary>
/// Library listing for one media type (artists, albums, tracks, playlists, radios).
///
/// Cards for visual types, a track list for tracks. Supports a text filter,
/// favorites-only and paged loading.
/// </summary>
public sealed partial class LibraryPage : Page
{
    private const int PageSize = 100;

    private readonly ObservableCollection<MediaItem> items = [];
    private string mediaType = "albums";
    private int    offset;

    public LibraryPage()
    {
        InitializeComponent();
        CardGrid.ItemsSource = items;
        List.ItemsSource = items;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var requested = e.Parameter as string ?? "albums";

        // Coming back to the same listing: keep items and scroll position
        if (requested == mediaType && items.Count > 0) return;

        mediaType = requested;
        TitleText.Text = mediaType switch
        {
            "artists"   => "Artists",
            "albums"    => "Albums",
            "tracks"    => "Tracks",
            "playlists" => "Playlists",
            "radios"     => "Radio",
            "audiobooks" => "Audiobooks",
            "podcasts"   => "Podcasts",
            "genres"     => "Genres",
            _            => mediaType,
        };

        var asList = mediaType == "tracks";
        List.Visibility = asList ? Visibility.Visible : Visibility.Collapsed;
        CardGrid.Visibility = asList ? Visibility.Collapsed : Visibility.Visible;

        // The footer element can only live in one list at a time
        ((Grid)Content).Children.Remove(Footer);
        List.Footer     = asList ? Footer : null;
        CardGrid.Footer = asList ? null : Footer;

        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        items.Clear();
        offset = 0;
        EmptyState.Visibility = Visibility.Collapsed;
        await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        Busy.IsActive = true; Busy.Visibility = Visibility.Visible;
        MoreButton.Visibility = Visibility.Collapsed;
        try
        {
            var page = await App.Client.GetLibraryItemsAsync(mediaType, SearchBox.Text, FavoritesToggle.IsChecked == true, PageSize, offset);
            foreach (var item in page) items.Add(item);
            offset += page.Count;
            MoreButton.Visibility = page.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
            UpdateEmptyState();
        }
        catch (ApiException ex)
        {
            App.Window.ShowMessage(ex.Message);
        }
        finally
        {
            Busy.IsActive = false; Busy.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Say why the view is empty: filter, favorites, or nothing in the library yet.</summary>
    private void UpdateEmptyState()
    {
        if (items.Count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        var what = TitleText.Text.ToLowerInvariant();
        (EmptyText.Text, EmptyHint.Text) = (SearchBox.Text.Length > 0, FavoritesToggle.IsChecked == true) switch
        {
            (true, _)     => ($"No {what} match \"{SearchBox.Text}\"", "Try a different filter."),
            (false, true) => ($"No favorite {what} yet", "Mark items with the heart and they will show up here."),
            _             => ($"No {what} in your library yet", "Add a music provider or sync your library in the web interface."),
        };
        EmptyIcon.Glyph = new MediaItem { MediaType = mediaType.TrimEnd('s') }.TypeGlyph;
        EmptyState.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Six cards per row, artwork flush with the page content edges.
    /// The panel gets an explicit width of exactly six whole-pixel cells (fractional cells wrap to five
    /// at some widths), centered, with room for the 14px hover bleed on each side, so the first and
    /// last artwork line up with the header, which is 1600px max and 24px in from the window.
    /// </summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CardGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        const int columns = 6, bleed = 14, gap = 12, textBlock = 60;   // gap = BareGridViewItemStyle margin, bleed = gap/2 + card padding

        var content   = Math.Min(e.NewSize.Width - CardGrid.Padding.Left - CardGrid.Padding.Right, (double)Application.Current.Resources["ContentMaxWidth"]);
        var available = content + 2 * bleed;
        var cell      = Math.Max(48, Math.Floor((available - 1) / columns));   // 1px slack so rounding can never push a card to the next row
        if (Math.Abs(panel.ItemWidth - cell) < 0.5 && Math.Abs(panel.Width - cell * columns) < 0.5) return;

        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Width      = cell * columns;
        panel.ItemWidth  = cell;
        panel.ItemHeight = cell - gap + textBlock;   // art is (cell - gap - padding) square, plus padding and text
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => _ = ReloadAsync();

    private void OnLoadMore(object sender, RoutedEventArgs e) => _ = LoadPageAsync();

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItem item) _ = App.OpenAsync(item);
    }
}
