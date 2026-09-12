using Windows.Media;
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
    private static string displaySignature = "";

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

        // The media overlay resolves name and icon through a Start Menu shortcut tagged with the same id
        StartMenuShortcut.Ensure(App.AppUserModelId, "Music Assistant", Environment.ProcessPath ?? "", iconPath);
    }

    // Windows → Music Assistant

    private static void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (App.ActivePlayer is not { } player) return;

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

        _ = App.Client.PlayerCommandAsync(player.PlayerId, command).ContinueWith(
            t => App.Dispatcher.TryEnqueue(() => App.Window.ShowMessage(t.Exception!.InnerException?.Message ?? "Command failed")),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // Music Assistant → Windows

    private static void Update()
    {
        if (controls is null) return;

        var player = App.ActivePlayer;
        var queue  = player is null ? null : App.Client.Queues.GetValueOrDefault(App.Client.QueueIdFor(player));
        var item   = queue?.CurrentItem;
        var media  = player?.CurrentMedia;

        controls.PlaybackStatus = player?.PlaybackState switch
        {
            "playing" => MediaPlaybackStatus.Playing,
            "paused"  => MediaPlaybackStatus.Paused,
            _         => MediaPlaybackStatus.Stopped,
        };

        var title    = item?.Name ?? media?.Title ?? "";
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
            updater.Thumbnail = Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? RandomAccessStreamReference.CreateFromUri(uri)
                : null;
            updater.Update();
        }

        // Timeline for the overlay's progress bar
        var duration = item?.Duration ?? media?.Duration ?? 0;
        if (duration > 0 && queue is not null)
        {
            var now     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var elapsed = queue.ElapsedTime + (queue.State == "playing" ? Math.Max(0, now - queue.ElapsedTimeLastUpdated) : 0);
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
