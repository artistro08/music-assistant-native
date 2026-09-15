using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicAssistant.Api;
using MusicAssistant.Controls;

namespace MusicAssistant.Pages;

/// <summary>
/// Home (Discover): players row, Top Picks collage, then the server's
/// recommendation rows as paged card rows, mirroring the web frontend.
/// </summary>
public sealed partial class HomePage : Page
{
    private string playersSignature = "";
    private bool   loaded;

    /// <summary>Creates the home page with its greeting and loads the players and recommendation rows the first time it appears.</summary>
    public HomePage()
    {
        InitializeComponent();
        GreetingText.Text = Greeting();

        PlayersRow.ItemTemplate = (DataTemplate)Application.Current.Resources["PlayerCardTemplate"];

        // The page is cached for back/forward, so load only once.
        Loaded += (_, _) =>
        {
            if (!loaded)
            {
                loaded = true;
                _ = LoadAsync();
            }
        };
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e) => App.StateChanged += RefreshPlayers;

    /// <inheritdoc/>
    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.StateChanged -= RefreshPlayers;

    private static string Greeting()
    {
        string? name = App.Client.CurrentUser?.DisplayName ?? App.Client.CurrentUser?.Username;
        string part = DateTime.Now.Hour switch { < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        return string.IsNullOrEmpty(name) ? part : $"{part}, {name}";
    }

    // Players.

    /// <summary>Rebuild the players row only when something visible changed; player events arrive every second while playing.</summary>
    private void RefreshPlayers()
    {
        // Fixed order (this PC first, then by name) so a player does not jump to another page when it pauses.
        var players = App.Client.Players.Values.Where(p => p.IsVisible)
            .OrderByDescending(p => p.IsThisDevice).ThenBy(p => p.Name).ToList();

        string signature = $"{string.Join("|", players.Select(p => $"{p.PlayerId}:{p.PlaybackState}:{p.NowPlayingText}"))}#{App.Settings.ActivePlayerId}";
        if (signature == playersSignature) return;
        playersSignature = signature;

        int playing = players.Count(p => p.IsPlaying);
        PlayersRow.BadgeText  = playing == 0 ? null : $"{playing} playing";
        PlayersRow.Items      = players;
        PlayersRow.Visibility = players.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Recommendations.

    private async Task LoadAsync()
    {
        RefreshPlayers();
        try
        {
            Task<List<MediaItem>> recentTask  = App.Client.GetRecentlyPlayedAsync(20);
            Task<List<MediaItem>> foldersTask = App.Client.GetRecommendationsAsync();
            await Task.WhenAll(recentTask, foldersTask);

            var folders   = foldersTask.Result.Where(f => f.EnabledByDefault != false).ToList();
            Task<List<MediaItem>>[] itemTasks = [.. folders.Select(LoadFolderItemsAsync)];
            await Task.WhenAll(itemTasks);

            var rows = folders.Zip(itemTasks.Select(t => t.Result), (folder, items) => (folder, items)).ToList();

            Picks.Load(rows.Select(r => (r.folder.Name, r.items)), recentTask.Result);

            AddRow("Recently played", "Pick up where you left off", recentTask.Result);
            foreach ((MediaItem folder, List<MediaItem> items) in rows) AddRow(folder.Name, folder.Subtitle, items);

            // Second pass, after the page is up: fill in album cards the server sent without a cover.
            if (await FillMissingAlbumArtAsync(recentTask.Result.Concat(rows.SelectMany(r => r.items))))
            {
                foreach (MediaRow row in Rows.Children.OfType<MediaRow>()) row.Rebind();
                Picks.Rebind();
            }
        }
        catch (ApiException ex)
        {
            App.Window.ShowMessage(ex.Message);
        }
        finally
        {
            Busy.IsActive = false;
            Busy.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = (Rows.Children.Count == 0) && (Picks.Visibility == Visibility.Collapsed) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static async Task<List<MediaItem>> LoadFolderItemsAsync(MediaItem folder)
    {
        // Folders may already carry items; otherwise fetch the row on its own so one slow provider cannot block the page.
        if (folder.Items is { Count: > 0 }) return folder.Items;
        try
        {
            return await App.Client.GetRecommendationItemsAsync(folder.Provider, folder.ItemId);
        }
        catch (ApiException)
        {
            return [];
        }
    }

    /// <summary>
    /// Load the tracks of every album card the server sent without a cover; the client keeps a track's cover for the
    /// album, and every card with that album's URI resolves it on re-bind. One request per blank album per session
    /// (a filled album no longer counts as blank). True if any card gained a cover.
    /// </summary>
    private static async Task<bool> FillMissingAlbumArtAsync(IEnumerable<MediaItem> items)
    {
        bool filled = false;
        foreach (MediaItem? album in items.Where(i => (i.MediaType == "album") && (i.Uri.Length > 0) && i.FindImage() is null).DistinctBy(i => i.Uri))
        {
            try
            {
                await App.Client.GetAlbumTracksAsync(album.ItemId, album.Provider, album.Uri);
                filled |= album.FindImage() is not null;
            }
            catch (ApiException)
            {
                // No cover is not worth an error bar.
            }
        }
        return filled;
    }

    private void AddRow(string title, string? subtitle, List<MediaItem> items)
    {
        if (items.Count == 0) return;
        Rows.Children.Add(new MediaRow { Title = title, Subtitle = subtitle, Items = items });
    }
}
