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

    /// <summary>The album, playlist, artist or other item this page shows; the track menu uses it for "Play Album from here".</summary>
    public MediaItem Item => item;

    public ItemPage()
    {
        InitializeComponent();

        // Live level bars follow whatever the active player is playing; subscribe for the page's time in the tree
        Loaded   += (_, _) => { App.StateChanged += UpdateNowPlaying; UpdateNowPlaying(); Templates.ImagesInvalidated -= OnImagesInvalidated; Templates.ImagesInvalidated += OnImagesInvalidated; };
        Unloaded += (_, _) => { App.StateChanged -= UpdateNowPlaying; Templates.ImagesInvalidated -= OnImagesInvalidated; };
    }

    /// <summary>Transport switched and the image cache was dropped: re-resolve the hero art and force the track rows to re-bind their thumbnails against the new base URL.</summary>
    private void OnImagesInvalidated()
    {
        Templates.Show(ArtImage, item.LargeImageUrl, 200);
        if (TrackList.ItemsSource is { } source)
        {
            TrackList.ItemsSource = null;
            TrackList.ItemsSource = source;
        }
    }

    /// <summary>Light the level bars on the track playing right now; clear them on every other row.</summary>
    private void UpdateNowPlaying()
    {
        if (TrackList.ItemsSource is not IEnumerable<MediaItem> tracks) return;

        string? playing = App.ActivePlayer?.IsPlaying == true ? App.ActivePlayer!.CurrentMedia?.Uri : null;
        foreach (MediaItem track in tracks)
        {
            track.IsNowPlaying = playing is not null && track.Uri == playing;
        }
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

        string? description = item.Metadata?.Description;
        DescriptionText.Text       = description ?? "";
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;

        Templates.Show(ArtImage, item.LargeImageUrl, 200);
    }

    private async Task LoadAsync(int version)
    {
        MediaItem target = item;   // local copy: the field changes as soon as the user opens another item
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
            string title = "Tracks";
            switch (target.MediaType)
            {
                case "podcast":
                    title  = "Episodes";
                    tracks = await App.Client.GetPodcastEpisodesAsync(target.ItemId, target.Provider);
                    break;
                case "genre":
                    // Overview rows (Artists, Albums, Tracks, Playlists, ...) as the web app shows them; plain track list only as a fallback
                    rows = [.. (await App.Client.GetGenreOverviewAsync(target.ItemId, target.Provider)).Where(f => f.Items is { Count: > 0 })];
                    if (rows.Count == 0) tracks = await App.Client.GetGenreTracksAsync(target.ItemId);
                    break;
                case "audiobook":
                    break;
                case "album":
                    // Loading the tracks also records a track's cover for an album the server has no image for; re-render
                    // the hero when that just gave it one (the tracks resolve it through their album link on bind)
                    bool hadCover = target.FindImage() is not null;
                    tracks = await App.Client.GetAlbumTracksAsync(target.ItemId, target.Provider, target.Uri);
                    if (!hadCover && target.FindImage() is not null && version == loadVersion) Render();
                    break;
                case "playlist":
                    tracks = await App.Client.GetPlaylistTracksAsync(target.ItemId, target.Provider);
                    break;
                case "artist":
                    title = "Top tracks";
                    Task<List<MediaItem>> tracksTask = App.Client.GetArtistTopTracksAsync(target.ItemId, target.Provider);
                    Task<List<MediaItem>> albumsTask = App.Client.GetArtistAlbumsAsync(target.ItemId, target.Provider);
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
            foreach (MediaItem folder in rows)
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
        UpdateNowPlaying();
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
        // which replaces the queue, so the previous song cannot linger); artist and genre tracks play alone.
        // loadingItem is the clicked track so its row art shows the spinner while playback starts.
        _ = item.MediaType is "artist" or "genre"
            ? App.PlayAsync(track)
            : App.PlayAsync(item, startItem: track.ItemId, loadingItem: track);
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => Templates.FadeIn((UIElement)sender);
}
