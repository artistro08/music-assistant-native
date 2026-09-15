using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicAssistant.Api;

namespace MusicAssistant.Pages;

/// <summary>
/// Full-window Now Playing view: large artwork on the left, the active player's queue on the right, and the transport
/// bar at the bottom. Opened from the player bar.
///
/// The queue is three separate lists so a drag can never cross sections: already played tracks (dimmed), the track
/// playing now, and Up Next. Only Up Next can be reordered. The first two sit in Up Next's header so all three scroll
/// together while Up Next stays virtualized.
/// </summary>
public sealed partial class QueuePage : Page
{
    private ObservableCollection<QueueItem> upNext = [];
    private List<QueueItem> allItems = [];
    private string? loadedQueueId;
    private int     loadedCount  = -1;
    private int     currentIndex = -1;
    private string? lastImageUrl;
    private int     dragFromPosition = -1;

    /// <summary>From drag start until the server confirmed the move; periodic reloads would undo the drop.</summary>
    private bool    reordering;

    /// <summary>Bumped per fetch and per drag, so an older or overlapping fetch cannot repaint over newer state.</summary>
    private int     loadVersion;

    /// <summary>Fetches running; a periodic reload waits for the fetch in flight; on a slow connection each tick would otherwise supersede the last and none would land.</summary>
    private int     inFlight;

    /// <summary>Creates the Now Playing page and follows app state changes while the page is in the visual tree.</summary>
    public QueuePage()
    {
        InitializeComponent();

        // Subscribed for the page's time in the tree, not per navigation: the window closes the panel by clearing
        // the frame's content, which raises Unloaded but never OnNavigatedFrom, and a leaked subscription would
        // keep an invisible page refetching the queue once a second.
        Loaded   += (_, _) =>
        {
            App.StateChanged += OnStateChanged;
            _ = LoadAsync(force: true);
        };
        Unloaded += (_, _) => App.StateChanged -= OnStateChanged;
    }

    private static PlayerQueue? Queue
        => App.ActivePlayer is { } p && App.Client.Queues.TryGetValue(App.Client.QueueIdFor(p), out PlayerQueue? q) ? q : null;

    private void OnStateChanged() => _ = LoadAsync(force: false);

    // =========================================================================
    // RENDER
    // =========================================================================

    private async Task LoadAsync(bool force)
    {
        PlayerQueue? queue = Queue;
        QueueItem? item  = queue?.CurrentItem;
        PlayerMedia? media = App.ActivePlayer?.CurrentMedia;

        TitleText.Text  = item?.Title ?? media?.Title ?? "Nothing playing";
        ArtistText.Text = item?.MediaItem?.ArtistsText ?? media?.Artist ?? "";

        string? imageUrl = item is not null ? App.Client.ImageUrl(item.FindImage(), 512) : media?.ImageUrl;
        if (imageUrl != lastImageUrl)
        {
            lastImageUrl = imageUrl;
            Templates.Show(ArtImage, imageUrl, 380);
        }

        AutoplayToggle.IsChecked  = queue?.AutoplayEnabled == true;
        CrossfadeToggle.IsChecked = queue?.CrossfadeEnabled == true;
        AutoplayToggle.IsEnabled  = CrossfadeToggle.IsEnabled = queue is not null;

        if (queue is null)
        {
            PlayedList.ItemsSource = NowPlayingList.ItemsSource = UpNextList.ItemsSource = null;
            NowPlayingSection.Visibility = UpNextHeader.Visibility = Visibility.Collapsed;
            Busy.IsActive = false;
            Busy.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        // Pause/play toggles the level bars without changing the queue, so refresh them every state change.
        UpdateNowPlaying();

        // A reorder in flight owns the list until the server has answered; reloading now would snap the row back.
        if (reordering && !force) return;

        // Refetch items only when the queue identity, length or position changed, or something else reordered it.
        bool changed = force || (queue.QueueId != loadedQueueId) || (queue.Items != loadedCount) || ((queue.CurrentIndex ?? -1) != currentIndex)
            || (queue.NextItem?.QueueItemId != upNext.FirstOrDefault()?.QueueItemId);

        // The next state change re-checks once this fetch has landed.
        if (!changed || ((inFlight > 0) && !force)) return;

        int version = ++loadVersion;
        inFlight++;
        try
        {
            List<QueueItem> items = await App.Client.GetQueueItemsAsync(queue.QueueId);

            // A newer fetch or a drag started meanwhile.
            if (version != loadVersion) return;
            loadedQueueId = queue.QueueId;
            loadedCount   = queue.Items;
            currentIndex  = queue.CurrentIndex ?? -1;
            allItems      = items;

            bool playing = (currentIndex >= 0) && (currentIndex < items.Count);
            QueueItem? current = playing ? items[currentIndex] : null;
            upNext = new ObservableCollection<QueueItem>(playing ? items.Skip(currentIndex + 1) : items);

            PlayedList.ItemsSource     = playing ? items.Take(currentIndex).ToList() : null;
            NowPlayingList.ItemsSource = current is null ? null : new List<QueueItem> { current };
            UpNextList.ItemsSource     = upNext;

            // Selection draws the accent line on the current track.
            if (current is not null) NowPlayingList.SelectedIndex = 0;

            PlayedList.Visibility        = playing && (currentIndex > 0) ? Visibility.Visible : Visibility.Collapsed;
            NowPlayingSection.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            UpNextHeader.Visibility      = upNext.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpNextText.Text              = $"UP NEXT   {upNext.Count}";
            EmptyText.Visibility         = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateNowPlaying();

            // Open with the current track at the top; earlier tracks are above it. The new lists have to be laid out
            // first, or the header has no position yet and the request scrolls nowhere.
            if (playing)
            {
                UpNextList.UpdateLayout();
                NowPlayingSection.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
            }
        }
        catch (ApiException ex)
        {
            if (version == loadVersion) App.Window.ShowMessage(ex.Message);
        }
        finally
        {
            inFlight--;
            if (version == loadVersion)
            {
                Busy.IsActive = false;
                Busy.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Light the level bars on the row playing right now; clear them everywhere else.</summary>
    private void UpdateNowPlaying()
    {
        bool playing = App.ActivePlayer?.IsPlaying == true;
        foreach (QueueItem item in allItems)
        {
            item.IsNowPlaying = playing && (item.SortIndex == currentIndex);
        }
    }

    // =========================================================================
    // REORDER (drag and drop, Up Next only)
    // =========================================================================

    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not QueueItem item)
        {
            e.Cancel = true;
            return;
        }
        reordering       = true;

        // Drop any fetch already in flight so it cannot replace the list under the drag.
        loadVersion++;
        dragFromPosition = upNext.IndexOf(item);
    }

    private async void OnDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            if (args.Items.FirstOrDefault() is not QueueItem item || (dragFromPosition < 0) || Queue is not { } queue) return;

            // Up Next holds only tracks after the current one, so a shift inside it is the same shift in the whole queue.
            int shift = upNext.IndexOf(item) - dragFromPosition;
            if (shift == 0) return;

            // The server owns the order; ask it to move and reload from its answer.
            await Run(() => App.Client.QueueCommandAsync(queue.QueueId, "move_item", new { queue_item_id = item.QueueItemId, pos_shift = shift }));
            await LoadAsync(force: true);
        }
        finally
        {
            dragFromPosition = -1;
            reordering       = false;
        }
    }

    // =========================================================================
    // ACTIONS
    // =========================================================================

    private void OnClose(object sender, RoutedEventArgs e) => App.Window.ToggleQueue();

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueItem item) _ = App.PlayQueueItemAsync(item);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Queue is not { } queue) return;
        _ = Run(() => App.Client.QueueCommandAsync(queue.QueueId, "clear"));
    }

    /// <summary>Lists every other visible player as a place to move the queue to.</summary>
    /// <param name="sender">The transfer menu.</param>
    /// <param name="e">Unused.</param>
    private void OnTransferMenuOpening(object sender, object e)
    {
        TransferMenu.Items.Clear();
        if ((Queue is not { } queue) || (App.ActivePlayer is not { } source))
        {
            TransferMenu.Items.Add(new MenuFlyoutItem { Text = "No queue to transfer", IsEnabled = false });
            return;
        }

        foreach (Player target in App.Client.Players.Values.Where(p => p.IsVisible && (p.PlayerId != source.PlayerId)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entry = new MenuFlyoutItem { Text = target.DisplayName };
            entry.Click += (_, _) => _ = TransferAsync(queue, target);
            TransferMenu.Items.Add(entry);
        }
    }

    /// <summary>Moves the queue to the target player's queue, then follows it there so this view keeps showing the music.</summary>
    /// <param name="queue">The queue shown now.</param>
    /// <param name="target">The player that takes the queue over.</param>
    /// <returns>A task that completes once the move is done or its error is shown.</returns>
    private static Task TransferAsync(PlayerQueue queue, Player target) => Run(async () =>
    {
        await App.Client.TransferQueueAsync(queue.QueueId, App.Client.QueueIdFor(target));
        App.SetActivePlayer(target.PlayerId);
    });
    private void OnAutoplay(object sender, RoutedEventArgs e)
    {
        if (Queue is not { } queue) return;
        _ = Run(() => App.Client.QueueCommandAsync(queue.QueueId, "autoplay", new { autoplay_enabled = AutoplayToggle.IsChecked == true }));
    }

    private void OnCrossfade(object sender, RoutedEventArgs e)
    {
        if (Queue is not { } queue) return;
        _ = Run(() => App.Client.QueueCommandAsync(queue.QueueId, "crossfade", new { crossfade_enabled = CrossfadeToggle.IsChecked == true }));
    }

    private static async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Window.ShowMessage(ex.Message);
        }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => Templates.FadeIn((UIElement)sender);
}
