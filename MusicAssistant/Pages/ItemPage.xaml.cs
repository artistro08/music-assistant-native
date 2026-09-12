using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using MusicAssistant.Api;

namespace MusicAssistant.Pages;

/// <summary>
/// Detail page for an album, artist or playlist.
///
/// Albums and playlists list their tracks; clicking a track starts the
/// container at that track. Artists show top tracks and an album strip.
/// </summary>
public sealed partial class ItemPage : Page
{
    private MediaItem item = new();
    private int       loadVersion;   // bumped per navigation so a slow load for the previous item cannot land on this one

    public ItemPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not MediaItem parameter) return;
        if (parameter.Uri == item.Uri && TrackList.ItemsSource is not null) return;   // back/forward to the same item
        item = parameter;
        Clear();
        Render();
        _ = LoadAsync(++loadVersion);
    }

    /// <summary>The page instance is cached, so drop the previous item's lists before the new one loads.</summary>
    private void Clear()
    {
        TrackList.ItemsSource  = null;
        TracksTitle.Visibility = Visibility.Collapsed;
        EmptyText.Visibility   = Visibility.Collapsed;
        AlbumsRow.Visibility   = Visibility.Collapsed;
        HeaderRows.Children.Clear();   // genre rows from the previous item
        Busy.IsActive          = true;
        Busy.Visibility        = Visibility.Visible;
    }

    private void Render()
    {
        TypeIcon.Glyph    = item.TypeGlyph;
        TypeText.Text     = item.MediaType.ToUpperInvariant();
        NameText.Text     = item.Name;
        SubtitleText.Text = item.SubtitleText;
        SubtitleText.Visibility = string.Equals(item.SubtitleText, item.MediaType, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed : Visibility.Visible;   // "Genre" / "Artist" would just repeat the type label above the name
        FavoriteToggle.IsChecked = item.Favorite;

        var description = item.Metadata?.Description;
        DescriptionText.Text       = description ?? "";
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;

        Templates.Show(ArtImage, item.LargeImageUrl, 200);
    }

    private async Task LoadAsync(int version)
    {
        var target = item;   // local copy: the field changes as soon as the user opens another item
        try
        {
            // Item mappings from lists are thin; fetch the full item for metadata and favorite state
            if (target.Metadata is null && target.MediaType != "genre")
            {
                target = await App.Client.GetItemAsync(target.MediaType, target.ItemId, target.Provider);
                if (version != loadVersion) return;
                item = target;
                Render();
            }

            List<MediaItem>? tracks = null;
            List<MediaItem>? albums = null;
            List<MediaItem>  rows   = [];   // genre: one row per media type
            var title = "Tracks";
            switch (target.MediaType)
            {
                case "podcast":
                    title  = "Episodes";
                    tracks = await App.Client.GetPodcastEpisodesAsync(target.ItemId, target.Provider);
                    break;
                case "genre":
                    // Overview rows (Artists, Albums, Tracks, Playlists, ...) as the web app shows them; plain track list only as a fallback
                    rows = (await App.Client.GetGenreOverviewAsync(target.ItemId, target.Provider)).Where(f => f.Items is { Count: > 0 }).ToList();
                    if (rows.Count == 0) tracks = await App.Client.GetGenreTracksAsync(target.ItemId);
                    break;
                case "audiobook":
                    break;
                case "album":
                    tracks = await App.Client.GetAlbumTracksAsync(target.ItemId, target.Provider);
                    break;
                case "playlist":
                    tracks = await App.Client.GetPlaylistTracksAsync(target.ItemId, target.Provider);
                    break;
                case "artist":
                    title = "Top tracks";
                    var tracksTask = App.Client.GetArtistTopTracksAsync(target.ItemId, target.Provider);
                    var albumsTask = App.Client.GetArtistAlbumsAsync(target.ItemId, target.Provider);
                    await Task.WhenAll(tracksTask, albumsTask);
                    tracks = tracksTask.Result;
                    albums = albumsTask.Result;
                    break;
            }
            if (version != loadVersion) return;   // user moved on while we were loading

            if (tracks is not null) ShowTracks(title, tracks);
            if (albums is not null)
            {
                AlbumsRow.Items      = albums.OrderByDescending(a => a.Year ?? 0).ToList();
                AlbumsRow.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            foreach (var folder in rows)
            {
                HeaderRows.Children.Add(new Controls.MediaRow { Title = folder.Name, Items = folder.Items! });
            }
        }
        catch (ApiException ex)
        {
            if (version == loadVersion) App.Window.ShowMessage(ex.Message);
        }
        finally
        {
            if (version == loadVersion) { Busy.IsActive = false; Busy.Visibility = Visibility.Collapsed; }
        }
    }

    private void ShowTracks(string title, List<MediaItem> tracks)
    {
        TracksTitle.Text       = title;
        TrackList.ItemsSource  = tracks;
        TracksTitle.Visibility = tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text         = $"No {title.ToLowerInvariant()} found for this {item.MediaType}.";
        EmptyText.Visibility   = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Actions

    private void OnPlay(object sender, RoutedEventArgs e)       => _ = App.PlayAsync(item);   // server default: albums and playlists replace the queue
    private void OnPlayNext(object sender, RoutedEventArgs e)   => _ = App.PlayAsync(item, "next");
    private void OnAddToQueue(object sender, RoutedEventArgs e) => _ = App.PlayAsync(item, "add");

    private async void OnShuffle(object sender, RoutedEventArgs e)
    {
        if (App.ActivePlayer is not { } player) { App.Window.ShowMessage("Select a player first."); return; }
        try { await App.Client.PlayMediaAsync(App.Client.QueueIdFor(player), item.Uri, "replace", shuffle: true); }
        catch (Exception ex) { App.Window.ShowMessage(ex.Message); }
    }

    private async void OnFavorite(object sender, RoutedEventArgs e)
    {
        await App.ToggleFavoriteAsync(item);
        FavoriteToggle.IsChecked = item.Favorite;
    }

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MediaItem track) return;

        // Albums, playlists and podcasts play in context starting at the clicked item (the server's default option,
        // which replaces the queue, so the previous song cannot linger); artist and genre tracks play alone
        _ = item.MediaType is "artist" or "genre"
            ? App.PlayAsync(track)
            : App.PlayAsync(item, startItem: track.ItemId);
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => Templates.FadeIn((UIElement)sender);
}
