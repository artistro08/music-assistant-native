using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicAssistant.Api;

/// <summary>
/// Shared JSON options for the Music Assistant API.
///
/// The server speaks snake_case JSON. Every model in this file is a plain
/// data class deserialized with these options, so property names below map
/// 1:1 onto the server's field names.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling              = JsonNumberHandling.AllowReadingFromString,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

// =========================================================================
// SERVER / AUTH
// =========================================================================

/// <summary>First message the server sends on a new websocket connection.</summary>
public sealed class ServerInfo
{
    public string  ServerId      { get; set; } = "";
    public string  ServerVersion { get; set; } = "";
    public int     SchemaVersion { get; set; }
    public string  BaseUrl       { get; set; } = "";
    public bool    OnboardDone   { get; set; } = true;
    public string? Name          { get; set; }
}

public sealed class User
{
    public string  UserId      { get; set; } = "";
    public string  Username    { get; set; } = "";
    public string  Role        { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? AvatarUrl   { get; set; }
}

public sealed class AuthProvider
{
    public string ProviderId       { get; set; } = "";
    public string ProviderType     { get; set; } = "";
    public bool   RequiresRedirect { get; set; }
}

public sealed class LoginResult
{
    public bool    Success     { get; set; }
    public string? AccessToken { get; set; }
    public string? Error       { get; set; }
    public User?   User        { get; set; }
}

/// <summary>Server-side remote access state (admin only).</summary>
public sealed class RemoteAccessInfo
{
    public bool   Enabled      { get; set; }
    public bool   Running      { get; set; }
    public bool   Connected    { get; set; }
    public string RemoteId     { get; set; } = "";
    public bool   UsingHaCloud { get; set; }

    /// <summary>Remote ID grouped like the web UI shows it: 8-5-5-8.</summary>
    [JsonIgnore]
    public string RemoteIdDisplay => RemoteId.Length == 26 ? $"{RemoteId[..8]}-{RemoteId[8..13]}-{RemoteId[13..18]}-{RemoteId[18..]}" : RemoteId;
}

public sealed class AuthResult
{
    public bool  Authenticated { get; set; }
    public User? User          { get; set; }
}

// =========================================================================
// MEDIA ITEMS
// =========================================================================

public sealed class MediaImage
{
    public string  Type               { get; set; } = "thumb";
    public string  Path               { get; set; } = "";
    public string  Provider           { get; set; } = "";
    public bool    RemotelyAccessible { get; set; }
    public string? ProxyId            { get; set; }
}

public sealed class MediaMetadata
{
    public List<MediaImage>? Images      { get; set; }
    public string?           Description { get; set; }
    public List<string>?     Genres      { get; set; }
}

/// <summary>
/// One class for every media item shape the server returns.
///
/// Artists, albums, tracks, playlists, radios, browse folders, recommendation
/// folders and item mappings all share the same base fields, so a single
/// class with optional extras keeps the client small. Fields that do not
/// apply to a given media type stay null.
/// </summary>
public class MediaItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private bool isLoading;
    private bool isNowPlaying;

    /// <summary>True while this item was asked to play and the player has not started it yet (drives the card overlay).</summary>
    [JsonIgnore]
    public bool IsLoading
    {
        get => isLoading;
        set
        {
            if (isLoading == value) return;
            isLoading = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsLoading)));
        }
    }

    /// <summary>True when this track is the one currently playing (drives the live level bars on its row).</summary>
    [JsonIgnore]
    public bool IsNowPlaying
    {
        get => isNowPlaying;
        set
        {
            if (isNowPlaying == value) return;
            isNowPlaying = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsNowPlaying)));
        }
    }

    public string  ItemId     { get; set; } = "";
    public string  Provider   { get; set; } = "";
    public string  Name       { get; set; } = "";
    public string? Version    { get; set; }
    public string  Uri        { get; set; } = "";
    public string  MediaType  { get; set; } = "unknown";
    public bool    IsPlayable { get; set; }
    public bool    Favorite   { get; set; }
    public bool    Available  { get; set; } = true;

    // ItemMapping image / full item metadata
    public MediaImage?    Image    { get; set; }
    public MediaMetadata? Metadata { get; set; }

    // Album / track
    public List<MediaItem>? Artists     { get; set; }
    public MediaItem?       Album       { get; set; }
    public int?             Year        { get; set; }
    public double?          Duration    { get; set; }
    public int?             TrackNumber { get; set; }
    public int?             DiscNumber  { get; set; }
    public int?             Position    { get; set; }

    // Playlist
    public string? Owner      { get; set; }
    public bool?   IsEditable { get; set; }

    // Podcast / audiobook (authors and narrators arrive as plain strings or artist objects)
    public string?            Publisher     { get; set; }
    public int?               TotalEpisodes { get; set; }
    public List<JsonElement>? Authors       { get; set; }
    public MediaItem?         Podcast       { get; set; }

    // Browse / recommendation folder
    public string?          Path             { get; set; }
    public string?          Subtitle         { get; set; }
    public string?          Icon             { get; set; }
    public List<MediaItem>? Items            { get; set; }
    public bool?            EnabledByDefault { get; set; }

    // Derived display helpers

    [JsonIgnore]
    public string ArtistsText => Artists is { Count: > 0 } ? string.Join(", ", Artists.Select(a => a.Name)) : "";

    [JsonIgnore]
    public string AuthorsText => Authors is null ? "" : string.Join(", ", Authors.Select(a =>
        a.ValueKind == JsonValueKind.String ? a.GetString() : a.TryGetProperty("name", out var n) ? n.GetString() : null).Where(s => !string.IsNullOrEmpty(s)));

    [JsonIgnore]
    public string SubtitleText => MediaType switch
    {
        "track"           => Album is null ? ArtistsText : $"{ArtistsText} • {Album.Name}",
        "album"           => Year is null ? ArtistsText : $"{ArtistsText} • {Year}",
        "playlist"        => Owner ?? "",
        "artist"          => "Artist",
        "radio"           => "Radio",
        "podcast"         => Publisher ?? "Podcast",
        "podcast_episode" => Podcast?.Name ?? "Episode",
        "audiobook"       => AuthorsText.Length > 0 ? AuthorsText : "Audiobook",
        "genre"           => "Genre",
        _                 => Subtitle ?? "",
    };

    [JsonIgnore]
    public string DurationText => Duration is > 0 ? Format.Duration(Duration.Value) : "";

    [JsonIgnore]
    public bool HasDetailPage => MediaType is "album" or "artist" or "playlist" or "podcast" or "audiobook" or "genre";

    /// <summary>Name of the recommendation row an item was picked from (Top Picks collage only).</summary>
    [JsonIgnore] public string? Tag { get; set; }
    [JsonIgnore] public string  TagText   => Tag?.ToUpperInvariant() ?? "";
    [JsonIgnore] public string  TypeLabel => MediaType.Length > 0 ? char.ToUpperInvariant(MediaType[0]) + MediaType[1..].Replace('_', ' ') : "";

    [JsonIgnore]
    public string? ThumbUrl => Volatile(Images.Resolver(FindImage(), 256));

    [JsonIgnore]
    public string? LargeImageUrl => Volatile(Images.Resolver(FindImage(), 512));

    /// <summary>
    /// Playlist covers are the one kind of art that changes; the fragment tells the art cache to refresh them now and
    /// then, and is stripped before any request. Inline data: images are left alone, they are never cached.
    /// </summary>
    private string? Volatile(string? url) => MediaType == "playlist" && url?.StartsWith("http", StringComparison.Ordinal) == true ? url + "#playlist" : url;

    [JsonIgnore]
    public string TypeGlyph => MediaType switch
    {
        "artist"   => "\uE77B",
        "album"    => "\uE93C",
        "track"    => "\uEC4F",
        "playlist" => "\uE90B",
        "radio"    => "\uE7F5",
        "folder"   => "\uE8B7",
        "podcast" or "podcast_episode" => "\uE720",
        "audiobook" => "\uE8F1",
        "genre"     => "\uE8D6",
        _          => "\uEC4F",
    };

    /// <summary>Best thumbnail for this item: own image, a cover borrowed from one of its tracks (albums), album image, then artist image.</summary>
    public MediaImage? FindImage()
    {
        if (OwnImage() is { } own) return own;
        if (MediaType == "album" && Uri.Length > 0 && Images.BorrowedCovers.TryGetValue(Uri, out var borrowed)) return borrowed;
        if (Album?.FindImage() is { } albumImage) return albumImage;
        return Artists?.Select(a => a.FindImage()).FirstOrDefault(i => i is not null);
    }

    /// <summary>The item's own image only, never one inherited from its album or artists.</summary>
    public MediaImage? OwnImage()
        => Image ?? Metadata?.Images?.FirstOrDefault(i => i.Type == "thumb") ?? Metadata?.Images?.FirstOrDefault();
}

public sealed class SearchResults
{
    public List<MediaItem> Artists   { get; set; } = [];
    public List<MediaItem> Albums    { get; set; } = [];
    public List<MediaItem> Tracks    { get; set; } = [];
    public List<MediaItem> Playlists { get; set; } = [];
    public List<MediaItem> Radio     { get; set; } = [];
}

public sealed class ProviderInstance
{
    public string Type       { get; set; } = "";
    public string Domain     { get; set; } = "";
    public string Name       { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public bool   Available  { get; set; }
}

// =========================================================================
// PLAYERS / QUEUES
// =========================================================================

public sealed class PlayerMedia
{
    public string? Uri      { get; set; }
    public string? Title    { get; set; }
    public string? Artist   { get; set; }
    public string? Album    { get; set; }
    public string? ImageUrl { get; set; }
    public double? Duration { get; set; }
}

public sealed class Player
{
    public string        PlayerId          { get; set; } = "";
    public string        Provider          { get; set; } = "";
    public string        Type              { get; set; } = "player";
    public string        Name              { get; set; } = "";
    public bool          Available         { get; set; }
    public bool          Enabled           { get; set; } = true;
    public bool          HideInUi          { get; set; }
    public string        PlaybackState     { get; set; } = "idle";
    public bool?         Powered           { get; set; }
    public double?       VolumeLevel       { get; set; }
    public bool?         VolumeMuted       { get; set; }
    public string?       ActiveSource      { get; set; }
    public string?       SyncedTo          { get; set; }
    public string?       ActiveGroup       { get; set; }
    public List<string>  GroupMembers      { get; set; } = [];
    public List<string>  SupportedFeatures { get; set; } = [];
    public PlayerMedia?  CurrentMedia      { get; set; }
    public string?       Icon              { get; set; }
    public ActiveSourceAudio? ActiveSourceAudio { get; set; }

    /// <summary>
    /// Player id of this PC's own speaker. The server flags web players as
    /// hide_in_ui so other clients do not list them; the client that owns one
    /// still shows it, the same way the web app shows its own web player.
    /// </summary>
    public static string? OwnPlayerId { get; set; }

    [JsonIgnore] public bool   IsThisDevice => PlayerId == OwnPlayerId;
    [JsonIgnore] public string DisplayName  => IsThisDevice ? $"{Name} (This Device)" : Name;
    [JsonIgnore] public bool   IsPlaying    => PlaybackState == "playing";
    [JsonIgnore] public bool   HasMedia     => CurrentMedia?.ImageUrl is { Length: > 0 };   // the server clears current_media when a player really stops
    [JsonIgnore] public string? ArtUrl      => HasMedia ? CurrentMedia!.ImageUrl : null;   // artwork replaces the type icon while something is loaded, paused included
    [JsonIgnore] public bool   IsVisible    => Enabled && Available && Type != "source" && (!HideInUi || IsThisDevice);

    [JsonIgnore]
    public string NowPlayingText => CurrentMedia is { Title.Length: > 0 } m
        ? (string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} • {m.Artist}")
        : PlaybackState == "paused" ? "Paused" : "Idle";

    [JsonIgnore]
    public string TypeGlyph => IsThisDevice ? "\uE7F4" : Type switch
    {
        "group"       => "",
        "stereo_pair" => "",
        "display"     => "",
        _             => "",
    };

    public bool Supports(string feature) => SupportedFeatures.Contains(feature);
}

public sealed class AudioFormat
{
    public string ContentType { get; set; } = "";
    public int    SampleRate  { get; set; }
    public int    BitDepth    { get; set; }
    public int    Channels    { get; set; }
    public int    BitRate     { get; set; }

    /// <summary>Human readable format, for example "FLAC • 44.1 kHz • 16-bit".</summary>
    [JsonIgnore]
    public string Text
    {
        get
        {
            var parts = new List<string>();
            if (ContentType.Length > 0) parts.Add(ContentType.ToUpperInvariant());
            if (SampleRate > 0)         parts.Add($"{SampleRate / 1000.0:0.#} kHz");
            if (BitDepth > 0)           parts.Add($"{BitDepth}-bit");
            if (BitRate > 0 && BitDepth == 0) parts.Add($"{BitRate} kbps");
            return string.Join(" • ", parts);
        }
    }
}

public sealed class AudioFidelity
{
    public string Quality    { get; set; } = "unknown";
    public bool?  BitPerfect { get; set; }

    /// <summary>Short chip label as shown in the web UI: LQ, SQ, HQ, HR.</summary>
    [JsonIgnore] public string Label => Quality switch { "low" => "LQ", "standard" => "SQ", "lossless" => "HQ", "hi_res" => "HR", _ => "" };

    /// <summary>Chip dot color as a hex string matching the web UI tiers.</summary>
    [JsonIgnore] public string Color => Quality switch { "low" => "#FFA500", "standard" => "#90EE90", "lossless" => "#2ECC71", "hi_res" => "#00E5FF", _ => "#9E9E9E" };
}

public sealed class AudioProcessingChain
{
    public AudioFidelity? InputFidelity { get; set; }
}

public sealed class StreamDetails
{
    public AudioFormat?          AudioFormat     { get; set; }
    public AudioProcessingChain? AudioProcessing { get; set; }
}

public sealed class ActiveSourceAudio
{
    public AudioFormat?   InputFormat   { get; set; }
    public AudioFidelity? InputFidelity { get; set; }
}

public sealed class QueueItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string         QueueId       { get; set; } = "";
    public string         QueueItemId   { get; set; } = "";
    public string         Name          { get; set; } = "";
    public double?        Duration      { get; set; }
    public int            SortIndex     { get; set; }
    public bool           Available     { get; set; } = true;
    public MediaItem?     MediaItem     { get; set; }
    public MediaImage?    Image         { get; set; }
    public StreamDetails? Streamdetails { get; set; }

    private bool isNowPlaying;

    /// <summary>True when this is the current queue item and it is playing (drives the live level bars).</summary>
    [JsonIgnore]
    public bool IsNowPlaying
    {
        get => isNowPlaying;
        set
        {
            if (isNowPlaying == value) return;
            isNowPlaying = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsNowPlaying)));
        }
    }

    [JsonIgnore] public string SubtitleText => MediaItem?.SubtitleText ?? "";
    [JsonIgnore] public string DurationText => Duration is > 0 ? Format.Duration(Duration.Value) : "";
    [JsonIgnore] public string? ThumbUrl => Images.Resolver(FindImage(), 160);

    public MediaImage? FindImage() => Image ?? MediaItem?.FindImage();
}

public sealed class PlayerQueue
{
    public string     QueueId                { get; set; } = "";
    public bool       Active                 { get; set; }
    public string     DisplayName            { get; set; } = "";
    public bool       Available              { get; set; }
    public int        Items                  { get; set; }
    public bool       ShuffleEnabled         { get; set; }
    public bool       AutoplayEnabled        { get; set; }
    public bool       CrossfadeEnabled       { get; set; }
    public string     RepeatMode             { get; set; } = "off";
    public int?       CurrentIndex           { get; set; }
    public double     ElapsedTime            { get; set; }
    public double     ElapsedTimeLastUpdated { get; set; }
    public string     State                  { get; set; } = "idle";
    public QueueItem? CurrentItem            { get; set; }
    public QueueItem? NextItem               { get; set; }

    /// <summary>
    /// Position right now: the last reported elapsed time, advanced by the wall clock while playing. MassClient stamps
    /// ElapsedTimeLastUpdated with this PC's clock whenever a queue or time report arrives, so the math never mixes the
    /// server's clock with ours (a queue resumed after hours used to carry its old server timestamp and jump to the end).
    /// </summary>
    [JsonIgnore]
    public double ElapsedNow => State == "playing"
        ? ElapsedTime + Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - ElapsedTimeLastUpdated)
        : ElapsedTime;
}

// =========================================================================
// PROTOCOL MESSAGES
// =========================================================================

public sealed class ApiException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    // Error codes from music_assistant_models/errors.py
    public const int AuthenticationRequired = 20;
    public const int AuthenticationFailed   = 21;
    public const int InsufficientPermissions = 22;
    public const int InvalidToken           = 23;
    public const int SetupRequired          = 503;

    [JsonIgnore] public bool IsAuthError => Code is AuthenticationRequired or AuthenticationFailed or InvalidToken;
}

/// <summary>Resolves a MediaImage into a display URL. Set by the app once a client exists.</summary>
public static class Images
{
    public static Func<MediaImage?, int, string?> Resolver { get; set; } = (_, _) => null;

    /// <summary>
    /// Covers for albums the server has no image for, taken from one of the album's own tracks, keyed by album URI.
    /// Filled by MassClient whenever an album's tracks load; read by MediaItem.FindImage, so every list, the player
    /// bar, the queue and the media overlay pick it up.
    /// </summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<string, MediaImage> BorrowedCovers { get; } = new();
}

/// <summary>Small formatting helpers shared by models and views.</summary>
public static class Format
{
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
