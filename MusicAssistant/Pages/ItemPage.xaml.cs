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

    public ItemPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not MediaItem parameter) return;
        if (parameter.Uri == item.Uri && TrackList.ItemsSource is not null) return;   // back/forward to the same item
        item = parameter;
        Render();
        _ = LoadAsync();
    }

    private void Render()
    {
        TypeIcon.Glyph    = item.TypeGlyph;
        TypeText.Text     = item.MediaType.ToUpperInvariant();
        NameText.Text     = item.Name;
        SubtitleText.Text = item.SubtitleText;
        FavoriteToggle.IsChecked = item.Favorite;

        var description = item.Metadata?.Description;
        DescriptionText.Text       = description ?? "";
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;

        ArtImage.Source = Templates.Decode(item.LargeImageUrl, 200);
    }

    private async Task LoadAsync()
    {
        try
        {
            // Item mappings from lists are thin; fetch the full item for metadata and favorite state
            if (item.Metadata is null && item.MediaType != "genre")
            {
                item = await App.Client.GetItemAsync(item.MediaType, item.ItemId, item.Provider);
                Render();
            }

            switch (item.MediaType)
            {
                case "podcast":
                    ShowTracks("Episodes", await App.Client.GetPodcastEpisodesAsync(item.ItemId, item.Provider));
                    break;
                case "genre":
                    ShowTracks("Tracks", await App.Client.GetGenreTracksAsync(item.ItemId));
                    break;
                case "audiobook":
                    break;
                case "album":
                    ShowTracks("Tracks", await App.Client.GetAlbumTracksAsync(item.ItemId, item.Provider));
                    break;
                case "playlist":
                    ShowTracks("Tracks", await App.Client.GetPlaylistTracksAsync(item.ItemId, item.Provider));
                    break;
                case "artist":
                    var tracksTask = App.Client.GetArtistTopTracksAsync(item.ItemId, item.Provider);
                    var albumsTask = App.Client.GetArtistAlbumsAsync(item.ItemId, item.Provider);
                    await Task.WhenAll(tracksTask, albumsTask);
                    ShowTracks("Top tracks", tracksTask.Result);
                    AlbumsRow.Items      = albumsTask.Result.OrderByDescending(a => a.Year ?? 0).ToList();
                    AlbumsRow.Visibility = albumsTask.Result.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
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

    private void ShowTracks(string title, List<MediaItem> tracks)
    {
        TracksTitle.Text       = title;
        TrackList.ItemsSource  = tracks;
        TracksTitle.Visibility = tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text         = $"No {title.ToLowerInvariant()} found for this {item.MediaType}.";
        EmptyText.Visibility   = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Actions

    private void OnPlay(object sender, RoutedEventArgs e)       => _ = App.PlayAsync(item, "play");
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

        // Albums, playlists and podcasts play in context starting at the clicked item; artist and genre tracks play alone
        _ = item.MediaType is "artist" or "genre"
            ? App.PlayAsync(track, "play")
            : App.PlayAsync(item, "play", startItem: track.Uri);
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => Templates.FadeIn((UIElement)sender);
}
