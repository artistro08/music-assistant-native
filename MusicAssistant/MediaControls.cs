using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using MusicAssistant.Api;

namespace MusicAssistant;

/// <summary>
/// System Media Transport Controls integration.
///
/// Registers the app with Windows' media session so the media overlay, the
/// volume flyout, keyboard media keys and Bluetooth headset buttons show and
/// control what the active Music Assistant player is doing.
/// </summary>
/// <remarks>
/// @link https://learn.microsoft.com/windows/uwp/audio-video-camera/system-media-transport-controls
/// </remarks>
public static class MediaControls
{
    private static SystemMediaTransportControls? controls;
    private static string  displaySignature = "";
    private static string? lastTargetId;   // the player the keys last controlled or last saw playing

    public static void Attach(Microsoft.UI.Xaml.Window window)
    {
        RegisterAppIdentity();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);

        controls.IsEnabled         = true;
        controls.IsPlayEnabled     = true;
        controls.IsPauseEnabled    = true;
        controls.IsStopEnabled     = true;
        controls.IsNextEnabled     = true;
        controls.IsPreviousEnabled = true;
        controls.ButtonPressed    += OnButtonPressed;

        App.StateChanged += Update;
        Update();
    }

    /// <summary>
    /// Unpackaged apps have no manifest, so Windows looks the AppUserModelId up in
    /// HKCU\Software\Classes\AppUserModelId for a display name and icon. Without
    /// this the media overlay and notifications say "Unknown app".
    /// </summary>
    private static void RegisterAppIdentity()
    {
        if (Packaging.IsPackaged) return;   // package identity covers name, icon and AppUserModelID
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{App.AppUserModelId}");
            key?.SetValue("DisplayName", "Music Assistant");
            key?.SetValue("IconUri", iconPath);
            key?.SetValue("IconBackgroundColor", "FF0E8FE0");
        }
        catch (Exception)
        {
            // Registry not writable: the overlay just shows a generic name
        }
    }

    /// <summary>
    /// The player the media keys and overlay stand for. Windows' controls are about the sound this PC
    /// makes, so while this PC's own speaker is playing they follow it even if another player is selected
    /// in the app; otherwise they follow the selected player. When both are idle, the one the keys last
    /// drove wins, so a web player the server stopped (it cannot pause) still gets the next Play.
    /// </summary>
    private static Player? Target()
    {
        var selected = App.ActivePlayer;
        var local    = Player.OwnPlayerId is { } id && App.Client.Players.GetValueOrDefault(id) is { IsVisible: true } p ? p : null;

        Player? target;
        if (local is null || local.PlayerId == selected?.PlayerId || selected?.PlaybackState is "playing" or "paused") target = selected;
        else if (local.IsPlaying) target = local;
        // Both idle: the selected player, unless the keys last drove this PC's speaker. A paused WiiM goes idle after
        // 30 s, and an old queue left on this PC's speaker must not steal the next Play from it.
        else target = lastTargetId == local.PlayerId ? local : selected;

        if (target?.PlaybackState is "playing" or "paused") lastTargetId = target.PlayerId;
        return target;
    }

    // Windows → Music Assistant

    private static void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (Target() is not { } player) return;

        var command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play     => "play",
            SystemMediaTransportControlsButton.Pause    => "pause",
            SystemMediaTransportControlsButton.Stop     => "stop",
            SystemMediaTransportControlsButton.Next     => "next",
            SystemMediaTransportControlsButton.Previous => "previous",
            _ => null,
        };
        if (command is null) return;

        lastTargetId = player.PlayerId;
        App.Log($"SMTC button {args.Button} -> {player.Name}");   // an external media key, headset or app press shows up here as the source
        App.Dispatcher.TryEnqueue(() => App.SendPlayerCommand(player, command));   // this event arrives off the UI thread
    }

    // Music Assistant → Windows

    /// <summary>
    /// Overlay art from the app's own art cache, so it gets the same retries and reuses the file the player bar
    /// already downloaded, instead of Windows fetching the server URL on its own. Skipped if the track changed meanwhile.
    /// </summary>
    private static async Task ShowThumbnailAsync(string signature, string? imageUrl)
    {
        try
        {
            if (await Templates.ArtFileAsync(imageUrl) is not { } path) return;
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (controls is null || signature != displaySignature) return;
            controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            controls.DisplayUpdater.Update();
        }
        catch (Exception ex)
        {
            App.Debug("SMTC thumbnail: " + ex.Message);
        }
    }

    private static void Update()
    {
        if (controls is null) return;

        var player = Target();
        var queue  = player is null ? null : App.Client.Queues.GetValueOrDefault(App.Client.QueueIdFor(player));
        var item   = queue?.CurrentItem;
        var media  = player?.CurrentMedia;

        // Windows routes media keys to the most recent app whose session is playing or paused. A player that stopped
        // with a queue still on it (a paused WiiM turns idle after 30 s, a stopped web player) is resumable, so report
        // it as paused; reporting Stopped hands the keys to the next app and Play then goes nowhere.
        var resumable = player?.PlaybackState is "paused" || item is not null;
        controls.PlaybackStatus = player?.IsPlaying == true ? MediaPlaybackStatus.Playing
                                : resumable                 ? MediaPlaybackStatus.Paused
                                :                             MediaPlaybackStatus.Stopped;

        var title    = item?.Title ?? media?.Title ?? "";
        var artist   = item?.MediaItem?.ArtistsText ?? media?.Artist ?? "";
        var album    = item?.MediaItem?.Album?.Name ?? media?.Album ?? "";
        var imageUrl = item is not null ? App.Client.ImageUrl(item.FindImage(), 512) : media?.ImageUrl;

        // Only push display metadata when it changed, to avoid re-fetching artwork every second
        var signature = $"{title}|{artist}|{album}|{imageUrl}";
        if (signature != displaySignature)
        {
            displaySignature = signature;
            var updater = controls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title      = title;
            updater.MusicProperties.Artist     = artist;
            updater.MusicProperties.AlbumTitle = album;
            updater.Thumbnail = null;
            updater.Update();
            _ = ShowThumbnailAsync(signature, imageUrl);
        }

        // Timeline for the overlay's progress bar
        var duration = item?.Duration ?? media?.Duration ?? 0;
        if (duration > 0 && queue is not null)
        {
            var elapsed = queue.ElapsedNow;
            controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime   = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position    = TimeSpan.FromSeconds(Math.Min(elapsed, duration)),
                MaxSeekTime = TimeSpan.FromSeconds(duration),
                EndTime     = TimeSpan.FromSeconds(duration),
            });
        }
    }
}
