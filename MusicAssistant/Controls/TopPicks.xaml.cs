using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicAssistant.Api;

namespace MusicAssistant.Controls;

/// <summary>
/// "Top Picks for You" collage: a large lead tile plus columns of two smaller
/// tiles, filled with an interleave of the recommendation rows so every
/// enabled row contributes, and recently played items fill any shortfall.
/// Tiles are created once; resizing only stretches them.
/// </summary>
public sealed partial class TopPicks : UserControl
{
    private const int    LeadSpan     = 2;   // lead tile is as wide as two tile columns
    private const int    Columns      = 3;   // columns of two tiles after the lead (2 + 3 = 5 units per row)
    private const int    MaxPicks     = 25;

    private readonly List<ContentControl> tiles = [];
    private List<MediaItem> picks = [];
    private int page;

    public TopPicks()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildTiles();
    }

    /// <summary>Build the pick list from rows (each with its title and items) and a fallback list.</summary>
    public void Load(IEnumerable<(string title, List<MediaItem> items)> rows, List<MediaItem> fallback)
    {
        var seen   = new HashSet<string>();
        var result = new List<MediaItem>();
        var queues = rows.Where(r => r.items.Count > 0).Select(r => (r.title, queue: new Queue<MediaItem>(r.items))).ToList();

        // Round-robin across rows so the collage mixes sources
        while (queues.Any(q => q.queue.Count > 0) && result.Count < MaxPicks)
        {
            foreach (var (title, queue) in queues)
            {
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    if (!seen.Add(item.Uri)) continue;
                    item.Tag = title;
                    result.Add(item);
                    break;
                }
            }
        }

        foreach (var item in fallback)
        {
            if (result.Count >= MaxPicks) break;
            if (!seen.Add(item.Uri)) continue;
            item.Tag = "Recently played";
            result.Add(item);
        }

        picks = result;
        page  = 0;
        Visibility = picks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Fill();
    }

    // Layout: built once

    private void BuildTiles()
    {
        if (tiles.Count > 0) return;

        const int columns = Columns;
        var template = (DataTemplate)Application.Current.Resources["HeroCardTemplate"];

        Collage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LeadSpan, GridUnitType.Star) });
        for (var c = 0; c < columns; c++) Collage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Lead tile, then column by column top to bottom
        AddTile(template, column: 0, row: 0, rowSpan: 2);
        for (var c = 1; c <= columns; c++)
        {
            AddTile(template, c, 0, 1);
            AddTile(template, c, 1, 1);
        }
        Fill();
    }

    private void AddTile(DataTemplate template, int column, int row, int rowSpan)
    {
        var tile = new ContentControl
        {
            ContentTemplate            = template,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment   = VerticalAlignment.Stretch,
            IsTabStop                  = true,
            UseSystemFocusVisuals      = true,
        };
        tile.Tapped  += (s, _) => Open(((ContentControl)s).Content);
        tile.KeyDown += (s, e) => { if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space) Open(((ContentControl)s).Content); };
        Grid.SetColumn(tile, column);
        Grid.SetRow(tile, row);
        Grid.SetRowSpan(tile, rowSpan);
        Collage.Children.Add(tile);
        tiles.Add(tile);
    }

    // Paging: each page fills the same tiles with the next slice of picks

    private int PerPage   => Math.Max(1, tiles.Count);
    private int PageCount => Math.Max(1, (int)Math.Ceiling(picks.Count / (double)PerPage));

    private void Fill()
    {
        if (tiles.Count == 0) return;
        page = Math.Clamp(page, 0, PageCount - 1);

        for (var i = 0; i < tiles.Count; i++)
        {
            var index = page * PerPage + i;
            var item  = index < picks.Count ? picks[index] : null;
            tiles[i].Content    = item;
            tiles[i].Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
            tiles[i].IsTabStop  = item is not null;
            AutomationProperties.SetName(tiles[i], item?.Name ?? "");
        }

        Pager.Visibility     = PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text        = $"{page + 1} / {PageCount}";
        PrevButton.IsEnabled = page > 0;
        NextButton.IsEnabled = page < PageCount - 1;
    }

    /// <summary>Re-run every tile's template so art resolved after the first bind shows (the image binding is OneTime).</summary>
    public void Rebind()
    {
        foreach (var tile in tiles)
        {
            var item = tile.Content;
            if (item is null) continue;
            tile.Content = null;
            tile.Content = item;
        }
    }

    private void OnPrev(object sender, RoutedEventArgs e) => TurnPage(-1);
    private void OnNext(object sender, RoutedEventArgs e) => TurnPage(+1);

    private void TurnPage(int direction)
    {
        var target = page + direction;
        if (target < 0 || target >= PageCount) return;
        page = target;
        Fill();
        Paging.Slide(Collage, direction);
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var step = Paging.WheelStep(e, this);
        if (step == 0) return;
        e.Handled = true;
        TurnPage(step);
    }

    private static void Open(object? content)
    {
        if (content is MediaItem item) _ = App.OpenAsync(item);
    }
}
