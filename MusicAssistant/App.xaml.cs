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
        => Settings.ActivePlayerId is { } id && Client.Players.TryGetValue(id, out Player? player) ? player : null;

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
        Images.Resolver   = Client.ImageUrl;
        MassClient.Logger = Log;
        Client.EventReceived += (_, _, _) => Dispatcher.TryEnqueue(() =>
        {
            // Recover the selection if the active player was removed mid-session (deleted server-side, or the speaker
            // feature turned off while the PC was active). No-op while the current selection is still valid, and it
            // keeps a player that is merely powered off, since that one stays in the list.
            EnsureActivePlayer();
            StateChanged?.Invoke();
        });
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Write the failure to %LOCALAPPDATA%\MusicAssistant\crash.log so a user can report it. No secrets are logged.</summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"{e.Message}{Environment.NewLine}{e.Exception}");
        e.Handled = true;   // a failed UI action must not take the whole app down
        if (Window is null) return;   // failed while the main window was still being built: nothing to show it in
        Dispatcher.TryEnqueue(() => Window.ShowMessage("Something went wrong. Details were written to crash.log."));
    }

    /// <summary>Append a line to the app log (same file as crashes). Never include tokens or passwords. Never throws.</summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // The app runs for days and logs every player state change, so cap the file: past 1 MB the current log
            // becomes crash.log.1 (replacing the previous one) and a fresh file starts
            if (new FileInfo(LogPath) is { Exists: true, Length: > 1_000_000 }) File.Move(LogPath, LogPath + ".1", overwrite: true);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { }   // unwritable profile folder: logging must not become the crash
    }

    /// <summary>Verbose diagnostics (speaker sync, remote lifecycle); written only when MA_RTC_LOG is set, so the log stays for crashes.</summary>
    public static readonly bool Verbose = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MA_RTC_LOG"));
    public static void Debug(string message) { if (Verbose) Log(message); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!SingleInstance.Claim())
        {
            Exit();   // the running copy was told to show itself
            return;
        }

        Player.OwnPlayerId = Settings.SpeakerClientId;   // this PC's speaker stays listed even though the server hides web players
        Remote.DiagnosticLog.EnableIfRequested();
        Templates.TrimArtCache();
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

        // The server streams events as soon as the connection is authenticated, before the full player list has been
        // fetched. Judging the selection against that partial list would replace it with whichever player happened
        // to send the first event (or with nothing), so wait for the complete state.
        if (!Client.StateLoaded) return;

        // Keep a remembered selection that simply has not appeared yet, rather than clobbering it with a fallback:
        // this PC's own speaker is added to the list only once it connects (a few seconds after launch), and after a
        // server restart the other speakers are missing from players/all until their providers rediscover them.
        // Only give it up when the server said player_removed, or when the own speaker was switched off here.
        // Overwriting on mere absence is what silently moved playback from the remembered speaker to this PC.
        string? remembered = Settings.ActivePlayerId;
        if (!string.IsNullOrEmpty(remembered))
        {
            if (Client.Players.ContainsKey(remembered)) return;
            bool own     = remembered == Player.OwnPlayerId;
            bool ownOff  = own && !Settings.SpeakerEnabled;
            bool removed = !own && Client.RemovedPlayers.ContainsKey(remembered);   // own speaker comes and goes with its connection
            if (!ownOff && !removed) return;
        }

        var visible = Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name).ToList();

        // No usable remembered selection: pick the first playing player, else the first visible one. The own web
        // player counts as playing only when the local speaker is really streaming, so a stale server flag after a
        // crash does not make the app open selecting itself.
        bool ownStreaming = Window.SpeakerPlaying;
        string? fallback     = (visible.FirstOrDefault(p => p.IsPlaying && (!p.IsThisDevice || ownStreaming)) ?? visible.FirstOrDefault())?.PlayerId;
        if (fallback is null) return;   // nothing to select yet; re-evaluated on the next player event
        Log($"Active player fallback: '{remembered}' is gone, selecting '{fallback}'");
        SetActivePlayer(fallback);
    }

    public static void NotifyStateChanged() => StateChanged?.Invoke();

    // =========================================================================
    // PLAYBACK HELPERS
    // =========================================================================

    /// <summary>The item currently being started, or null. Drives the loading overlay on the player bar.</summary>
    public static MediaItem? PendingItem { get; private set; }

    // Starting playback that takes a while: a player coming out of idle (the WiiM has to open a new stream) or a jump to
    // another queue item. The bar shows a spinner in the play button and locks the seek bar until the player is playing
    // (the target item, for a jump), the command fails, or the wait runs out. Play from paused is instant and skips it.
    private static string?  startingPlayerId;
    private static string?  startingItemId;   // the queue item the player has to be on, for a jump within the queue
    private static DateTime startingUntil;
    private static readonly TimeSpan StartingWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Send a transport command to a player from any control: the player bar, the Space key, the tray menu or the
    /// media keys. A play or play_pause to a player that is neither playing nor paused shows the loading state; a
    /// failed command ends it and shows the error. UI thread.
    /// </summary>
    public static void SendPlayerCommand(Player player, string command)
    {
        bool starts = command is "play" or "play_pause" && player.PlaybackState is not ("playing" or "paused");
        if (starts) MarkStarting(player, null);

        _ = Client.PlayerCommandAsync(player.PlayerId, command).ContinueWith(t => Dispatcher.TryEnqueue(() =>
        {
            if (starts) ClearStarting(player.PlayerId);
            Window.ShowMessage(t.Exception!.InnerException?.Message ?? "Command failed");
        }), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Jump to another item in a queue (a click in the queue, or Play here). Shows the loading state until the player plays that item. UI thread.</summary>
    public static async Task PlayQueueItemAsync(QueueItem item)
    {
        // Every member of a synced group maps to the leader's queue; mark the player the bar shows, so its spinner and seek lock apply
        Player? player = ActivePlayer is { } active && Client.QueueIdFor(active) == item.QueueId
            ? active
            : Client.Players.Values.FirstOrDefault(p => Client.QueueIdFor(p) == item.QueueId);
        if (player is not null) MarkStarting(player, item.QueueItemId);

        try
        {
            await Client.QueueCommandAsync(item.QueueId, "play_index", new { index = item.QueueItemId });
        }
        catch (Exception ex)
        {
            if (player is not null) ClearStarting(player.PlayerId);
            Window.ShowMessage(ex.Message);
        }
    }

    private static void MarkStarting(Player player, string? queueItemId)
    {
        startingPlayerId = player.PlayerId;
        startingItemId   = queueItemId;
        startingUntil    = DateTime.UtcNow + StartingWait;
        StateChanged?.Invoke();
    }

    private static void ClearStarting(string playerId)
    {
        if (startingPlayerId != playerId) return;
        startingPlayerId = null;
        StateChanged?.Invoke();
    }

    /// <summary>True while playback started on this player is still loading. Clears itself once it plays or the wait runs out. UI thread.</summary>
    public static bool IsStarting(Player player)
    {
        if (startingPlayerId != player.PlayerId) return false;

        string? current = Client.Queues.GetValueOrDefault(Client.QueueIdFor(player))?.CurrentItem?.QueueItemId;
        bool started = player.IsPlaying && (startingItemId is null || startingItemId == current);
        if (!started && DateTime.UtcNow < startingUntil) return true;

        startingPlayerId = null;   // playing now, or gave up: a later pause must not bring the spinner back
        return false;
    }

    /// <summary>
    /// Play a media item on the active player. option: play, replace, next, replace_next, add, or null
    /// to let the server apply its configured default for the media type (what the web app's play button
    /// does: replace the queue for albums and playlists, insert and play for single tracks).
    /// </summary>
    /// <param name="loadingItem">The item whose loading spinner to show while playback starts; defaults to <paramref name="item"/>. For a track clicked inside an album or playlist, pass the clicked track so its row shows the spinner, not the container.</param>
    public static async Task PlayAsync(MediaItem item, string? option = null, string? startItem = null, MediaItem? loadingItem = null)
    {
        if (ActivePlayer is not { } player)
        {
            Window.ShowMessage("Select a player first.");
            return;
        }

        // Only immediate playback shows a loading state; queueing for later is instant
        bool startsNow = option is null or "play" or "replace";
        MediaItem loading   = loadingItem ?? item;
        if (startsNow) BeginLoading(loading);

        try
        {
            // Snapshot before sending: the server pushes the queue update before it answers the command
            string queueId = Client.QueueIdFor(player);
            string? before  = Client.Queues.GetValueOrDefault(queueId)?.CurrentItem?.QueueItemId;

            // The server builds an album's queue from the same track list the album page shows, so an album whose stored
            // track numbers are still 0 would be queued in alphabetical order. Loading its tracks first has the server
            // repair the numbers (see GetAlbumTracksAsync). A failure here must not block playback.
            if (item.MediaType == "album")
            {
                try { await Client.GetAlbumTracksAsync(item.ItemId, item.Provider, item.Uri); }
                catch (ApiException ex) { Log("Album track order check: " + ex.Message); }
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Client.PlayMediaAsync(queueId, item.Uri, option, startItem);
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) Log($"play_media on {player.Name} took {watch.Elapsed.TotalSeconds:0}s to be accepted");
            if (startsNow && !await WaitForPlaybackAsync(player.PlayerId, queueId, before, TimeSpan.FromSeconds(15)))
            {
                Player? current = Client.Players.GetValueOrDefault(player.PlayerId);
                PlayerQueue? queue   = Client.Queues.GetValueOrDefault(queueId);
                Log($"{player.Name} did not report playback within 15s: player {current?.PlaybackState}, queue {queue?.State} at {queue?.ElapsedTime:0}s of '{queue?.CurrentItem?.Name}'");
            }
        }
        catch (Exception ex)
        {
            Window.ShowMessage(ex.Message);
        }
        finally
        {
            if (startsNow) EndLoading(loading);
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

    /// <summary>Resolve once the player is playing a different item than before (or the same one from the start), or after the timeout. False on timeout.</summary>
    private static async Task<bool> WaitForPlaybackAsync(string playerId, string queueId, string? before, TimeSpan timeout)
    {
        var started = new TaskCompletionSource();

        void Check()
        {
            PlayerQueue? queue   = Client.Queues.GetValueOrDefault(queueId);
            bool playing = queue?.State == "playing" || Client.Players.GetValueOrDefault(playerId)?.IsPlaying == true;
            bool changed = queue?.CurrentItem is { } current && (current.QueueItemId != before || queue.ElapsedTime < 3);
            if (playing && changed) started.TrySetResult();
        }

        StateChanged += Check;
        try
        {
            Check();
            return await Task.WhenAny(started.Task, Task.Delay(timeout)) == started.Task;
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
                MediaItem library = item.Provider == "library" ? item : await Client.GetItemByUriAsync(item.Uri);
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
