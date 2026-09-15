using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicAssistant.Api;

namespace MusicAssistant.Controls;

/// <summary>
/// The menu behind a right-click (or Shift+F10 and the menu key) on a track row, album or artist card, Top Picks tile or
/// browse folder, and behind a track row's "..." button. Modeled on the Music Assistant web app's item menu
/// (ItemContextMenu.vue): Play on, play from here or play now, play next and enqueue options, go to artist, album and
/// endless mix, library and favorites membership, and add to playlist.
///
/// It is rebuilt every time it opens, because what it offers depends on the item (its type, favorite, library
/// membership, artists, album), the page it sits on (an album or playlist offers "play from here") and the active
/// player. Anything that needs the server (library membership of a streaming item, whether an endless mix can be
/// generated) updates its entry in place once the answer arrives.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://github.com/music-assistant/frontend/blob/main/src/layouts/default/ItemContextMenu.vue
/// </remarks>
public static class ItemMenu
{
    /// <summary>Labels are the web app's English strings (src/translations/en.json).</summary>
    private static readonly (string Option, string Label, string Glyph)[] EnqueueOptions =
    [
        ("play",         "Play now (keep queue)",     "\uE768"),
        ("next",         "Play next (keep queue)",    "\uE893"),
        ("add",          "Add to the queue",          "\uE710"),
        ("replace",      "Play now (replace queue)",  "\uE768"),
        ("replace_next", "Play next (replace queue)", "\uE893"),
    ];

    /// <summary>
    /// Fill the menu for one media item. <paramref name="parent"/> is the album, playlist or other item whose page the
    /// row or card is on, if any.
    /// </summary>
    /// <param name="menu">The shared item menu, cleared and refilled.</param>
    /// <param name="item">The track, album, artist, playlist, radio station, genre or folder the menu was opened on.</param>
    /// <param name="parent">The item whose page the row or card is on, or null.</param>
    public static void Populate(MenuFlyout menu, MediaItem item, MediaItem? parent)
    {
        menu.Items.Clear();
        Player? player  = App.ActivePlayer;
        bool    canPlay = item.IsPlayable && item.IsAvailableNow;

        // Primary play action: on an album, playlist or podcast page the whole list from this item, else the item alone.
        bool fromHere = parent is { MediaType: "album" or "playlist" or "podcast" } && (parent.Uri != item.Uri);
        Func<Task> playPrimary = fromHere
            ? () => App.PlayAsync(parent!, startItem: item.ItemId, loadingItem: item)
            : () => App.PlayAsync(item);

        // Play On: start that play action on the chosen speaker, which also becomes the active player.
        if (canPlay)
        {
            var playOn = new MenuFlyoutSubItem { Text = $"Play on: {player?.DisplayName ?? "No player selected"}", Icon = Glyph("\uE7F5") };
            foreach (Player candidate in App.Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool active = candidate.PlayerId == player?.PlayerId;
                Add(playOn, candidate.DisplayName, active ? "\uE73E" : "", () =>
                {
                    App.SetActivePlayer(candidate.PlayerId);
                    return playPrimary();
                });
            }
            menu.Items.Add(playOn);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        // Playback: needs a player to play on.
        if ((player is not null) && canPlay)
        {
            string label = parent?.MediaType switch { "album" => "Play Album from here", "playlist" => "Play Playlist from here", _ => "Play from here to latest" };
            Add(menu, fromHere ? label : "Play Now", "\uE768", playPrimary);
            Add(menu, "Play Next", "\uE893", () => App.PlayAsync(item, "next"));

            var enqueue = new MenuFlyoutSubItem { Text = "Enqueue options", Icon = Glyph("\uE8FD") };
            foreach ((string option, string text, string glyph) in EnqueueOptions)
            {
                Add(enqueue, text, glyph, () => App.PlayAsync(item, option));
            }
            menu.Items.Add(enqueue);
        }

        // Navigation: artist (only when there is exactly one, like the web app), album, endless mix.
        if (item.IsAvailableNow && item.Artists is [var artist])
        {
            Add(menu, $"View artist {artist.Name}", "\uE77B", () => App.OpenAsync(artist));
        }
        if (item.IsAvailableNow && item.Album is { } album)
        {
            Add(menu, $"View album {album.Name}", "\uE93C", () => App.OpenAsync(album));
        }

        // Endless mix: the web app's radio seeds are tracks, albums, artists, genres, and playlists that aren't dynamic already.
        bool mixSeed = (item.MediaType is "track" or "album" or "artist" or "genre") || ((item.MediaType == "playlist") && (item.IsDynamic != true));
        if (mixSeed && item.IsAvailableNow)
        {
            MenuFlyoutItem mix = Add(menu, $"View {item.MediaType} endless mix", "\uE8EE", () => App.OpenAsync(EndlessMix(item)));
            _ = CheckEndlessMixAsync(mix, item);
        }

        // Library membership: a library item knows it; a streaming item is looked up.
        if (item.MediaType is "track" or "album" or "artist" or "playlist" or "radio" or "audiobook" or "podcast")
        {
            MenuFlyoutItem library = Add(menu, (item.Provider == "library") && item.IsInLibrary ? "Remove from library" : "Add to library", "\uE8F1", () => Task.CompletedTask);
            _ = WireLibraryAsync(library, item);
        }

        // Favorites (not for browse folders, which the server cannot favorite).
        if (item.MediaType != "folder")
        {
            Add(menu, item.Favorite ? "Remove from favorites" : "Add to favorites", item.Favorite ? "\uEB52" : "\uEB51", () => App.ToggleFavoriteAsync(item));
        }

        // Add to playlist: tracks, and albums, which the server unwraps into their tracks.
        if (item.MediaType is "track" or "album")
        {
            Add(menu, "Add to playlist...", "\uE710", () => ShowAddToPlaylistAsync(item, parent));
        }
    }

    // =========================================================================
    // ENDLESS MIX
    // =========================================================================

    /// <summary>
    /// The endless mix is a dynamic playlist the server's radio_playlist provider generates from a seed item, addressed
    /// as radio_playlist://playlist/&lt;seed uri&gt;. It opens on the normal playlist page.
    /// </summary>
    private static MediaItem EndlessMix(MediaItem seed) => new()
    {
        MediaType  = "playlist",
        Provider   = "radio_playlist",
        ItemId     = seed.Uri,
        Uri        = $"radio_playlist://playlist/{seed.Uri}",
        Name       = $"{seed.Name} Endless Mix",
        IsPlayable = true,
    };

    /// <summary>A mix can only be generated when a provider of the item (or, for a library item, any provider) supplies similar tracks, or for a genre; otherwise the entry is disabled.</summary>
    private static async Task CheckEndlessMixAsync(MenuFlyoutItem entry, MediaItem item)
    {
        // The server builds genre mixes itself, without a similar-tracks provider.
        if (item.MediaType == "genre")
        {
            return;
        }

        try
        {
            var providers = (await App.Client.GetProvidersCachedAsync()).ToDictionary(p => p.InstanceId);
            bool Similar(string instanceId) => providers.TryGetValue(instanceId, out ProviderInstance? provider) && provider.Supports("similar_tracks");

            bool supported = item.ProviderMappings is { Count: > 0 } mappings ? mappings.Any(m => Similar(m.ProviderInstance)) : Similar(item.Provider);
            if (!supported && (item.Provider == "library")) supported = providers.Values.Any(p => p.Supports("similar_tracks"));
            entry.IsEnabled = supported;
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Leave the entry enabled; opening the mix reports its own error.
            App.Debug($"Item menu: provider lookup failed: {ex.Message}");
        }
    }

    // =========================================================================
    // LIBRARY
    // =========================================================================

    /// <summary>
    /// Settle the library entry: resolve a streaming track to its library copy (the removal needs that id), set the
    /// label, and attach add or remove. Disabled until the answer is in.
    /// </summary>
    private static async Task WireLibraryAsync(MenuFlyoutItem entry, MediaItem item)
    {
        entry.IsEnabled = false;
        MediaItem? libraryItem;
        try
        {
            libraryItem = item.Provider == "library" ? item : await App.Client.GetLibraryItemAsync(item.MediaType, item.ItemId, item.Provider);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Debug($"Item menu: library lookup failed: {ex.Message}");
            return;
        }

        bool inLibrary = libraryItem?.IsInLibrary == true;
        entry.Text      = inLibrary ? "Remove from library" : "Add to library";
        entry.IsEnabled = true;
        entry.Click += async (_, _) =>
        {
            try
            {
                if (!inLibrary)
                {
                    await App.Client.AddToLibraryAsync(item.Uri);
                    App.Window.ShowMessage($"Added {item.Name} to the library", InfoBarSeverity.Success);
                }
                else if (await ConfirmRemoveAsync())
                {
                    await App.Client.RemoveFromLibraryAsync(libraryItem!.MediaType, libraryItem.ItemId);

                    // A favorite is always in the library, so leaving it clears that too.
                    item.Favorite = false;
                    App.Window.ShowMessage($"Removed {item.Name} from the library", InfoBarSeverity.Success);
                }
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                App.Window.ShowMessage(ex.Message);
            }
        };
    }

    private static async Task<bool> ConfirmRemoveAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot          = App.Window.Content.XamlRoot,
            Title             = "Remove from library",
            Content           = new TextBlock { Text = "Are you sure you want to delete this item from the library? Any items depending on this item will also be recursively removed. Note that this will only remove this item from the library. If this is a music file on disk, it may return on the next sync.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Remove",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // =========================================================================
    // ADD TO PLAYLIST
    // =========================================================================

    /// <summary>Pick one of the library playlists the track or album can be added to, then add it.</summary>
    private static async Task ShowAddToPlaylistAsync(MediaItem item, MediaItem? parent)
    {
        var busy   = new ProgressRing { IsActive = true, Margin = new Thickness(0, 24, 0, 24) };
        var empty  = new TextBlock { Text = "No playlists this item can be added to.", Visibility = Visibility.Collapsed, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var filter = new TextBox { PlaceholderText = "Search", Visibility = Visibility.Collapsed };
        var list   = new ListView
        {
            SelectionMode      = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            MaxHeight          = 420,
            ItemTemplate       = (DataTemplate)Application.Current.Resources["PlaylistPickRowTemplate"],
            ItemContainerStyle = (Style)Application.Current.Resources["TrackListItemStyle"],
        };

        // "Create new playlist on ..." buttons, one per provider that can make one.
        var create  = new StackPanel { Spacing = 4 };

        // Fixed: long names realized while scrolling would widen the dialog.
        var content = new StackPanel { Spacing = 12, Width = 440 };
        content.Children.Add(filter);
        content.Children.Add(busy);
        content.Children.Add(empty);
        content.Children.Add(list);
        content.Children.Add(create);

        var dialog = new ContentDialog
        {
            XamlRoot        = App.Window.Content.XamlRoot,
            Title           = "Add to playlist...",
            Content         = content,
            CloseButtonText = "Cancel",
        };

        MediaItem?        chosen   = null;
        ProviderInstance? createOn = null;
        list.ItemClick += (_, e) =>
        {
            chosen = e.ClickedItem as MediaItem;
            dialog.Hide();
        };

        Windows.Foundation.IAsyncOperation<ContentDialogResult> shown = dialog.ShowAsync();
        List<MediaItem>        targets  = [];
        List<ProviderInstance> creators = [];
        try
        {
            (targets, creators) = await PlaylistTargetsAsync(item, parent);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            empty.Text = ex.Message;
        }
        busy.IsActive     = false;
        busy.Visibility   = Visibility.Collapsed;
        empty.Visibility  = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        filter.Visibility = targets.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
        list.Visibility   = targets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        list.ItemsSource  = targets;
        filter.TextChanged += (_, _) =>
        {
            string query = filter.Text.Trim();
            list.ItemsSource = query.Length == 0 ? targets : [.. targets.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
        };
        foreach (ProviderInstance provider in creators)
        {
            var button = new Button { Content = $"Create new playlist on {provider.Name}", HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) =>
            {
                createOn = provider;
                dialog.Hide();
            };
            create.Children.Add(button);
        }

        await shown;

        // New playlist: ask for a name, create it, then add to it.
        if (createOn is not null)
        {
            string? name = await AskPlaylistNameAsync();
            if (name is null) return;
            try
            {
                chosen = await App.Client.CreatePlaylistAsync(name, createOn.InstanceId, CreateMediaTypes(createOn, item));
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                App.Window.ShowMessage(ex.Message);
                return;
            }
        }
        if (chosen is null) return;

        try
        {
            await App.Client.AddPlaylistTracksAsync(chosen.ItemId, [item.Uri]);
            App.Window.ShowMessage($"Added {item.Name} to {chosen.Name}", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Window.ShowMessage($"Could not add {item.Name} to {chosen.Name}: {ex.Message}");
        }
    }

    /// <summary>The name for a new playlist, or null when cancelled. Create stays disabled until a name is typed.</summary>
    private static async Task<string?> AskPlaylistNameAsync()
    {
        var input  = new TextBox { Header = "Enter a name for the new playlist" };
        var dialog = new ContentDialog
        {
            XamlRoot               = App.Window.Content.XamlRoot,
            Title                  = "New playlist",
            Content                = input,
            PrimaryButtonText      = "Create",
            CloseButtonText        = "Cancel",
            DefaultButton          = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = input.Text.Trim().Length > 0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    /// <summary>Media types for a new playlist, as the web app picks them: everything the provider can mix, or just this item's type.</summary>
    private static List<string> CreateMediaTypes(ProviderInstance provider, MediaItem item)
    {
        if (!provider.Supports("playlist_create_mixed")) return [item.MediaType];

        List<string> types = [];
        if (provider.Supports("playlist_create") || provider.Supports("playlist_create_tracks")) types.Add("track");
        if (provider.Supports("playlist_create_audiobooks"))                                      types.Add("audiobook");
        if (provider.Supports("playlist_create_podcast_episodes"))                                types.Add("podcast_episode");
        if (provider.Supports("playlist_create_radios"))                                          types.Add("radio");
        return types;
    }

    /// <summary>
    /// The web app's rules for which playlists are offered: available, editable by this user, not the playlist being
    /// viewed, holding the track's media type (albums go into track playlists, the server unwraps them), and on a
    /// provider that can take this track (the built-in provider, a streaming provider, or one the track itself is on).
    ///
    /// One rule stricter than the web app: the playlist's provider must support editing playlist tracks. The server
    /// refuses the add otherwise (YouTube Music does), but only in a background task after it already answered.
    ///
    /// Also returns the providers a new playlist can be created on, by the same web app rules.
    /// </summary>
    private static async Task<(List<MediaItem> Playlists, List<ProviderInstance> Creators)> PlaylistTargetsAsync(MediaItem item, MediaItem? parent)
    {
        MediaItem reference = item.ProviderMappings is not null ? item : await App.Client.GetItemByUriAsync(item.Uri);
        var providers = (await App.Client.GetProvidersCachedAsync()).ToDictionary(p => p.InstanceId);
        List<MediaItem> playlists = await App.Client.GetAllLibraryPlaylistsAsync();

        bool Fits(ProviderInstance provider) => (provider.Domain == "builtin")
            || (provider.IsStreamingProvider == true)
            || (reference.ProviderMappings?.Any(m => m.ProviderInstance == provider.InstanceId) == true);

        var result = new List<MediaItem>();
        foreach (MediaItem playlist in playlists)
        {
            if (playlist.ProviderMappings is not { Count: > 0 } mappings || !mappings.Any(m => m.Available == true)) continue;
            if (!CanEditPlaylistItems(playlist)) continue;
            if (parent is { MediaType: "playlist" } && (parent.ItemId == playlist.ItemId)) continue;

            List<string> types = playlist.SupportedMediatypes ?? ["track"];
            if (types.Contains("track")) types = [.. types, "album"];
            if (!types.Contains(reference.MediaType)) continue;

            if (!providers.TryGetValue(mappings[0].ProviderInstance, out ProviderInstance? provider)) continue;
            if (provider.Supports("playlist_tracks_edit") && Fits(provider)) result.Add(playlist);
        }

        var creators = providers.Values
            .Where(p => p.Supports("playlist_tracks_edit") && (p.Supports("playlist_create") || p.Supports("playlist_create_tracks")) && Fits(p))
            .ToList();
        return (result, creators);
    }

    /// <summary>
    /// Editable, and either without an access record (every provider playlist, and every playlist on servers that
    /// predate access control), managed by an admin, or owned by the signed-in user.
    /// </summary>
    private static bool CanEditPlaylistItems(MediaItem playlist)
    {
        if (playlist.IsEditable != true) return false;
        if (playlist.Access is not { ValueKind: JsonValueKind.Object } access) return true;
        if (App.Client.CurrentUser is not { } user) return false;
        if (user.Role == "admin") return true;
        return access.TryGetProperty("owner", out JsonElement owner) && (owner.ValueKind == JsonValueKind.String) && (owner.GetString() == user.UserId);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph };

    private static MenuFlyoutItem Add(IList<MenuFlyoutItemBase> items, string text, string glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = Glyph(glyph) };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                App.Window.ShowMessage(ex.Message);
            }
        };
        items.Add(item);
        return item;
    }

    private static MenuFlyoutItem Add(MenuFlyout menu, string text, string glyph, Func<Task> action) => Add(menu.Items, text, glyph, action);

    private static MenuFlyoutItem Add(MenuFlyoutSubItem menu, string text, string glyph, Func<Task> action) => Add(menu.Items, text, glyph, action);
}
