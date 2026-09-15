using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicAssistant.Api;

namespace MusicAssistant.Controls;

/// <summary>
/// The menu behind a track row's right-click and "..." button, modeled on the Music Assistant web app's item menu
/// (ItemContextMenu.vue): Play on, play from here or play now, enqueue options, go to artist, album and endless mix,
/// library and favorites membership, and add to playlist.
///
/// It is rebuilt every time it opens, because what it offers depends on the track (favorite, library membership,
/// artists, album), the page it sits on (an album or playlist offers "play from here") and the active player. Anything
/// that needs the server (library membership of a streaming track, whether an endless mix can be generated) updates
/// its entry in place once the answer arrives.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://github.com/music-assistant/frontend/blob/main/src/layouts/default/ItemContextMenu.vue
/// </remarks>
public static class TrackMenu
{
    // Labels are the web app's English strings (src/translations/en.json)
    private static readonly (string Option, string Label, string Glyph)[] EnqueueOptions =
    [
        ("play",         "Play now (keep queue)",     ""),
        ("next",         "Play next (keep queue)",    ""),
        ("add",          "Add to the queue",          ""),
        ("replace",      "Play now (replace queue)",  ""),
        ("replace_next", "Play next (replace queue)", ""),
    ];

    /// <summary>Fill the menu for one track. <paramref name="parent"/> is the album, playlist or other item whose page the row is on, if any.</summary>
    public static void Populate(MenuFlyout menu, MediaItem track, MediaItem? parent)
    {
        menu.Items.Clear();
        var player = App.ActivePlayer;

        // Play On: pick the player the play entries below use
        var playOn = new MenuFlyoutSubItem { Text = $"Play on: {player?.DisplayName ?? "No player selected"}", Icon = Glyph("") };
        foreach (var candidate in App.Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entry = new ToggleMenuFlyoutItem { Text = candidate.DisplayName, IsChecked = candidate.PlayerId == player?.PlayerId };
            // On the checked state rather than Click: screen readers toggle the item without raising Click
            entry.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) => { if (entry.IsChecked) App.SetActivePlayer(candidate.PlayerId); });
            playOn.Items.Add(entry);
        }
        menu.Items.Add(playOn);
        menu.Items.Add(new MenuFlyoutSeparator());

        // Playback: only for a playable, available track with a player to play it on
        if (player is not null && track.IsPlayable && track.IsAvailableNow)
        {
            var fromHere = parent is { MediaType: "album" or "playlist" or "podcast" } && parent.Uri != track.Uri;
            if (fromHere)
            {
                var label = parent!.MediaType switch { "album" => "Play Album from here", "playlist" => "Play Playlist from here", _ => "Play from here to latest" };
                Add(menu, label, "", () => App.PlayAsync(parent, startItem: track.ItemId, loadingItem: track));
            }
            else
            {
                Add(menu, "Play Now", "", () => App.PlayAsync(track));
            }

            var enqueue = new MenuFlyoutSubItem { Text = "Enqueue options", Icon = Glyph("") };
            foreach (var (option, label, glyph) in EnqueueOptions) Add(enqueue, label, glyph, () => App.PlayAsync(track, option));
            menu.Items.Add(enqueue);
        }

        // Navigation: artist (only when there is exactly one, like the web app), album, endless mix
        if (track.IsAvailableNow && track.Artists is [var artist])
        {
            Add(menu, $"View artist {artist.Name}", "", () => App.OpenAsync(artist));
        }
        if (track.IsAvailableNow && track.Album is { } album)
        {
            Add(menu, $"View album {album.Name}", "", () => App.OpenAsync(album));
        }

        // Track-only entries: the browse and podcast lists use the same row for folders and episodes
        var isTrack = track.MediaType == "track";
        if (isTrack && track.IsAvailableNow)
        {
            var mix = Add(menu, "View track endless mix", "", () => App.OpenAsync(EndlessMix(track)));
            _ = CheckEndlessMixAsync(mix, track);
        }

        // Library membership: a library track knows it; a streaming track is looked up
        if (isTrack)
        {
            var library = Add(menu, track.Provider == "library" && track.IsInLibrary ? "Remove from library" : "Add to library", "", () => Task.CompletedTask);
            _ = WireLibraryAsync(library, track);
        }

        // Favorites (not for browse folders, which the server cannot favorite)
        if (track.MediaType != "folder") Add(menu, track.Favorite ? "Remove from favorites" : "Add to favorites", track.Favorite ? "" : "", () => App.ToggleFavoriteAsync(track));

        // Add to playlist
        if (isTrack) Add(menu, "Add to playlist...", "", () => ShowAddToPlaylistAsync(track, parent));
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

    /// <summary>A mix can only be generated when a provider of the track (or, for a library track, any provider) supplies similar tracks; otherwise the entry is disabled.</summary>
    private static async Task CheckEndlessMixAsync(MenuFlyoutItem entry, MediaItem track)
    {
        try
        {
            var providers = (await App.Client.GetProvidersCachedAsync()).ToDictionary(p => p.InstanceId);
            bool Similar(string instanceId) => providers.TryGetValue(instanceId, out var provider) && provider.Supports("similar_tracks");

            var supported = track.ProviderMappings is { Count: > 0 } mappings ? mappings.Any(m => Similar(m.ProviderInstance)) : Similar(track.Provider);
            if (!supported && track.Provider == "library") supported = providers.Values.Any(p => p.Supports("similar_tracks"));
            entry.IsEnabled = supported;
        }
        catch (Exception ex)
        {
            App.Debug("Track menu: provider lookup failed: " + ex.Message);   // leave the entry enabled; opening the mix reports its own error
        }
    }

    // =========================================================================
    // LIBRARY
    // =========================================================================

    /// <summary>
    /// Settle the library entry: resolve a streaming track to its library copy (the removal needs that id), set the
    /// label, and attach add or remove. Disabled until the answer is in.
    /// </summary>
    private static async Task WireLibraryAsync(MenuFlyoutItem entry, MediaItem track)
    {
        entry.IsEnabled = false;
        MediaItem? libraryItem;
        try
        {
            libraryItem = track.Provider == "library" ? track : await App.Client.GetLibraryItemAsync(track.MediaType, track.ItemId, track.Provider);
        }
        catch (Exception ex)
        {
            App.Debug("Track menu: library lookup failed: " + ex.Message);
            return;
        }

        var inLibrary = libraryItem?.IsInLibrary == true;
        entry.Text      = inLibrary ? "Remove from library" : "Add to library";
        entry.IsEnabled = true;
        entry.Click += async (_, _) =>
        {
            try
            {
                if (!inLibrary)
                {
                    await App.Client.AddToLibraryAsync(track.Uri);
                    App.Window.ShowMessage($"Added {track.Name} to the library", InfoBarSeverity.Success);
                }
                else if (await ConfirmRemoveAsync())
                {
                    await App.Client.RemoveFromLibraryAsync(libraryItem!.MediaType, libraryItem.ItemId);
                    track.Favorite = false;   // a favorite is always in the library, so leaving it clears that too
                    App.Window.ShowMessage($"Removed {track.Name} from the library", InfoBarSeverity.Success);
                }
            }
            catch (Exception ex)
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

    /// <summary>Pick one of the library playlists the track can be added to, then add it.</summary>
    private static async Task ShowAddToPlaylistAsync(MediaItem track, MediaItem? parent)
    {
        var busy   = new ProgressRing { IsActive = true, Margin = new Thickness(0, 24, 0, 24) };
        var empty  = new TextBlock { Text = "No playlists this track can be added to.", Visibility = Visibility.Collapsed, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var filter = new TextBox { PlaceholderText = "Search", Visibility = Visibility.Collapsed };
        var list   = new ListView
        {
            SelectionMode      = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            MaxHeight          = 420,
            ItemTemplate       = (DataTemplate)Application.Current.Resources["PlaylistPickRowTemplate"],
            ItemContainerStyle = (Style)Application.Current.Resources["TrackListItemStyle"],
        };
        var content = new StackPanel { Spacing = 12, Width = 440 };   // fixed: long names realized while scrolling would widen the dialog
        content.Children.Add(filter);
        content.Children.Add(busy);
        content.Children.Add(empty);
        content.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot        = App.Window.Content.XamlRoot,
            Title           = "Add to playlist...",
            Content         = content,
            CloseButtonText = "Cancel",
        };

        MediaItem? chosen = null;
        list.ItemClick += (_, e) => { chosen = e.ClickedItem as MediaItem; dialog.Hide(); };

        var shown = dialog.ShowAsync();
        List<MediaItem> targets = [];
        try
        {
            targets = await PlaylistTargetsAsync(track, parent);
        }
        catch (Exception ex)
        {
            empty.Text = ex.Message;
        }
        busy.IsActive   = false;
        busy.Visibility = Visibility.Collapsed;
        empty.Visibility  = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        filter.Visibility = targets.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
        list.ItemsSource  = targets;
        filter.TextChanged += (_, _) =>
        {
            var query = filter.Text.Trim();
            list.ItemsSource = query.Length == 0 ? targets : targets.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        await shown;
        if (chosen is null) return;

        try
        {
            await App.Client.AddPlaylistTracksAsync(chosen.ItemId, [track.Uri]);
            App.Window.ShowMessage($"Added {track.Name} to {chosen.Name}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            App.Window.ShowMessage(ex.Message);
        }
    }

    /// <summary>
    /// The web app's rules for which playlists are offered: available, editable by this user, not the playlist being
    /// viewed, holding the track's media type (albums go into track playlists, the server unwraps them), and on a
    /// provider that can take this track (the built-in provider, a streaming provider, or one the track itself is on).
    /// </summary>
    private static async Task<List<MediaItem>> PlaylistTargetsAsync(MediaItem track, MediaItem? parent)
    {
        var reference = track.ProviderMappings is not null ? track : await App.Client.GetItemByUriAsync(track.Uri);
        var providers = (await App.Client.GetProvidersCachedAsync()).ToDictionary(p => p.InstanceId);
        var playlists = await App.Client.GetAllLibraryPlaylistsAsync();

        var result = new List<MediaItem>();
        foreach (var playlist in playlists)
        {
            if (playlist.ProviderMappings is not { Count: > 0 } mappings || !mappings.Any(m => m.Available == true)) continue;
            if (!CanEditPlaylistItems(playlist)) continue;
            if (parent is { MediaType: "playlist" } && parent.ItemId == playlist.ItemId) continue;

            var types = playlist.SupportedMediatypes ?? ["track"];
            if (types.Contains("track")) types = [.. types, "album"];
            if (!types.Contains(reference.MediaType)) continue;

            if (!providers.TryGetValue(mappings[0].ProviderInstance, out var provider)) continue;
            var fits = provider.Domain == "builtin"
                || provider.IsStreamingProvider == true
                || reference.ProviderMappings?.Any(m => m.ProviderInstance == provider.InstanceId) == true;
            if (fits) result.Add(playlist);
        }
        return result;
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
        return access.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.String && owner.GetString() == user.UserId;
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
            try { await action(); }
            catch (Exception ex) { App.Window.ShowMessage(ex.Message); }
        };
        items.Add(item);
        return item;
    }

    private static MenuFlyoutItem Add(MenuFlyout menu, string text, string glyph, Func<Task> action) => Add(menu.Items, text, glyph, action);

    private static MenuFlyoutItem Add(MenuFlyoutSubItem menu, string text, string glyph, Func<Task> action) => Add(menu.Items, text, glyph, action);
}
