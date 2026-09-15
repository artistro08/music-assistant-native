using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicAssistant.Api;
using MusicAssistant.Controls;

namespace MusicAssistant.Pages;

/// <summary>Global search across the library and all providers, for the query typed in the title bar search box.</summary>
public sealed partial class SearchPage : Page
{
    /// <summary>Query of the results on screen, or of the search still loading them.</summary>
    private string? shownQuery;

    /// <summary>Creates the search page.</summary>
    public SearchPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows results for a query typed while this page is open, unless they're already on screen or loading.
    /// </summary>
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
        // Back and forward return to the results as they were left, which may be a later query than the one this page
        // was first opened with; the title bar box shows that query again.
        if (e.NavigationMode is NavigationMode.Back or NavigationMode.Forward)
        {
            if (shownQuery is not null)
            {
                App.Window.SetSearchText(shownQuery);
            }
            return;
        }

        // A new search always asks the server again, even for the query already shown, so results are never stale.
        if (e.Parameter is string query)
        {
            _ = SearchAsync(query);
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

            // Results arrive without the page changing focus, so tell screen readers they're in.
            AnnounceTitle(any ? TitleText.Text : $"{TitleText.Text}. {EmptyText.Text}");
        }
        catch (ApiException ex)
        {
            // Only the latest search reports its failure; an older one it replaced stays quiet.
            if (query != shownQuery)
            {
                return;
            }

            // Cleared so typing the same query again retries it.
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

    /// <summary>Reads a short status out through Narrator and other screen readers, without moving focus.</summary>
    /// <param name="text">The text to announce.</param>
    private void AnnounceTitle(string text)
    {
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(TitleText) ?? FrameworkElementAutomationPeer.CreatePeerForElement(TitleText);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, text, "SearchResults");
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
