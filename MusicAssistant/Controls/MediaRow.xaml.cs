using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        // Re-render the visible page when the image cache is dropped (transport switch) so the cards re-resolve their
        // art against the new base URL. Subscribed only while in the tree, so a paged-away row does not leak.
        Loaded   += (_, _) => { BuildSlots(); Templates.ImagesInvalidated -= Rebind; Templates.ImagesInvalidated += Rebind; };
        Unloaded += (_, _) => Templates.ImagesInvalidated -= Rebind;
    }

    /// <summary>
    /// Force every visible card to re-run its template. Re-assigning the same item reference is a no-op for the
    /// content presenter, and the card's image binding is OneTime, so the content has to go through null.
    /// </summary>
    public void Rebind()
    {
        foreach (Button slot in slots)
        {
            object? item = slot.Content;
            if (item is null) continue;
            slot.Content = null;
            slot.Content = item;
        }
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

    /// <summary>Short status shown as an accent pill on the right of the header row; null hides it.</summary>
    public string? BadgeText
    {
        get => BadgeLabel.Text;
        set
        {
            BadgeLabel.Text  = value ?? "";
            Badge.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public DataTemplate ItemTemplate { get; set; }

    /// <summary>Show every item at once, wrapping into rows of five, instead of paging. No pager is shown.</summary>
    public bool ShowAll { get; set; }

    public IEnumerable<object> Items
    {
        get => items;
        set
        {
            items = [.. value];
            page  = 0;
            BuildSlots();
            Render();
        }
    }

    // Slots

    /// <summary>Slots to keep: one page, or enough full rows for every item when showing all.</summary>
    private int SlotsNeeded => ShowAll ? Math.Max(SlotCount, (int)Math.Ceiling(items.Count / (double)SlotCount) * SlotCount) : SlotCount;

    private void BuildSlots()
    {
        int needed = SlotsNeeded;
        if (slots.Count >= needed) return;   // ponytail: slots only grow; a shrinking list leaves collapsed slots behind

        while (Slots.ColumnDefinitions.Count < SlotCount)
            Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Card grids (players) have no hover bleed, so the gap is the real gap
        if (ShowAll) Slots.ColumnSpacing = Slots.RowSpacing = 12;

        for (int i = slots.Count; i < needed; i++)
        {
            int row = i / SlotCount;
            if (Slots.RowDefinitions.Count <= row) Slots.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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

            // Self-drawn cards: no hover surface behind them; a translucent overlay on top of the card tints it under
            // the pointer. Done in code rather than template visual states, which crash Microsoft.UI.Xaml on hover.
            UIElement cell = slot;
            if (ShowAll)
            {
                slot.Style   = (Style)Application.Current.Resources["PlainCardButtonStyle"];
                slot.Padding = new Thickness(0);
                slot.Margin  = new Thickness(0);

                var tint = new Border
                {
                    Background        = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                    CornerRadius      = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
                    Opacity           = 0,
                    IsHitTestVisible  = false,
                    OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) },
                };
                slot.PointerEntered += (_, _) => tint.Opacity = 1;
                slot.PointerExited  += (_, _) => tint.Opacity = 0;

                var wrapper = new Grid();
                wrapper.Children.Add(slot);
                wrapper.Children.Add(tint);
                cell = wrapper;
            }
            Grid.SetRow((FrameworkElement)cell, row);
            Grid.SetColumn((FrameworkElement)cell, i % SlotCount);
            Slots.Children.Add(cell);
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

        for (int i = 0; i < slots.Count; i++)
        {
            int index = page * PerPage + i;
            object? item  = index < items.Count ? items[index] : null;
            slots[i].Content    = item;
            slots[i].Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
            slots[i].IsTabStop  = item is not null;
            AutomationProperties.SetName(slots[i], item switch { MediaItem m => m.Name, Player p => p.DisplayName, _ => "" });
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
        int target = page + direction;
        if (target < 0 || target >= PageCount) return;
        page = target;
        Render();
        Paging.Slide(Slots, direction);
    }

    /// <summary>Horizontal wheel or trackpad swipe (or Shift + wheel) turns the page.</summary>
    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        int step = Paging.WheelStep(e, this);
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
