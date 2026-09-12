using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicAssistant.Api;

namespace MusicAssistant.Controls;

/// <summary>
/// Titled, paged row of cards, Microsoft Store style.
///
/// A fixed number of slots is created once and never changes. Resizing the window only stretches
/// the slots; paging only swaps the content shown in them. Works for media
/// items (opens or plays them) and players (selects the active player).
/// </summary>
public sealed partial class MediaRow : UserControl
{
    private const int SlotCount = 5;

    private readonly List<Button> slots = [];
    private IList<object> items = [];
    private int page;

    public MediaRow()
    {
        InitializeComponent();
        ItemTemplate = (DataTemplate)Application.Current.Resources["MediaCardTemplate"];
        Loaded += (_, _) => BuildSlots();
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string? Subtitle
    {
        get => SubtitleText.Text;
        set
        {
            SubtitleText.Text       = value ?? "";
            SubtitleText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public DataTemplate ItemTemplate { get; set; }

    public IEnumerable<object> Items
    {
        get => items;
        set
        {
            items = value.ToList();
            page  = 0;
            Render();
        }
    }

    // Slots

    private void BuildSlots()
    {
        if (slots.Count > 0) return;

        for (var i = 0; i < SlotCount; i++)
        {
            Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // A subtle button gives the hover/press surface around image and text, plus keyboard and narrator support for free
            var slot = new Button
            {
                Style                      = (Style)Application.Current.Resources["SubtleButtonStyle"],
                ContentTemplate            = ItemTemplate,
                Padding                    = new Thickness(8),
                Margin                     = new Thickness(-8, -8, -8, -8),
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment   = VerticalAlignment.Top,
                CornerRadius               = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"],
            };
            slot.Click += OnSlotClick;
            Grid.SetColumn(slot, i);
            Slots.Children.Add(slot);
            slots.Add(slot);
        }
        Render();
    }

    private int PerPage   => Math.Max(1, slots.Count);
    private int PageCount => Math.Max(1, (int)Math.Ceiling(items.Count / (double)PerPage));

    private void Render()
    {
        if (slots.Count == 0) return;
        page = Math.Clamp(page, 0, PageCount - 1);

        for (var i = 0; i < slots.Count; i++)
        {
            var index = page * PerPage + i;
            var item  = index < items.Count ? items[index] : null;
            slots[i].Content    = item;
            slots[i].Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
            slots[i].IsTabStop  = item is not null;
            AutomationProperties.SetName(slots[i], item switch { MediaItem m => m.Name, Player p => p.Name, _ => "" });
        }

        Pager.Visibility     = PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text        = $"{page + 1} / {PageCount}";
        PrevButton.IsEnabled = page > 0;
        NextButton.IsEnabled = page < PageCount - 1;
    }

    private void OnPrev(object sender, RoutedEventArgs e) => TurnPage(-1);
    private void OnNext(object sender, RoutedEventArgs e) => TurnPage(+1);

    private void TurnPage(int direction)
    {
        var target = page + direction;
        if (target < 0 || target >= PageCount) return;
        page = target;
        Render();
        Paging.Slide(Slots, direction);
    }

    /// <summary>Horizontal wheel or trackpad swipe (or Shift + wheel) turns the page.</summary>
    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var step = Paging.WheelStep(e, this);
        if (step == 0) return;
        e.Handled = true;
        TurnPage(step);
    }

    // Activation

    private void OnSlotClick(object sender, RoutedEventArgs e)
    {
        switch (((Button)sender).Content)
        {
            case MediaItem item: _ = App.OpenAsync(item); break;
            case Player player:  App.SetActivePlayer(player.PlayerId); break;
        }
    }
}
