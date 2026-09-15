using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicAssistant.Api;
using MusicAssistant.Controls;

namespace MusicAssistant.Pages;

/// <summary>Global search across the library and all providers, for the query typed in the title bar search box.</summary>
public sealed partial class SearchPage : Page
{
    /// <summary>Query of the results on screen, so going back to the page doesn't search again.</summary>
    private string? shownQuery;

    /// <summary>Creates the search page.</summary>
    public SearchPage()
    {
        InitializeComponent();
    }

    /// <summary>Shows results for a query, unless they're already on screen.</summary>
    /// <param name="query">The text typed in the title bar search box.</param>
    public void ShowResults(string query)
    {
        if (query == shownQuery)
        {
            return;
        }

        _ = SearchAsync(query);
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string query)
        {
            ShowResults(query);
        }
    }

    /// <summary>Searches every provider and fills the result rows.</summary>
    /// <param name="query">The text typed in the title bar search box.</param>
    /// <returns>A task that completes once the results or the error are shown.</returns>
    private async Task SearchAsync(string query)
    {
        shownQuery     = query;
        TitleText.Text = $"Results for \"{query}\"";

        Busy.IsActive        = true;
        Busy.Visibility      = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            SearchResults results = await App.Client.SearchAsync(query);

            // A newer search started while this one was loading.
            if (query != shownQuery)
            {
                return;
            }

            Fill(ArtistsRow,   results.Artists);
            Fill(AlbumsRow,    results.Albums);
            Fill(PlaylistsRow, results.Playlists);
            Fill(RadioRow,     results.Radio);

            TrackList.ItemsSource    = results.Tracks;
            TracksSection.Visibility = results.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool any = (results.Artists.Count + results.Albums.Count + results.Playlists.Count + results.Radio.Count + results.Tracks.Count) > 0;
            EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (ApiException ex)
        {
            // A failed search is retried by searching again, so the next attempt must not be skipped as already shown.
            shownQuery = null;
            App.Window.ShowMessage(ex.Message);
        }
        finally
        {
            if ((shownQuery is null) || (query == shownQuery))
            {
                Busy.IsActive   = false;
                Busy.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static void Fill(MediaRow row, List<MediaItem> items)
    {
        row.Items      = items;
        row.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItem track)
        {
            _ = App.PlayAsync(track);
        }
    }
}
