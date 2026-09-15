using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicAssistant.Api;
using MusicAssistant.Controls;

namespace MusicAssistant.Pages;

/// <summary>Global search across the library and all providers.</summary>
public sealed partial class SearchPage : Page
{
    /// <summary>Creates the search page and puts keyboard focus in the query box once it loads.</summary>
    public SearchPage()
    {
        InitializeComponent();
        Loaded += (_, _) => QueryBox.Focus(FocusState.Programmatic);
    }

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string query = args.QueryText.Trim();
        if (query.Length < 2) return;

        Busy.IsActive = true;
        Busy.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            SearchResults results = await App.Client.SearchAsync(query);

            Fill(ArtistsRow,   results.Artists);
            Fill(AlbumsRow,    results.Albums);
            Fill(PlaylistsRow, results.Playlists);
            Fill(RadioRow,     results.Radio);

            TrackList.ItemsSource    = results.Tracks;
            TracksSection.Visibility = results.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool any = results.Artists.Count + results.Albums.Count + results.Playlists.Count + results.Radio.Count + results.Tracks.Count > 0;
            EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (ApiException ex)
        {
            App.Window.ShowMessage(ex.Message);
        }
        finally
        {
            Busy.IsActive = false;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private static void Fill(MediaRow row, List<MediaItem> items)
    {
        row.Items      = items;
        row.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItem track) _ = App.PlayAsync(track);
    }
}
