using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicAssistant.Api;

namespace MusicAssistant.Pages;

/// <summary>
/// Full-window Now Playing view: large artwork on the left, the active
/// player's queue on the right split into played / NOW PLAYING / UP NEXT,
/// and the transport bar at the bottom. Opened from the player bar.
/// </summary>
public sealed partial class QueuePage : Page
{
    private readonly QueueTemplateSelector selector;

    private ObservableCollection<object> rows = [];
    private string? loadedQueueId;
    private int     loadedCount = -1;
    private int     currentIndex = -1;
    private string? lastImageUrl;
    private int     dragFromPosition = -1;

    public QueuePage()
    {
        InitializeComponent();
        selector = new QueueTemplateSelector
        {
            Item   = (DataTemplate)Application.Current.Resources["QueueRowTemplate"],
            Header = (DataTemplate)Resources["QueueHeaderTemplate"],
        };
        List.ItemTemplateSelector = selector;

        // Subscribed for the page's time in the tree, not per navigation: the window closes the panel by clearing
        // the frame's content, which raises Unloaded but never OnNavigatedFrom, and a leaked subscription would
        // keep an invisible page refetching the queue once a second.
        Loaded   += (_, _) => { App.StateChanged += OnStateChanged; _ = LoadAsync(force: true); };
        Unloaded += (_, _) => App.StateChanged -= OnStateChanged;
    }

    private static PlayerQueue? Queue
        => App.ActivePlayer is { } p && App.Client.Queues.TryGetValue(App.Client.QueueIdFor(p), out var q) ? q : null;

    private void OnStateChanged() => _ = LoadAsync(force: false);

    // =========================================================================
    // RENDER
    // =========================================================================

    private async Task LoadAsync(bool force)
    {
        var queue = Queue;
        var item  = queue?.CurrentItem;
        var media = App.ActivePlayer?.CurrentMedia;

        TitleText.Text  = item?.Name ?? media?.Title ?? "Nothing playing";
        ArtistText.Text = item?.MediaItem?.ArtistsText ?? media?.Artist ?? "";

        var imageUrl = item is not null ? App.Client.ImageUrl(item.FindImage(), 512) : media?.ImageUrl;
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
            List.ItemsSource = null;
            Busy.IsActive = false; Busy.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        // Refetch items only when the queue identity, length or position changed, or something else reordered it
        var changed = force || queue.QueueId != loadedQueueId || queue.Items != loadedCount || (queue.CurrentIndex ?? -1) != currentIndex
            || queue.NextItem?.QueueItemId != rows.OfType<QueueItem>().ElementAtOrDefault(currentIndex + 1)?.QueueItemId;

        // Pause/play toggles the level bars without changing the queue, so refresh them every state change
        UpdateNowPlaying();
        if (!changed) return;

        try
        {
            var items = await App.Client.GetQueueItemsAsync(queue.QueueId);
            loadedQueueId = queue.QueueId;
            loadedCount   = queue.Items;
            currentIndex  = queue.CurrentIndex ?? -1;

            rows = new ObservableCollection<object>(BuildRows(items, currentIndex));
            List.ItemsSource = rows;
            UpdateNowPlaying();
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (currentIndex >= 0 && currentIndex < items.Count)
            {
                var current = items[currentIndex];
                List.SelectedItem = current;
                List.ScrollIntoView(current, ScrollIntoViewAlignment.Leading);
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

    /// <summary>Light the level bars on the row playing right now; clear them everywhere else.</summary>
    private void UpdateNowPlaying()
    {
        var playing = App.ActivePlayer?.IsPlaying == true;
        foreach (var item in rows.OfType<QueueItem>())
        {
            item.IsNowPlaying = playing && item.SortIndex == currentIndex;
        }
    }

    /// <summary>Queue items interleaved with section header strings, in display order.</summary>
    private static List<object> BuildRows(List<QueueItem> items, int currentIndex)
    {
        var rows = new List<object>(items.Count + 2);
        for (var i = 0; i < items.Count; i++)
        {
            if (i == currentIndex)     rows.Add("NOW PLAYING");
            if (i == currentIndex + 1) rows.Add($"UP NEXT   {items.Count - i}");
            rows.Add(items[i]);
        }
        return rows;
    }

    /// <summary>Dim rows that already played; headers are not selectable.</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is string)
        {
            args.ItemContainer.IsHitTestVisible = false;
            args.ItemContainer.Opacity = 1;
            return;
        }

        args.ItemContainer.IsHitTestVisible = true;
        var index = args.Item is QueueItem item ? item.SortIndex : 0;
        args.ItemContainer.Opacity = currentIndex >= 0 && index < currentIndex ? 0.5 : 1;
    }

    // =========================================================================
    // REORDER (drag and drop)
    // =========================================================================

    /// <summary>Position of a queue item counting queue items only (section headers excluded).</summary>
    private int QueuePositionOf(QueueItem item) => rows.OfType<QueueItem>().ToList().IndexOf(item);

    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not QueueItem item) { e.Cancel = true; return; }
        dragFromPosition = QueuePositionOf(item);
    }

    private async void OnDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.Items.FirstOrDefault() is not QueueItem item || dragFromPosition < 0 || Queue is not { } queue) return;

        var shift = QueuePositionOf(item) - dragFromPosition;
        dragFromPosition = -1;
        if (shift == 0) return;

        // The server owns the order; ask it to move and reload from its answer
        await Run(() => App.Client.QueueCommandAsync(queue.QueueId, "move_item", new { queue_item_id = item.QueueItemId, pos_shift = shift }));
        await LoadAsync(force: true);
    }

    // =========================================================================
    // ACTIONS
    // =========================================================================

    private void OnClose(object sender, RoutedEventArgs e) => App.Window.ToggleQueue();

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QueueItem item || Queue is not { } queue) return;
        _ = Run(() => App.Client.QueueCommandAsync(queue.QueueId, "play_index", new { index = item.QueueItemId }));
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Queue is not { } queue) return;
        _ = Run(() => App.Client.QueueCommandAsync(queue.QueueId, "clear"));
    }

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
        try { await action(); }
        catch (Exception ex) { App.Window.ShowMessage(ex.Message); }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => Templates.FadeIn((UIElement)sender);
}

/// <summary>Picks the header template for section strings and the row template for queue items.</summary>
public sealed class QueueTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Item   { get; set; }
    public DataTemplate? Header { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is string ? Header : Item;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
