using System.Collections.Concurrent;
using System.Text.Json;

namespace MusicAssistant.Api;

/// <summary>
/// Client for the Music Assistant server API.
///
/// Mirrors the official web frontend: open a transport (local websocket or
/// remote WebRTC data channel), wait for the ServerInfo message, authenticate
/// with the "auth" command, then send JSON commands keyed by message_id and
/// receive results or events. Player and queue state is kept in memory and
/// updated from server events.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://github.com/music-assistant/server/tree/dev/music_assistant/controllers/webserver
/// </remarks>
public sealed class MassClient : IDisposable
{
    private const string DeviceName = "Windows";

    private IMassTransport? transport;
    private TaskCompletionSource<JsonElement>? firstMessage;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<string, List<JsonElement>>                 partials = new();

    /// <summary>Favorite states learned since connecting, by item URI.</summary>
    private readonly ConcurrentDictionary<string, bool> knownFavorites = new();

    /// <summary>URIs whose favorite state was already asked of the server this connection, so each track is asked once.</summary>
    private readonly ConcurrentDictionary<string, byte> favoriteLookups = new();

    /// <summary>Where the client reports non-fatal oddities (the app points this at its log file). The API layer has no UI dependency.</summary>
    public static Action<string>? Logger { get; set; }

    /// <summary>Server details from the first message of the current connection, or null when disconnected.</summary>
    public ServerInfo? ServerInfo { get; private set; }

    /// <summary>The user this connection authenticated as, or null before authentication.</summary>
    public User?       CurrentUser { get; private set; }

    /// <summary>HTTP base URL of the connected server, used to build image proxy URLs.</summary>
    public string      BaseUrl     { get; private set; } = "";

    /// <summary>True from the moment the server info arrives until the connection closes.</summary>
    public bool        IsConnected { get; private set; }

    /// <summary>True when the connection goes through the remote (WebRTC) transport instead of the local network.</summary>
    public bool        IsRemote    => transport?.IsRemote == true;

    /// <summary>The live transport, for features that ride the same connection (the speaker's remote channel).</summary>
    public IMassTransport? Transport => transport;

    /// <summary>Every known player keyed by player id, kept current from server events.</summary>
    public ConcurrentDictionary<string, Player>      Players { get; } = new();

    /// <summary>Every known player queue keyed by queue id, kept current from server events.</summary>
    public ConcurrentDictionary<string, PlayerQueue> Queues  { get; } = new();

    /// <summary>
    /// Player ids the server explicitly removed this connection (player_removed). A player merely missing from the
    /// list is not the same thing: after a server restart, players/all answers before the providers have rediscovered
    /// their devices, and the missing ones trickle in as player_added over the next minute.
    /// </summary>
    public ConcurrentDictionary<string, byte> RemovedPlayers { get; } = new();

    /// <summary>True once the full player and queue lists were fetched after authentication. Events arrive before that, applied to a partial list.</summary>
    public bool StateLoaded { get; private set; }

    /// <summary>Raised for every server event (event name, object id, raw data). Runs on a background thread.</summary>
    public event Action<string, string?, JsonElement>? EventReceived;

    /// <summary>Raised when the transport closes for any reason after a successful connect.</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>Raised on a background thread when a favorite state looked up from the server was applied to a loaded queue.</summary>
    public event Action? FavoritesChanged;

    // =========================================================================
    // CONNECTION
    // =========================================================================

    /// <summary>Connect over the local network. Accepts http(s):// or ws(s):// addresses with or without a trailing /ws.</summary>
    public Task<ServerInfo> ConnectAsync(string serverAddress, CancellationToken ct = default)
        => ConnectAsync(new WebSocketTransport(serverAddress), ct);

    /// <summary>
    /// Open the given transport and wait for the server's ServerInfo message.
    /// Throws ApiException(SetupRequired) when the server has no users yet.
    /// </summary>
    public async Task<ServerInfo> ConnectAsync(IMassTransport newTransport, CancellationToken ct = default)
    {
        await DisconnectAsync();

        transport    = newTransport;
        BaseUrl      = newTransport.HttpBaseUrl;
        firstMessage = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        newTransport.MessageReceived += OnTransportMessage;
        newTransport.Closed          += OnTransportClosed;

        try
        {
            await newTransport.ConnectAsync(ct);

            // Server info is the first message; a 503 "connection" error means onboarding is not done.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using (timeout.Token.Register(() => firstMessage.TrySetException(new ApiException(0, "Timeout waiting for server info"))))
            {
                JsonElement first = await firstMessage.Task;
                if (first.TryGetProperty("error_code", out JsonElement code))
                {
                    throw new ApiException(code.GetInt32(), first.TryGetProperty("details", out JsonElement d) ? d.GetString() ?? "" : "Server error");
                }
                ServerInfo = first.Deserialize<ServerInfo>(Json.Options) ?? throw new ApiException(0, "Invalid server info");
            }
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }

        IsConnected = true;
        return ServerInfo;
    }

    /// <summary>Close the transport, fail every pending command and clear all cached server state.</summary>
    /// <returns>A task that completes once the old transport is closed and the state is cleared.</returns>
    public async Task DisconnectAsync()
    {
        IsConnected = false;
        if (transport is { } old)
        {
            transport = null;
            old.MessageReceived -= OnTransportMessage;
            old.Closed          -= OnTransportClosed;
            try
            {
                await old.CloseAsync();
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                // Best-effort close; the transport is disposed next either way.
            }
            old.Dispose();
        }
        FailPending(new ApiException(0, "Disconnected"));
        StateLoaded = false;
        Players.Clear();
        Queues.Clear();
        knownFavorites.Clear();
        favoriteLookups.Clear();
        RemovedPlayers.Clear();
        // A different server (or remote session) may run different providers.
        providersCache = null;
        CurrentUser = null;
        ServerInfo  = null;
    }

    /// <summary>Start disconnecting without waiting for it to finish.</summary>
    public void Dispose() => _ = DisconnectAsync();

    private void OnTransportMessage(string text)
    {
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (firstMessage is { Task.IsCompleted: false } first)
        {
            first.TrySetResult(message);
            return;
        }
        HandleMessage(message);
    }

    private void OnTransportClosed(Exception? error)
    {
        bool wasConnected = IsConnected;
        IsConnected = false;
        FailPending(new ApiException(0, "Connection lost"));
        if (wasConnected) Disconnected?.Invoke(error);
    }

    // =========================================================================
    // AUTHENTICATION
    // =========================================================================

    /// <summary>List the sign-in providers the server offers (built-in username and password, Home Assistant, ...).</summary>
    /// <returns>The server's authentication providers.</returns>
    public Task<List<AuthProvider>> GetAuthProvidersAsync() => SendAsync<List<AuthProvider>>("auth/providers");

    /// <summary>Username/password login. Returns the access token to store for later sessions.</summary>
    public async Task<string> LoginAsync(string username, string password, string providerId = "builtin")
    {
        LoginResult result = await SendAsync<LoginResult>("auth/login", new
        {
            username,
            password,
            provider_id = providerId,
            device_name = $"{DeviceName} - {Environment.MachineName}",
        });

        if (!result.Success || string.IsNullOrEmpty(result.AccessToken))
        {
            throw new ApiException(ApiException.AuthenticationFailed, result.Error ?? "Invalid credentials");
        }

        await AuthenticateAsync(result.AccessToken);
        return result.AccessToken;
    }

    /// <summary>Authenticate this connection with an existing token, then load players and queues.</summary>
    public async Task<User> AuthenticateAsync(string token)
    {
        AuthResult result = await SendAsync<AuthResult>("auth", new
        {
            token,
            device_name = $"{DeviceName} - {Environment.MachineName}",
        });

        CurrentUser = result.User ?? throw new ApiException(ApiException.InvalidToken, "Invalid or expired token");
        await FetchStateAsync();
        return CurrentUser;
    }

    /// <summary>URL the user must open in a browser to sign in through an OAuth provider (Home Assistant).</summary>
    public async Task<string> GetAuthorizationUrlAsync(string providerId, string returnUrl)
    {
        JsonElement result = await SendAsync<JsonElement>("auth/authorization_url", new { provider_id = providerId, return_url = returnUrl });
        string?     url    = result.TryGetProperty("authorization_url", out JsonElement u) && (u.ValueKind == JsonValueKind.String) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
        {
            string? error = result.TryGetProperty("error", out JsonElement e) ? e.GetString() : null;
            throw new ApiException(ApiException.AuthenticationFailed, error ?? "Provider does not support browser sign-in");
        }
        return url;
    }

    /// <summary>Sign out the current session on the server.</summary>
    /// <returns>A task that completes when the server confirmed the logout.</returns>
    public Task LogoutAsync() => SendAsync<JsonElement>("auth/logout");

    // Remote access (admin only on the server).

    /// <summary>Read the server's remote access state.</summary>
    /// <returns>Whether remote access is on, connected, and the server's Remote ID.</returns>
    public Task<RemoteAccessInfo> GetRemoteAccessInfoAsync() => SendAsync<RemoteAccessInfo>("remote_access/info");

    /// <summary>Turn the server's remote access on or off.</summary>
    /// <param name="enabled">True to enable remote access, false to disable it.</param>
    /// <returns>The remote access state after the change.</returns>
    public Task<RemoteAccessInfo> ConfigureRemoteAccessAsync(bool enabled)
        => SendAsync<RemoteAccessInfo>("remote_access/configure", new { enabled });

    /// <summary>Normalize a Remote ID typed or pasted by a user: strip separators, upper-case. Null when it is not 26 base32 characters.</summary>
    public static string? NormalizeRemoteId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string id = new string([.. text.Where(char.IsLetterOrDigit)]).ToUpperInvariant();
        // Base32 alphabet A-Z 2-7; the server prints 2 as 9. 8, 0 and 1 never occur, so a typo with them is caught here.
        return (id.Length == 26) && id.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7' or '9') ? id : null;
    }

    private async Task FetchStateAsync()
    {
        foreach (Player player in await SendAsync<List<Player>>("players/all"))       Players[player.PlayerId] = player;
        foreach (PlayerQueue queue  in await SendAsync<List<PlayerQueue>>("player_queues/all"))
        {
            ApplyKnownFavorites(queue.CurrentItem?.MediaItem);
            ApplyKnownFavorites(queue.NextItem?.MediaItem);
            Queues[queue.QueueId] = Stamped(queue);
        }
        StateLoaded = true;
    }

    // =========================================================================
    // MUSIC LIBRARY
    // =========================================================================

    /// <summary>List library items. mediaType is the plural key: artists, albums, tracks, playlists, radios.</summary>
    public Task<List<MediaItem>> GetLibraryItemsAsync(string mediaType, string? search, bool favoriteOnly, int limit, int offset, string orderBy = "sort_name")
        => SendAsync<List<MediaItem>>($"music/{mediaType}/library_items", new
        {
            favorite   = favoriteOnly ? true : (bool?)null,
            search     = string.IsNullOrWhiteSpace(search) ? null : search,
            limit,
            offset,
            order_by   = orderBy,
            // Genres without any library item are noise (the web app hides them too).
            hide_empty = mediaType == "genres" ? true : (bool?)null,
        });

    /// <summary>Genre page rows: one folder per media type (Artists, Albums, Tracks, ...) with its items, like the web app's genre view.</summary>
    public Task<List<MediaItem>> GetGenreOverviewAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/genres/overview", new { item_id = itemId, provider_instance_id_or_domain = provider });

    /// <summary>Look up a media item by its Music Assistant URI.</summary>
    /// <param name="uri">The item URI, for example library://track/12 or spotify://album/abc.</param>
    /// <returns>The full media item.</returns>
    public Task<MediaItem> GetItemByUriAsync(string uri)
        => SendAsync<MediaItem>("music/item_by_uri", new { uri });

    /// <summary>Fetch one media item by id from a provider.</summary>
    /// <param name="mediaType">Singular media type: artist, album, track, playlist, radio, ...</param>
    /// <param name="itemId">The item's id on that provider.</param>
    /// <param name="provider">Provider instance id or domain that holds the item (library for library items).</param>
    /// <returns>The full media item.</returns>
    public Task<MediaItem> GetItemAsync(string mediaType, string itemId, string provider)
        => SendAsync<MediaItem>($"music/{Plural(mediaType)}/get_{mediaType}", new { item_id = itemId, provider_instance_id_or_domain = provider });

    /// <summary>
    /// Tracks of an album. Also remembers a cover for the album when the server has none: a library album can lack an
    /// image while one of its tracks carries the album cover (metadata never merged server-side). Only a track's own
    /// image counts, never an artist photo it inherits. Stored under the album's URI and each track's album link.
    /// </summary>
    /// <param name="itemId">The album's id on the provider.</param>
    /// <param name="provider">Provider instance id or domain that holds the album.</param>
    /// <param name="albumUri">URI of the album being listed, so its cards resolve the borrowed cover too.</param>
    /// <returns>The album's tracks in disc and track order.</returns>
    public async Task<List<MediaItem>> GetAlbumTracksAsync(string itemId, string provider, string? albumUri = null)
    {
        var             args   = new { item_id = itemId, provider_instance_id_or_domain = provider };
        List<MediaItem> tracks = await SendAsync<List<MediaItem>>("music/albums/album_tracks", args);

        // A library album whose tracks came from a streaming provider (YouTube Music) can have track number 0 stored for
        // every track. The server sorts by disc and track number, so the list comes back in its alphabetical database
        // order, and it copies the provider's real numbers into the database during that same request. Asking once more
        // returns the album in its real order.
        // ponytail: an album whose provider has no track numbers at all costs one extra request every time it opens.
        if ((tracks.Count > 1) && tracks.Any(t => t.TrackNumber is null or 0))
        {
            tracks = await SendAsync<List<MediaItem>>("music/albums/album_tracks", args);
        }

        if (tracks.Select(t => t.OwnImage()).FirstOrDefault(i => i is not null) is { } cover)
        {
            foreach (string? uri in tracks.Select(t => t.Album?.Uri).Append(albumUri))
            {
                if (!string.IsNullOrEmpty(uri)) Images.BorrowedCovers.TryAdd(uri, cover);
            }
        }
        return tracks;
    }

    /// <summary>Tracks of a playlist, in playlist order.</summary>
    /// <param name="itemId">The playlist's id on the provider.</param>
    /// <param name="provider">Provider instance id or domain that holds the playlist.</param>
    /// <returns>The playlist's tracks.</returns>
    public Task<List<MediaItem>> GetPlaylistTracksAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/playlists/playlist_tracks", new { item_id = itemId, provider_instance_id_or_domain = provider });

    /// <summary>An artist's most popular tracks.</summary>
    /// <param name="itemId">The artist's id on the provider.</param>
    /// <param name="provider">Provider instance id or domain that holds the artist.</param>
    /// <returns>The artist's top tracks.</returns>
    public Task<List<MediaItem>> GetArtistTopTracksAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/artists/top_tracks", new { item_id = itemId, provider_instance_id_or_domain = provider });

    /// <summary>Albums by an artist.</summary>
    /// <param name="itemId">The artist's id on the provider.</param>
    /// <param name="provider">Provider instance id or domain that holds the artist.</param>
    /// <returns>The artist's albums.</returns>
    public Task<List<MediaItem>> GetArtistAlbumsAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/artists/artist_albums", new { item_id = itemId, provider_instance_id_or_domain = provider });

    /// <summary>Number of library items of a type (plural key: audiobooks, podcasts, ...).</summary>
    public Task<int> GetLibraryCountAsync(string mediaType)
        => SendAsync<int>($"music/{mediaType}/count", new { favorite_only = false });

    /// <summary>Episodes of a podcast.</summary>
    /// <param name="itemId">The podcast's id on the provider.</param>
    /// <param name="provider">Provider instance id or domain that holds the podcast.</param>
    /// <returns>The podcast's episodes.</returns>
    public Task<List<MediaItem>> GetPodcastEpisodesAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/podcasts/podcast_episodes", new { item_id = itemId, provider_instance_id_or_domain = provider });

    /// <summary>Library tracks tagged with a genre.</summary>
    /// <param name="itemId">The genre's library item id.</param>
    /// <param name="limit">Maximum number of tracks to return.</param>
    /// <returns>Tracks in the genre.</returns>
    public Task<List<MediaItem>> GetGenreTracksAsync(string itemId, int limit = 100)
        => SendAsync<List<MediaItem>>("music/genres/tracks", new { item_id = itemId, limit });

    /// <summary>List the folders and items at a browse path.</summary>
    /// <param name="path">Browse path to open, or null for the root list of providers.</param>
    /// <returns>The folders and media items at that path.</returns>
    public Task<List<MediaItem>> BrowseAsync(string? path)
        => SendAsync<List<MediaItem>>("music/browse", new { path });

    /// <summary>Search the library and every music provider.</summary>
    /// <param name="query">The text to search for.</param>
    /// <param name="limit">Maximum number of results per media type.</param>
    /// <returns>Matching artists, albums, tracks, playlists and radio stations.</returns>
    public Task<SearchResults> SearchAsync(string query, int limit = 25)
        => SendAsync<SearchResults>("music/search", new { search_query = query, limit });

    /// <summary>Items played most recently on this server.</summary>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>The recently played items.</returns>
    public Task<List<MediaItem>> GetRecentlyPlayedAsync(int limit = 20)
        => SendAsync<List<MediaItem>>("music/recently_played_items", new { limit });

    /// <summary>Recommendation folders the home page shows as rows.</summary>
    /// <returns>Recommendation folders, each with its items when the server includes them.</returns>
    public Task<List<MediaItem>> GetRecommendationsAsync()
        => SendAsync<List<MediaItem>>("music/recommendations");

    /// <summary>Items of one recommendation folder.</summary>
    /// <param name="provider">Provider that supplies the recommendation folder.</param>
    /// <param name="itemId">The recommendation folder's item id.</param>
    /// <returns>The folder's items.</returns>
    public Task<List<MediaItem>> GetRecommendationItemsAsync(string provider, string itemId)
        => SendAsync<List<MediaItem>>("music/recommendations/items", new { provider, item_id = itemId });

    /// <summary>Mark an item as a favorite.</summary>
    /// <param name="item">The item to favorite, sent by URI.</param>
    /// <returns>A task that completes when the server accepted the change.</returns>
    public Task AddFavoriteAsync(MediaItem item)
        => SendAsync<JsonElement>("music/favorites/add_item", new { item = item.Uri });

    /// <summary>Remove an item from the favorites.</summary>
    /// <param name="item">The library item to unfavorite, sent by media type and library item id.</param>
    /// <returns>A task that completes when the server accepted the change.</returns>
    public Task RemoveFavoriteAsync(MediaItem item)
        => SendAsync<JsonElement>("music/favorites/remove_item", new { media_type = item.MediaType, library_item_id = item.ItemId });

    /// <summary>Every provider instance configured on the server, with its capabilities.</summary>
    /// <returns>The provider instances.</returns>
    public Task<List<ProviderInstance>> GetProvidersAsync() => SendAsync<List<ProviderInstance>>("providers");

    private Task<List<ProviderInstance>>? providersCache;

    /// <summary>Provider instances with their capabilities, fetched once per connection (the track menu asks on every open).</summary>
    public Task<List<ProviderInstance>> GetProvidersCachedAsync()
    {
        Task<List<ProviderInstance>>? cached = providersCache;
        if (cached is { IsFaulted: false, IsCanceled: false }) return cached;
        return providersCache = GetProvidersAsync();
    }

    /// <summary>The library copy of an item held by a provider, or null when it is not in the library.</summary>
    public Task<MediaItem?> GetLibraryItemAsync(string mediaType, string itemId, string provider)
        => SendAsync<MediaItem?>("music/get_library_item", new { media_type = mediaType, item_id = itemId, provider_instance_id_or_domain = provider });

    /// <summary>Add an item to the library.</summary>
    /// <param name="uri">URI of the provider item to add.</param>
    /// <returns>A task that completes when the server added the item.</returns>
    public Task AddToLibraryAsync(string uri)
        => SendAsync<JsonElement>("music/library/add_item", new { item = uri, overwrite_existing = false });

    /// <summary>Remove a library item. Items depending on it are removed too (the server recurses).</summary>
    public Task RemoveFromLibraryAsync(string mediaType, string libraryItemId)
        => SendAsync<JsonElement>("music/library/remove_item", new { media_type = mediaType, library_item_id = libraryItemId });

    /// <summary>Every playlist in the library, fetched a page at a time.</summary>
    public async Task<List<MediaItem>> GetAllLibraryPlaylistsAsync()
    {
        const int pageSize = 500;
        var all = new List<MediaItem>();
        for (int offset = 0; ; offset += pageSize)
        {
            List<MediaItem> page = await SendAsync<List<MediaItem>>("music/playlists/library_items", new { limit = pageSize, offset, order_by = "sort_name" });
            all.AddRange(page);
            if (page.Count < pageSize) return all;
        }
    }

    /// <summary>
    /// Add items (by URI) to a library playlist. Albums are expanded to their tracks by the server.
    ///
    /// The server answers at once with a background task and does the work afterward, so a provider refusing the edit
    /// only shows up in that task. This waits for it and throws its error, so callers never report a failed add as done.
    /// </summary>
    /// <exception cref="ApiException">The server refused the command, or its background task failed or never finished.</exception>
    public async Task AddPlaylistTracksAsync(string dbPlaylistId, IEnumerable<string> uris)
    {
        JsonElement task = await SendAsync<JsonElement>("music/playlists/add_playlist_tracks", new { db_playlist_id = dbPlaylistId, uris = uris.ToArray() });
        // Older servers finish before answering.
        if ((task.ValueKind != JsonValueKind.Object) || !task.TryGetProperty("id", out JsonElement id)) return;
        await WaitForTaskAsync(id.GetString()!, TimeSpan.FromMinutes(2));
    }

    /// <summary>Poll a background task until it finishes; throws its error when it failed.</summary>
    private async Task WaitForTaskAsync(string taskId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            // ponytail: whole list per poll, a tasks/get command would be lighter if the server adds one.
            JsonElement tasks = await SendAsync<JsonElement>("tasks/list");
            JsonElement match = tasks.EnumerateArray().FirstOrDefault(t => t.TryGetProperty("id", out JsonElement tid) && (tid.GetString() == taskId));
            // Cleared from the list: finished.
            if (match.ValueKind != JsonValueKind.Object) return;

            string? status = match.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
            if (status == "success") return;
            if (status is "failed" or "cancelled")
            {
                string? error = match.TryGetProperty("last_error", out JsonElement e) && (e.ValueKind == JsonValueKind.String) ? e.GetString() : null;
                throw new ApiException(0, error ?? $"The server task {status}.");
            }
        }
        throw new ApiException(0, "The server is still working on it. Check the playlist in a moment.");
    }

    /// <summary>Create a playlist on a provider (the built-in one keeps it on the server) and return its library item.</summary>
    public Task<MediaItem> CreatePlaylistAsync(string name, string providerInstance, IEnumerable<string> mediaTypes)
        => SendAsync<MediaItem>("music/playlists/create_playlist", new { name, provider_instance_or_domain = providerInstance, media_types = mediaTypes.ToArray() });

    private static string Plural(string mediaType) => mediaType == "radio" ? "radios" : $"{mediaType}s";

    // =========================================================================
    // PLAYERS / QUEUES
    // =========================================================================

    /// <summary>Transport verbs whose every send is logged, so a spurious pause/resume can be traced to the app (and which path) versus the server.</summary>
    private static readonly HashSet<string> LoggedCommands = ["play", "pause", "play_pause", "stop", "next", "previous", "play_index", "seek"];

    /// <summary>Send a players/cmd command (play, pause, volume_set, ...) to a player.</summary>
    /// <param name="playerId">The player to control.</param>
    /// <param name="command">The command name after players/cmd/.</param>
    /// <param name="args">Extra command arguments as an anonymous object, merged with the player id.</param>
    /// <returns>A task that completes when the server accepted the command.</returns>
    public Task PlayerCommandAsync(string playerId, string command, object? args = null)
    {
        if (LoggedCommands.Contains(command)) Logger?.Invoke($"player cmd '{command}' -> {playerId}");
        return SendAsync<JsonElement>($"players/cmd/{command}", Merge(new { player_id = playerId }, args));
    }

    /// <summary>Send a player_queues command (next, shuffle, repeat, ...) to a queue.</summary>
    /// <param name="queueId">The queue to control.</param>
    /// <param name="command">The command name after player_queues/.</param>
    /// <param name="args">Extra command arguments as an anonymous object, merged with the queue id.</param>
    /// <returns>A task that completes when the server accepted the command.</returns>
    public Task QueueCommandAsync(string queueId, string command, object? args = null)
    {
        if (LoggedCommands.Contains(command)) Logger?.Invoke($"queue cmd '{command}' -> {queueId}");
        return SendAsync<JsonElement>($"player_queues/{command}", Merge(new { queue_id = queueId }, args));
    }

    /// <summary>Move a queue (its items and position) to another player's queue, which carries on playing if the source was.</summary>
    /// <param name="sourceQueueId">The queue to move.</param>
    /// <param name="targetQueueId">The queue that takes it over.</param>
    /// <returns>A task that completes when the server accepted the transfer.</returns>
    public Task TransferQueueAsync(string sourceQueueId, string targetQueueId)
        => SendAsync<JsonElement>("player_queues/transfer", new { source_queue_id = sourceQueueId, target_queue_id = targetQueueId });

    /// <summary>A page of the items in a queue.</summary>
    /// <param name="queueId">The queue to read.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <param name="offset">Index of the first item to return.</param>
    /// <returns>The queue items in play order.</returns>
    public Task<List<QueueItem>> GetQueueItemsAsync(string queueId, int limit = 500, int offset = 0)
        => SendAsync<List<QueueItem>>("player_queues/items", new { queue_id = queueId, limit, offset });

    /// <summary>
    /// Play media on a queue. option: play, replace, next, replace_next, add; null lets the server apply
    /// its configured default for the media type. startItem is the item id of an item inside media
    /// (a track within its album or playlist), which is what the web app sends.
    /// </summary>
    public Task PlayMediaAsync(string queueId, string mediaUri, string? option = null, string? startItem = null, bool? shuffle = null)
        => SendAsync<JsonElement>("player_queues/play_media", new
        {
            queue_id   = queueId,
            media      = mediaUri,
            option,
            start_item = startItem,
            shuffle,
        });

    /// <summary>The queue a player currently plays from, falling back to the player's own queue id.</summary>
    public string QueueIdFor(Player player)
        => player.ActiveSource is { } source && Queues.ContainsKey(source) ? source : player.PlayerId;

    // =========================================================================
    // TRANSPORT
    // =========================================================================

    /// <summary>Send a command and deserialize its result. Partial results are concatenated.</summary>
    public async Task<T> SendAsync<T>(string command, object? args = null)
    {
        if (transport is null) throw new ApiException(0, "Not connected");

        string messageId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[messageId] = tcs;

        // A failed send would otherwise orphan the pending entry until the next disconnect.
        try
        {
            await transport.SendAsync(JsonSerializer.Serialize(new { message_id = messageId, command, args }, Json.Options));
        }
        catch
        {
            pending.TryRemove(messageId, out _);
            throw;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // On timeout, drop the pending and any accumulated partial chunks too, or a reply that never comes leaks both.
        using (timeout.Token.Register(() =>
        {
            if (pending.TryRemove(messageId, out TaskCompletionSource<JsonElement>? timedOut)) timedOut.TrySetException(new ApiException(0, $"Timeout waiting for {command}"));
            partials.TryRemove(messageId, out _);
        }))
        {
            JsonElement element = await tcs.Task;
            return (element.ValueKind == JsonValueKind.Undefined) || (element.ValueKind == JsonValueKind.Null)
                ? default!
                : element.Deserialize<T>(Json.Options)!;
        }
    }

    private void HandleMessage(JsonElement message)
    {
        // Event.
        if (message.TryGetProperty("event", out JsonElement evt))
        {
            string      name     = evt.GetString() ?? "";
            string?     objectId = message.TryGetProperty("object_id", out JsonElement oid) && (oid.ValueKind == JsonValueKind.String) ? oid.GetString() : null;
            JsonElement data     = message.TryGetProperty("data", out JsonElement d) ? d : default;
            try
            {
                ApplyEvent(name, objectId, data);
                EventReceived?.Invoke(name, objectId, data);
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                // One event a newer server shapes differently must not take the connection down with it.
                Logger?.Invoke($"Ignoring '{name}' event: {ex.Message}");
            }
            return;
        }

        // Command result.
        if (!message.TryGetProperty("message_id", out JsonElement idElement)) return;
        string id = idElement.GetString() ?? "";

        if (message.TryGetProperty("error_code", out JsonElement code))
        {
            partials.TryRemove(id, out _);
            if (pending.TryRemove(id, out TaskCompletionSource<JsonElement>? failed))
            {
                string? details = message.TryGetProperty("details", out JsonElement det) && (det.ValueKind == JsonValueKind.String) ? det.GetString() : null;
                failed.TrySetException(new ApiException(code.GetInt32(), details ?? $"Command failed ({code})"));
            }
            return;
        }

        JsonElement resultElement = message.TryGetProperty("result", out JsonElement r) ? r : default;
        bool        isPartial     = message.TryGetProperty("partial", out JsonElement p) && (p.ValueKind == JsonValueKind.True);

        if (isPartial)
        {
            List<JsonElement> chunks = partials.GetOrAdd(id, _ => []);
            if (resultElement.ValueKind == JsonValueKind.Array) chunks.AddRange(resultElement.EnumerateArray());
            return;
        }

        if (partials.TryRemove(id, out List<JsonElement>? earlier))
        {
            if (resultElement.ValueKind == JsonValueKind.Array) earlier.AddRange(resultElement.EnumerateArray());
            resultElement = JsonSerializer.SerializeToElement(earlier, Json.Options);
        }

        if (pending.TryRemove(id, out TaskCompletionSource<JsonElement>? tcs)) tcs.TrySetResult(resultElement);
    }

    private void ApplyEvent(string name, string? objectId, JsonElement data)
    {
        switch (name)
        {
            case "player_added":
            case "player_updated":
                if (data.Deserialize<Player>(Json.Options) is { } player)
                {
                    // Log real playback_state transitions so a pause that flips back to playing with no matching app
                    // command in the log points at the server (buffering resume, group sync), not the client.
                    if (Players.TryGetValue(player.PlayerId, out Player? old) && (old.PlaybackState != player.PlaybackState))
                        Logger?.Invoke($"player '{player.Name}' {old.PlaybackState} -> {player.PlaybackState}");
                    Players[player.PlayerId] = player;
                    RemovedPlayers.TryRemove(player.PlayerId, out _);
                }
                break;
            case "player_removed":
                // A queue's lifecycle is tied to its player (no queue_removed event, and the fallback queue id is the
                // player id), so drop the matching queue too or it lingers as a ghost with no backing player.
                if (objectId is not null)
                {
                    Players.TryRemove(objectId, out _);
                    Queues.TryRemove(objectId, out _);
                    RemovedPlayers[objectId] = 0;
                }
                break;
            case "queue_added":
            case "queue_updated":
                if (data.Deserialize<PlayerQueue>(Json.Options) is { } queue)
                {
                    ApplyKnownFavorites(queue.CurrentItem?.MediaItem);
                    ApplyKnownFavorites(queue.NextItem?.MediaItem);
                    Queues[queue.QueueId] = Stamped(queue);
                }
                break;
            case "media_item_updated":
                // A favorite added or removed anywhere (this app, the web app, the Android app) arrives here.
                if (data.Deserialize<MediaItem>(Json.Options) is { } updated) RecordFavorite(updated, updated.Favorite);
                break;
            case "queue_time_updated":
                if (objectId is not null && Queues.TryGetValue(objectId, out PlayerQueue? q) && (data.ValueKind == JsonValueKind.Number))
                {
                    q.ElapsedTime            = data.GetDouble();
                    q.ElapsedTimeLastUpdated = NowSeconds();
                }
                break;
        }
    }

    /// <summary>
    /// Remembers an item's favorite state under its own URI and every provider URI it's known by, and applies it to the
    /// loaded queues' current and next items right away.
    /// </summary>
    /// <remarks>
    /// Queue items carry a snapshot of their track, and a queue item for a streaming track is addressed by the provider
    /// URI while the favorite belongs to the library copy. Without this the player bar's heart kept the old state.
    /// </remarks>
    /// <param name="item">The item whose favorite state changed, ideally with its provider mappings.</param>
    /// <param name="favorite">The new favorite state.</param>
    public void RecordFavorite(MediaItem item, bool favorite)
    {
        foreach (string uri in UrisOf(item))
        {
            knownFavorites[uri] = favorite;
        }

        foreach (PlayerQueue queue in Queues.Values)
        {
            ApplyKnownFavorites(queue.CurrentItem?.MediaItem);
            ApplyKnownFavorites(queue.NextItem?.MediaItem);
        }
    }

    /// <summary>
    /// Sets an item's favorite flag from the known states, when one of its URIs has one; otherwise asks the server once,
    /// because a queue item's copy of its track keeps the favorite flag it had when it was queued.
    /// </summary>
    /// <param name="item">A queue item's track, or null.</param>
    private void ApplyKnownFavorites(MediaItem? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (string uri in UrisOf(item))
        {
            if (knownFavorites.TryGetValue(uri, out bool favorite))
            {
                item.Favorite = favorite;
                return;
            }
        }

        if ((item.Uri.Length > 0) && favoriteLookups.TryAdd(item.Uri, 0))
        {
            _ = LookUpFavoriteAsync(item.Uri);
        }
    }

    /// <summary>Asks the server for a track's current favorite state and applies it to the loaded queues.</summary>
    /// <param name="uri">The queue item track's URI.</param>
    /// <returns>A task that completes once the state was applied or the lookup failed.</returns>
    private async Task LookUpFavoriteAsync(string uri)
    {
        try
        {
            MediaItem current = await GetItemByUriAsync(uri);

            // A like or unlike that arrived while this was in flight is newer than this answer.
            if (UrisOf(current).Append(uri).Any(knownFavorites.ContainsKey))
            {
                return;
            }

            knownFavorites[uri] = current.Favorite;
            RecordFavorite(current, current.Favorite);
            FavoritesChanged?.Invoke();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            Logger?.Invoke($"Couldn't look up whether {uri} is a favorite: {ex.Message}");
        }
    }

    /// <summary>The item's URI and the provider URI of each of its provider mappings.</summary>
    /// <param name="item">The media item.</param>
    /// <returns>Every URI the item can be addressed by.</returns>
    private static IEnumerable<string> UrisOf(MediaItem item)
    {
        if (item.Uri.Length > 0)
        {
            yield return item.Uri;
        }

        foreach (ProviderMapping mapping in item.ProviderMappings ?? [])
        {
            yield return $"{mapping.ProviderInstance}://{item.MediaType}/{mapping.ItemId}";
        }
    }

    /// <summary>
    /// Replace the server's elapsed-time timestamp with this PC's clock at arrival. The server stamps with its own clock
    /// and time reports are stamped here, so mixing the two made the position drift, freeze or jump to the end.
    /// </summary>
    private static PlayerQueue Stamped(PlayerQueue queue)
    {
        queue.ElapsedTimeLastUpdated = NowSeconds();
        return queue;
    }

    private static double NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private void FailPending(Exception error)
    {
        foreach (string? key in pending.Keys.ToArray())
        {
            if (pending.TryRemove(key, out TaskCompletionSource<JsonElement>? tcs)) tcs.TrySetException(error);
        }
        partials.Clear();
    }

    /// <summary>Merge two anonymous objects into one dictionary of command args, skipping nulls.</summary>
    private static Dictionary<string, object?> Merge(object first, object? second)
    {
        var merged = new Dictionary<string, object?>();
        foreach (object? source in new[] { first, second })
        {
            if (source is null) continue;
            JsonElement element = JsonSerializer.SerializeToElement(source, Json.Options);
            foreach (JsonProperty prop in element.EnumerateObject()) merged[prop.Name] = prop.Value;
        }
        return merged;
    }

    // =========================================================================
    // IMAGES
    // =========================================================================

    /// <summary>
    /// Build a display URL for a server image at one of the allowed proxy sizes
    /// (80, 160, 256, 512, 1024). Uses the opaque proxy id when the server
    /// provides one, otherwise the legacy path/provider query form.
    /// </summary>
    public string? ImageUrl(MediaImage? image, int size = 256)
    {
        if (image is null || string.IsNullOrEmpty(image.Path)) return null;
        if (image.Path.StartsWith("data:image", StringComparison.Ordinal)) return image.Path;

        if (!string.IsNullOrEmpty(image.ProxyId))
        {
            return $"{BaseUrl}/imageproxy/{image.ProxyId}?size={size}";
        }

        if (image.RemotelyAccessible && image.Path.StartsWith("https://", StringComparison.Ordinal))
        {
            return image.Path;
        }

        string encoded = Uri.EscapeDataString(Uri.EscapeDataString(image.Path));
        return $"{BaseUrl}/imageproxy?path={encoded}&provider={Uri.EscapeDataString(image.Provider)}&size={size}";
    }
}
