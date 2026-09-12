using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicAssistant.Api;

namespace MusicAssistant.Pages;

/// <summary>Provider file/folder browser. Each folder opens as a new page so the shell back button walks up.</summary>
public sealed partial class BrowsePage : Page
{
    private string? loadedPath;

    public BrowsePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // The navigation tag "browse" means root; anything else is a provider path
        var path = e.Parameter as string;
        if (path == "browse") path = null;
        if (path == loadedPath && List.ItemsSource is not null) return;   // back/forward to the same folder

        loadedPath    = path;
        PathText.Text = path ?? "All providers";
        _ = LoadAsync(path);
    }

    private async Task LoadAsync(string? path)
    {
        try
        {
            var items = await App.Client.BrowseAsync(path);
            List.ItemsSource     = items;
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItem item) _ = App.OpenAsync(item);
    }
}
