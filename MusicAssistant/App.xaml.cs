using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicAssistant.Api;

namespace MusicAssistant;

/// <summary>
/// Application entry point and shared app state.
///
/// Holds the single API client, persisted session, the active player and a
/// UI-thread StateChanged event that every page listens to for live updates.
/// </summary>
public partial class App : Application
{
    public static MassClient  Client   { get; } = new();
    public static Session     Settings { get; } = Session.Load();
    public static MainWindow  Window   { get; private set; } = null!;
    public static DispatcherQueue Dispatcher => Window.DispatcherQueue;

    /// <summary>Raised on the UI thread whenever a player or queue changed, or the active player switched.</summary>
    public static event Action? StateChanged;

    public static Player? ActivePlayer
        => Settings.ActivePlayerId is { } id && Client.Players.TryGetValue(id, out var player) ? player : null;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicAssistant", "crash.log");

    /// <summary>Stable identity for the taskbar, notifications and the media overlay ("Unknown app" otherwise).</summary>
    public const string AppUserModelId = "DevinGreen.MusicAssistant";

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public App()
    {
        if (!Packaging.IsPackaged) SetCurrentProcessExplicitAppUserModelID(AppUserModelId);   // MSIX builds carry identity already
        InitializeComponent();
        Images.Resolver = Client.ImageUrl;
        Client.EventReceived += (_, _, _) => Dispatcher.TryEnqueue(() => StateChanged?.Invoke());
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Write the failure to %LOCALAPPDATA%\MusicAssistant\crash.log so a user can report it. No secrets are logged.</summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"{e.Message}{Environment.NewLine}{e.Exception}");
        e.Handled = true;   // a failed UI action must not take the whole app down
        Dispatcher.TryEnqueue(() => Window.ShowMessage("Something went wrong. Details were written to crash.log."));
    }

    /// <summary>Append a line to the app log (same file as crashes). Never include tokens or passwords.</summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Player.OwnPlayerId = Settings.SpeakerClientId;   // this PC's speaker stays listed even though the server hides web players
        Window = new MainWindow();
        Window.Activate();
        MediaControls.Attach(Window);
    }

    // =========================================================================
    // PLAYER SELECTION
    // =========================================================================

    public static void SetActivePlayer(string? playerId)
    {
        Settings.ActivePlayerId = playerId;
        Settings.Save();
        StateChanged?.Invoke();
    }

    /// <summary>Pick a sensible default player once state is loaded: remembered one, else first playing, else first visible.</summary>
    public static void EnsureActivePlayer()
    {
        if (ActivePlayer is { IsVisible: true }) return;
        var visible = Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name).ToList();
        SetActivePlayer((visible.FirstOrDefault(p => p.IsPlaying) ?? visible.FirstOrDefault())?.PlayerId);
    }

    public static void NotifyStateChanged() => StateChanged?.Invoke();

    // =========================================================================
    // PLAYBACK HELPERS
    // =========================================================================

    /// <summary>The item currently being started, or null. Drives the loading overlay on the player bar.</summary>
    public static MediaItem? PendingItem { get; private set; }

    /// <summary>Play a media item on the active player. option: play, replace, next, replace_next, add.</summary>
    public static async Task PlayAsync(MediaItem item, string option = "play", string? startItem = null)
    {
        if (ActivePlayer is not { } player)
        {
            Window.ShowMessage("Select a player first.");
            return;
        }

        // Only immediate playback shows a loading state; queueing for later is instant
        var startsNow = option is "play" or "replace";
        if (startsNow) BeginLoading(item);

        try
        {
            // Snapshot before sending: the server pushes the queue update before it answers the command
            var queueId = Client.QueueIdFor(player);
            var before  = Client.Queues.GetValueOrDefault(queueId)?.CurrentItem?.QueueItemId;

            await Client.PlayMediaAsync(queueId, item.Uri, option, startItem);
            if (startsNow) await WaitForPlaybackAsync(player.PlayerId, queueId, before, TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Window.ShowMessage(ex.Message);
        }
        finally
        {
            if (startsNow) EndLoading(item);
        }
    }

    private static void BeginLoading(MediaItem item)
    {
        if (PendingItem is { } previous) previous.IsLoading = false;
        PendingItem    = item;
        item.IsLoading = true;
        StateChanged?.Invoke();
    }

    private static void EndLoading(MediaItem item)
    {
        item.IsLoading = false;
        if (PendingItem == item) PendingItem = null;
        StateChanged?.Invoke();
    }

    /// <summary>Resolve once the player is playing a different item than before (or the same one from the start), or after the timeout.</summary>
    private static async Task WaitForPlaybackAsync(string playerId, string queueId, string? before, TimeSpan timeout)
    {
        var started = new TaskCompletionSource();

        void Check()
        {
            var queue   = Client.Queues.GetValueOrDefault(queueId);
            var playing = queue?.State == "playing" || Client.Players.GetValueOrDefault(playerId)?.IsPlaying == true;
            var changed = queue?.CurrentItem is { } current && (current.QueueItemId != before || queue.ElapsedTime < 3);
            if (playing && changed) started.TrySetResult();
        }

        StateChanged += Check;
        try
        {
            Check();
            await Task.WhenAny(started.Task, Task.Delay(timeout));
        }
        finally
        {
            StateChanged -= Check;
        }
    }

    /// <summary>Open an item: detail page for albums/artists/playlists, browse for folders, otherwise play it.</summary>
    public static Task OpenAsync(MediaItem item)
    {
        if (item.HasDetailPage)
        {
            Window.Navigate(typeof(Pages.ItemPage), item);
            return Task.CompletedTask;
        }

        if (item.MediaType == "folder")
        {
            Window.Navigate(typeof(Pages.BrowsePage), item.Path ?? item.Uri);
            return Task.CompletedTask;
        }

        return PlayAsync(item);
    }

    public static async Task ToggleFavoriteAsync(MediaItem item)
    {
        try
        {
            if (item.Favorite)
            {
                // Removal needs the library id; queue and provider items carry the provider's id instead
                var library = item.Provider == "library" ? item : await Client.GetItemByUriAsync(item.Uri);
                await Client.RemoveFavoriteAsync(library);
            }
            else
            {
                await Client.AddFavoriteAsync(item);
            }
            item.Favorite = !item.Favorite;
        }
        catch (Exception ex)
        {
            Window.ShowMessage(ex.Message);
        }
    }
}
