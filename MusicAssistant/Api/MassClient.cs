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

    /// <summary>Where the client reports non-fatal oddities (the app points this at its log file). The API layer has no UI dependency.</summary>
    public static Action<string>? Logger { get; set; }

    public ServerInfo? ServerInfo { get; private set; }
    public User?       CurrentUser { get; private set; }
    public string      BaseUrl     { get; private set; } = "";
    public bool        IsConnected { get; private set; }
    public bool        IsRemote    => transport?.IsRemote == true;
    /// <summary>The live transport, for features that ride the same connection (the speaker's remote channel).</summary>
    public IMassTransport? Transport => transport;

    public ConcurrentDictionary<string, Player>      Players { get; } = new();
    public ConcurrentDictionary<string, PlayerQueue> Queues  { get; } = new();

    /// <summary>True once the full player and queue lists were fetched after authentication. Events arrive before that, applied to a partial list.</summary>
    public bool StateLoaded { get; private set; }

    /// <summary>Raised for every server event (event name, object id, raw data). Runs on a background thread.</summary>
    public event Action<string, string?, JsonElement>? EventReceived;

    /// <summary>Raised when the transport closes for any reason after a successful connect.</summary>
    public event Action<Exception?>? Disconnected;

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

            // Server info is the first message; a 503 "connection" error means onboarding is not done
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using (timeout.Token.Register(() => firstMessage.TrySetException(new ApiException(0, "Timeout waiting for server info"))))
            {
                var first = await firstMessage.Task;
                if (first.TryGetProperty("error_code", out var code))
                {
                    throw new ApiException(code.GetInt32(), first.TryGetProperty("details", out var d) ? d.GetString() ?? "" : "Server error");
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

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        if (transport is { } old)
        {
            transport = null;
            old.MessageReceived -= OnTransportMessage;
            old.Closed          -= OnTransportClosed;
            try { await old.CloseAsync(); } catch { }
            old.Dispose();
        }
        FailPending(new ApiException(0, "Disconnected"));
        StateLoaded = false;
        Players.Clear();
        Queues.Clear();
        CurrentUser = null;
        ServerInfo  = null;
    }

    public void Dispose() => _ = DisconnectAsync();

    private void OnTransportMessage(string text)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return; }

        if (firstMessage is { Task.IsCompleted: false } first)
        {
            first.TrySetResult(message);
            return;
        }
        HandleMessage(message);
    }

    private void OnTransportClosed(Exception? error)
    {
        var wasConnected = IsConnected;
        IsConnected = false;
        FailPending(new ApiException(0, "Connection lost"));
        if (wasConnected) Disconnected?.Invoke(error);
    }

    // =========================================================================
    // AUTHENTICATION
    // =========================================================================

    public Task<List<AuthProvider>> GetAuthProvidersAsync() => SendAsync<List<AuthProvider>>("auth/providers");

    /// <summary>Username/password login. Returns the access token to store for later sessions.</summary>
    public async Task<string> LoginAsync(string username, string password, string providerId = "builtin")
    {
        var result = await SendAsync<LoginResult>("auth/login", new
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
        var result = await SendAsync<AuthResult>("auth", new
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
        var result = await SendAsync<JsonElement>("auth/authorization_url", new { provider_id = providerId, return_url = returnUrl });
        var url = result.TryGetProperty("authorization_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
        {
            var error = result.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new ApiException(ApiException.AuthenticationFailed, error ?? "Provider does not support browser sign-in");
        }
        return url;
    }

    public Task LogoutAsync() => SendAsync<JsonElement>("auth/logout");

    // Remote access (admin only on the server)

    public Task<RemoteAccessInfo> GetRemoteAccessInfoAsync() => SendAsync<RemoteAccessInfo>("remote_access/info");

    public Task<RemoteAccessInfo> ConfigureRemoteAccessAsync(bool enabled)
        => SendAsync<RemoteAccessInfo>("remote_access/configure", new { enabled });

    /// <summary>Normalize a Remote ID typed or pasted by a user: strip separators, upper-case. Null when it is not 26 base32 characters.</summary>
    public static string? NormalizeRemoteId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var id = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        // Base32 alphabet A-Z 2-7; the server prints 2 as 9. 8, 0 and 1 never occur, so a typo with them is caught here
        return id.Length == 26 && id.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7' or '9') ? id : null;
    }

    private async Task FetchStateAsync()
    {
        foreach (var player in await SendAsync<List<Player>>("players/all"))       Players[player.PlayerId] = player;
        foreach (var queue  in await SendAsync<List<PlayerQueue>>("player_queues/all")) Queues[queue.QueueId] = queue;
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
            hide_empty = mediaType == "genres" ? true : (bool?)null,   // genres without any library item are noise (the web app hides them too)
        });

    /// <summary>Genre page rows: one folder per media type (Artists, Albums, Tracks, ...) with its items, like the web app's genre view.</summary>
    public Task<List<MediaItem>> GetGenreOverviewAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/genres/overview", new { item_id = itemId, provider_instance_id_or_domain = provider });

    public Task<MediaItem> GetItemByUriAsync(string uri)
        => SendAsync<MediaItem>("music/item_by_uri", new { uri });

    public Task<MediaItem> GetItemAsync(string mediaType, string itemId, string provider)
        => SendAsync<MediaItem>($"music/{Plural(mediaType)}/get_{mediaType}", new { item_id = itemId, provider_instance_id_or_domain = provider });

    public Task<List<MediaItem>> GetAlbumTracksAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/albums/album_tracks", new { item_id = itemId, provider_instance_id_or_domain = provider });

    public Task<List<MediaItem>> GetPlaylistTracksAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/playlists/playlist_tracks", new { item_id = itemId, provider_instance_id_or_domain = provider });

    public Task<List<MediaItem>> GetArtistTopTracksAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/artists/top_tracks", new { item_id = itemId, provider_instance_id_or_domain = provider });

    public Task<List<MediaItem>> GetArtistAlbumsAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/artists/artist_albums", new { item_id = itemId, provider_instance_id_or_domain = provider });

    /// <summary>Number of library items of a type (plural key: audiobooks, podcasts, ...).</summary>
    public Task<int> GetLibraryCountAsync(string mediaType)
        => SendAsync<int>($"music/{mediaType}/count", new { favorite_only = false });

    public Task<List<MediaItem>> GetPodcastEpisodesAsync(string itemId, string provider)
        => SendAsync<List<MediaItem>>("music/podcasts/podcast_episodes", new { item_id = itemId, provider_instance_id_or_domain = provider });

    public Task<List<MediaItem>> GetGenreTracksAsync(string itemId, int limit = 100)
        => SendAsync<List<MediaItem>>("music/genres/tracks", new { item_id = itemId, limit });

    public Task<List<MediaItem>> BrowseAsync(string? path)
        => SendAsync<List<MediaItem>>("music/browse", new { path });

    public Task<SearchResults> SearchAsync(string query, int limit = 25)
        => SendAsync<SearchResults>("music/search", new { search_query = query, limit });

    public Task<List<MediaItem>> GetRecentlyPlayedAsync(int limit = 20)
        => SendAsync<List<MediaItem>>("music/recently_played_items", new { limit });

    public Task<List<MediaItem>> GetRecommendationsAsync()
        => SendAsync<List<MediaItem>>("music/recommendations");

    public Task<List<MediaItem>> GetRecommendationItemsAsync(string provider, string itemId)
        => SendAsync<List<MediaItem>>("music/recommendations/items", new { provider, item_id = itemId });

    public Task AddFavoriteAsync(MediaItem item)
        => SendAsync<JsonElement>("music/favorites/add_item", new { item = item.Uri });

    public Task RemoveFavoriteAsync(MediaItem item)
        => SendAsync<JsonElement>("music/favorites/remove_item", new { media_type = item.MediaType, library_item_id = item.ItemId });

    public Task<List<ProviderInstance>> GetProvidersAsync() => SendAsync<List<ProviderInstance>>("providers");

    private static string Plural(string mediaType) => mediaType == "radio" ? "radios" : mediaType + "s";

    // =========================================================================
    // PLAYERS / QUEUES
    // =========================================================================

    /// <summary>Transport verbs whose every send is logged, so a spurious pause/resume can be traced to the app (and which path) versus the server.</summary>
    private static readonly HashSet<string> LoggedCommands = ["play", "pause", "play_pause", "stop", "next", "previous", "play_index", "seek"];

    public Task PlayerCommandAsync(string playerId, string command, object? args = null)
    {
        if (LoggedCommands.Contains(command)) Logger?.Invoke($"player cmd '{command}' -> {playerId}");
        return SendAsync<JsonElement>($"players/cmd/{command}", Merge(new { player_id = playerId }, args));
    }

    public Task QueueCommandAsync(string queueId, string command, object? args = null)
    {
        if (LoggedCommands.Contains(command)) Logger?.Invoke($"queue cmd '{command}' -> {queueId}");
        return SendAsync<JsonElement>($"player_queues/{command}", Merge(new { queue_id = queueId }, args));
    }

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

        var messageId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[messageId] = tcs;

        // A failed send would otherwise orphan the pending entry until the next disconnect
        try { await transport.SendAsync(JsonSerializer.Serialize(new { message_id = messageId, command, args }, Json.Options)); }
        catch { pending.TryRemove(messageId, out _); throw; }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // On timeout, drop the pending and any accumulated partial chunks too, or a reply that never comes leaks both
        using (timeout.Token.Register(() =>
        {
            if (pending.TryRemove(messageId, out var timedOut)) timedOut.TrySetException(new ApiException(0, $"Timeout waiting for {command}"));
            partials.TryRemove(messageId, out _);
        }))
        {
            var element = await tcs.Task;
            return element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null
                ? default!
                : element.Deserialize<T>(Json.Options)!;
        }
    }

    private void HandleMessage(JsonElement message)
    {
        // Event
        if (message.TryGetProperty("event", out var evt))
        {
            var name     = evt.GetString() ?? "";
            var objectId = message.TryGetProperty("object_id", out var oid) && oid.ValueKind == JsonValueKind.String ? oid.GetString() : null;
            var data     = message.TryGetProperty("data", out var d) ? d : default;
            try
            {
                ApplyEvent(name, objectId, data);
                EventReceived?.Invoke(name, objectId, data);
            }
            catch (Exception ex)
            {
                // One event a newer server shapes differently must not take the connection down with it
                Logger?.Invoke($"Ignoring '{name}' event: {ex.Message}");
            }
            return;
        }

        // Command result
        if (!message.TryGetProperty("message_id", out var idElement)) return;
        var id = idElement.GetString() ?? "";

        if (message.TryGetProperty("error_code", out var code))
        {
            partials.TryRemove(id, out _);
            if (pending.TryRemove(id, out var failed))
            {
                var details = message.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.String ? det.GetString() : null;
                failed.TrySetException(new ApiException(code.GetInt32(), details ?? $"Command failed ({code})"));
            }
            return;
        }

        var resultElement = message.TryGetProperty("result", out var r) ? r : default;
        var isPartial     = message.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;

        if (isPartial)
        {
            var chunks = partials.GetOrAdd(id, _ => []);
            if (resultElement.ValueKind == JsonValueKind.Array) chunks.AddRange(resultElement.EnumerateArray());
            return;
        }

        if (partials.TryRemove(id, out var earlier))
        {
            if (resultElement.ValueKind == JsonValueKind.Array) earlier.AddRange(resultElement.EnumerateArray());
            resultElement = JsonSerializer.SerializeToElement(earlier, Json.Options);
        }

        if (pending.TryRemove(id, out var tcs)) tcs.TrySetResult(resultElement);
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
                    if (Players.TryGetValue(player.PlayerId, out var old) && old.PlaybackState != player.PlaybackState)
                        Logger?.Invoke($"player '{player.Name}' {old.PlaybackState} -> {player.PlaybackState}");
                    Players[player.PlayerId] = player;
                }
                break;
            case "player_removed":
                // A queue's lifecycle is tied to its player (no queue_removed event, and the fallback queue id is the
                // player id), so drop the matching queue too or it lingers as a ghost with no backing player
                if (objectId is not null) { Players.TryRemove(objectId, out _); Queues.TryRemove(objectId, out _); }
                break;
            case "queue_added":
            case "queue_updated":
                if (data.Deserialize<PlayerQueue>(Json.Options) is { } queue) Queues[queue.QueueId] = queue;
                break;
            case "queue_time_updated":
                if (objectId is not null && Queues.TryGetValue(objectId, out var q) && data.ValueKind == JsonValueKind.Number)
                {
                    q.ElapsedTime            = data.GetDouble();
                    q.ElapsedTimeLastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                }
                break;
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var key in pending.Keys.ToArray())
        {
            if (pending.TryRemove(key, out var tcs)) tcs.TrySetException(error);
        }
        partials.Clear();
    }

    /// <summary>Merge two anonymous objects into one dictionary of command args, skipping nulls.</summary>
    private static Dictionary<string, object?> Merge(object first, object? second)
    {
        var merged = new Dictionary<string, object?>();
        foreach (var source in new[] { first, second })
        {
            if (source is null) continue;
            var element = JsonSerializer.SerializeToElement(source, Json.Options);
            foreach (var prop in element.EnumerateObject()) merged[prop.Name] = prop.Value;
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

        var encoded = Uri.EscapeDataString(Uri.EscapeDataString(image.Path));
        return $"{BaseUrl}/imageproxy?path={encoded}&provider={Uri.EscapeDataString(image.Provider)}&size={size}";
    }
}
