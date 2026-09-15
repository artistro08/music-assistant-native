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
    /// <summary>Serializer options for every server message: snake_case names, case-insensitive reads, nulls left out of commands, and enums as snake_case strings.</summary>
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
    /// <summary>Unique id of the Music Assistant server installation.</summary>
    public string  ServerId      { get; set; } = "";

    /// <summary>Version of the server software, for example 2.6.0.</summary>
    public string  ServerVersion { get; set; } = "";

    /// <summary>Version of the API schema the server speaks.</summary>
    public int     SchemaVersion { get; set; }

    /// <summary>Base URL the server advertises for its web interface and HTTP resources.</summary>
    public string  BaseUrl       { get; set; } = "";

    /// <summary>False while the server still needs its first-run setup (no users yet).</summary>
    public bool    OnboardDone   { get; set; } = true;

    /// <summary>Friendly name of the server, when one is set.</summary>
    public string? Name          { get; set; }
}

/// <summary>A Music Assistant user account, as returned after signing in.</summary>
public sealed class User
{
    /// <summary>Server-assigned id of the user.</summary>
    public string  UserId      { get; set; } = "";

    /// <summary>Login name of the user.</summary>
    public string  Username    { get; set; } = "";

    /// <summary>The user's role on the server, such as admin or user; admins can manage settings like remote access.</summary>
    public string  Role        { get; set; } = "";

    /// <summary>Name to show for the user instead of the login name, when set.</summary>
    public string? DisplayName { get; set; }

    /// <summary>URL of the user's profile picture, when set.</summary>
    public string? AvatarUrl   { get; set; }
}

/// <summary>A way to sign in to the server, as listed by auth/providers.</summary>
public sealed class AuthProvider
{
    /// <summary>Id of the provider to pass when signing in, for example builtin or homeassistant.</summary>
    public string ProviderId       { get; set; } = "";

    /// <summary>Kind of provider, for example builtin or oauth_homeassistant.</summary>
    public string ProviderType     { get; set; } = "";

    /// <summary>True when signing in happens in a browser that redirects back to the app (OAuth).</summary>
    public bool   RequiresRedirect { get; set; }
}

/// <summary>Answer to the auth/login command.</summary>
public sealed class LoginResult
{
    /// <summary>True when the username and password were accepted.</summary>
    public bool    Success     { get; set; }

    /// <summary>Long-lived access token to store and authenticate with later, when the login succeeded.</summary>
    public string? AccessToken { get; set; }

    /// <summary>The server's reason when the login failed.</summary>
    public string? Error       { get; set; }

    /// <summary>The signed-in user, when the login succeeded.</summary>
    public User?   User        { get; set; }
}

/// <summary>Server-side remote access state (admin only).</summary>
public sealed class RemoteAccessInfo
{
    /// <summary>True when an admin turned remote access on.</summary>
    public bool   Enabled      { get; set; }

    /// <summary>True while the server's remote access service is running.</summary>
    public bool   Running      { get; set; }

    /// <summary>True while the server is connected to the signaling relay and reachable remotely.</summary>
    public bool   Connected    { get; set; }

    /// <summary>The server's Remote ID, 26 base32 characters derived from its certificate fingerprint.</summary>
    public string RemoteId     { get; set; } = "";

    /// <summary>True when the connection is routed through Home Assistant Cloud.</summary>
    public bool   UsingHaCloud { get; set; }

    /// <summary>Remote ID grouped like the web UI shows it: 8-5-5-8.</summary>
    [JsonIgnore]
    public string RemoteIdDisplay => RemoteId.Length == 26 ? $"{RemoteId[..8]}-{RemoteId[8..13]}-{RemoteId[13..18]}-{RemoteId[18..]}" : RemoteId;
}

/// <summary>Answer to the auth command that authenticates a connection with a token.</summary>
public sealed class AuthResult
{
    /// <summary>True when the token was accepted.</summary>
    public bool  Authenticated { get; set; }

    /// <summary>The user the token belongs to, or null when it was rejected.</summary>
    public User? User          { get; set; }
}

// =========================================================================
// MEDIA ITEMS
// =========================================================================

/// <summary>An image attached to a media item, resolved to a display URL through the server's image proxy.</summary>
public sealed class MediaImage
{
    /// <summary>Kind of image, such as thumb, fanart or logo.</summary>
    public string  Type               { get; set; } = "thumb";

    /// <summary>Where the image lives: a URL, a provider-specific path, or an inline data: URI.</summary>
    public string  Path               { get; set; } = "";

    /// <summary>Provider instance or domain that can load the image path.</summary>
    public string  Provider           { get; set; } = "";

    /// <summary>True when the path is a public URL any client can load directly.</summary>
    public bool    RemotelyAccessible { get; set; }

    /// <summary>Opaque image proxy id on newer servers, used instead of the path and provider query.</summary>
    public string? ProxyId            { get; set; }
}

/// <summary>Extra descriptive data of a full media item.</summary>
public sealed class MediaMetadata
{
    /// <summary>Images of the item; the first thumb is used as its artwork.</summary>
    public List<MediaImage>? Images      { get; set; }

    /// <summary>Description or biography text, when the provider has one.</summary>
    public string?           Description { get; set; }

    /// <summary>Genre names the item is tagged with.</summary>
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
    /// <summary>Raised when IsLoading or IsNowPlaying changes, so bound views update.</summary>
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

    /// <summary>The item's id on its provider (the database id for library items).</summary>
    public string  ItemId     { get; set; } = "";

    /// <summary>Provider instance or domain that holds this copy of the item (library for library items).</summary>
    public string  Provider   { get; set; } = "";

    /// <summary>Display name of the item.</summary>
    public string  Name       { get; set; } = "";

    /// <summary>Version label such as Remastered or Live, when the item has one.</summary>
    public string? Version    { get; set; }

    /// <summary>Music Assistant URI of the item, used to play, favorite or look it up.</summary>
    public string  Uri        { get; set; } = "";

    /// <summary>Singular media type: artist, album, track, playlist, radio, podcast, podcast_episode, audiobook, genre, folder, ...</summary>
    public string  MediaType  { get; set; } = "unknown";

    /// <summary>True when the item can be started on a player as is.</summary>
    public bool    IsPlayable { get; set; }

    /// <summary>True when the item is marked as a favorite.</summary>
    public bool    Favorite   { get; set; }

    /// <summary>The item's own availability flag, used when it has no provider mappings.</summary>
    public bool    Available  { get; set; } = true;

    // ItemMapping image / full item metadata.

    /// <summary>Single image carried by lightweight item mappings (an album or artist reference inside another item).</summary>
    public MediaImage?    Image    { get; set; }

    /// <summary>Descriptive metadata of a full item, including its images.</summary>
    public MediaMetadata? Metadata { get; set; }

    // Album / track.

    /// <summary>Artists of an album or track.</summary>
    public List<MediaItem>? Artists     { get; set; }

    /// <summary>The album a track belongs to.</summary>
    public MediaItem?       Album       { get; set; }

    /// <summary>Release year of an album.</summary>
    public int?             Year        { get; set; }

    /// <summary>Length in seconds.</summary>
    public double?          Duration    { get; set; }

    /// <summary>Track number within its disc; 0 or null when the provider has none.</summary>
    public int?             TrackNumber { get; set; }

    /// <summary>Disc number within the album.</summary>
    public int?             DiscNumber  { get; set; }

    /// <summary>Position of a track within the playlist it was listed from.</summary>
    public int?             Position    { get; set; }

    // Playlist.

    /// <summary>Name of the playlist's owner on its provider.</summary>
    public string?       Owner               { get; set; }

    /// <summary>True when the provider allows adding and removing tracks.</summary>
    public bool?         IsEditable          { get; set; }

    /// <summary>True for playlists the provider generates itself, such as mixes.</summary>
    public bool?         IsDynamic           { get; set; }

    /// <summary>Media types a playlist accepts, such as track or podcast_episode.</summary>
    public List<string>? SupportedMediatypes { get; set; }

    /// <summary>Who owns and may edit a Music Assistant playlist; absent on older servers.</summary>
    public JsonElement?  Access              { get; set; }

    /// <summary>Where the item comes from, and whether each copy is in the library.</summary>
    public List<ProviderMapping>? ProviderMappings { get; set; }

    // Podcast / audiobook (authors and narrators arrive as plain strings or artist objects).

    /// <summary>Publisher of a podcast or audiobook.</summary>
    public string?            Publisher     { get; set; }

    /// <summary>Number of episodes a podcast has.</summary>
    public int?               TotalEpisodes { get; set; }

    /// <summary>Authors of an audiobook, each a plain name string or an artist object.</summary>
    public List<JsonElement>? Authors       { get; set; }

    /// <summary>The podcast a podcast episode belongs to.</summary>
    public MediaItem?         Podcast       { get; set; }

    // Browse / recommendation folder.

    /// <summary>Browse path to pass to music/browse to open this folder.</summary>
    public string?          Path             { get; set; }

    /// <summary>Secondary line of a folder or recommendation row.</summary>
    public string?          Subtitle         { get; set; }

    /// <summary>Icon name the server suggests for a folder or recommendation row.</summary>
    public string?          Icon             { get; set; }

    /// <summary>Items inside a recommendation or genre overview folder, when the server includes them.</summary>
    public List<MediaItem>? Items            { get; set; }

    /// <summary>False for recommendation rows that are hidden by default; the home page skips them.</summary>
    public bool?            EnabledByDefault { get; set; }

    // Derived display helpers.

    /// <summary>In the library: a favorite always is, otherwise any provider copy marked in_library (the web app's rule).</summary>
    [JsonIgnore]
    public bool IsInLibrary => Favorite || (ProviderMappings?.Any(m => m.InLibrary == true) == true);

    /// <summary>Playable right now: any available provider copy, or the item's own flag when it carries no mappings (folders, item mappings).</summary>
    [JsonIgnore]
    public bool IsAvailableNow => ProviderMappings is { Count: > 0 } mappings ? mappings.Any(m => m.Available != false) : Available;

    /// <summary>Artist names joined with commas, or an empty string.</summary>
    [JsonIgnore]
    public string ArtistsText => Artists is { Count: > 0 } ? string.Join(", ", Artists.Select(a => a.Name)) : "";

    /// <summary>Author names joined with commas, or an empty string.</summary>
    [JsonIgnore]
    public string AuthorsText => Authors is null ? "" : string.Join(", ", Authors.Select(a =>
        a.ValueKind == JsonValueKind.String ? a.GetString() : a.TryGetProperty("name", out JsonElement n) ? n.GetString() : null).Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Second line under the item's name, chosen by media type (artists and album for a track, owner for a playlist, ...).</summary>
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

    /// <summary>Duration formatted as m:ss or h:mm:ss, or an empty string when unknown.</summary>
    [JsonIgnore]
    public string DurationText => Duration is > 0 ? Format.Duration(Duration.Value) : "";

    /// <summary>True for media types that open their own detail page when clicked.</summary>
    [JsonIgnore]
    public bool HasDetailPage => MediaType is "album" or "artist" or "playlist" or "podcast" or "audiobook" or "genre";

    /// <summary>Name of the recommendation row an item was picked from (Top Picks collage only).</summary>
    [JsonIgnore]
    public string? Tag { get; set; }

    /// <summary>The recommendation row name in upper case, or an empty string.</summary>
    [JsonIgnore]
    public string  TagText   => Tag?.ToUpperInvariant() ?? "";

    /// <summary>The media type as a readable label, for example "Podcast episode".</summary>
    [JsonIgnore]
    public string  TypeLabel => MediaType.Length > 0 ? $"{char.ToUpperInvariant(MediaType[0])}{MediaType[1..].Replace('_', ' ')}" : "";

    /// <summary>Display URL of the item's thumbnail at 256 pixels, or null when it has no image.</summary>
    [JsonIgnore]
    public string? ThumbUrl => Volatile(Images.Resolver(FindImage(), 256));

    /// <summary>Display URL of the item's image at 512 pixels, or null when it has no image.</summary>
    [JsonIgnore]
    public string? LargeImageUrl => Volatile(Images.Resolver(FindImage(), 512));

    /// <summary>
    /// Playlist covers are the one kind of art that changes; the fragment tells the art cache to refresh them now and
    /// then, and is stripped before any request. Inline data: images are left alone, they are never cached.
    /// </summary>
    private string? Volatile(string? url) => (MediaType == "playlist") && (url?.StartsWith("http", StringComparison.Ordinal) == true) ? $"{url}#playlist" : url;

    /// <summary>Segoe Fluent Icons glyph for the item's media type, shown when it has no artwork.</summary>
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
    /// <returns>The image to show, or null when none of them has one.</returns>
    public MediaImage? FindImage()
    {
        if (OwnImage() is { } own) return own;
        if ((MediaType == "album") && (Uri.Length > 0) && Images.BorrowedCovers.TryGetValue(Uri, out MediaImage? borrowed)) return borrowed;
        if (Album?.FindImage() is { } albumImage) return albumImage;
        return Artists?.Select(a => a.FindImage()).FirstOrDefault(i => i is not null);
    }

    /// <summary>The item's own image only, never one inherited from its album or artists.</summary>
    public MediaImage? OwnImage()
        => Image ?? Metadata?.Images?.FirstOrDefault(i => i.Type == "thumb") ?? Metadata?.Images?.FirstOrDefault();
}

/// <summary>One way the server can deliver audio to a player: its own protocol or a protocol player it wraps.</summary>
public sealed class OutputProtocol
{
    /// <summary>Id of the protocol player behind this output, or "native" for the player's own protocol.</summary>
    public string OutputProtocolId { get; set; } = "";

    /// <summary>Display name of the protocol, such as Sendspin or AirPlay.</summary>
    public string Name             { get; set; } = "";

    /// <summary>Provider domain of the protocol, such as sendspin.</summary>
    public string ProtocolDomain   { get; set; } = "";

    /// <summary>True while the server can reach the player through this protocol.</summary>
    public bool   Available        { get; set; }
}

/// <summary>One provider copy of a media item: which provider instance holds it, under which id, and whether it is in the library.</summary>
public sealed class ProviderMapping
{
    /// <summary>The item's id on that provider.</summary>
    public string ItemId           { get; set; } = "";

    /// <summary>Provider type, for example spotify or filesystem_local.</summary>
    public string ProviderDomain   { get; set; } = "";

    /// <summary>Id of the configured provider instance that holds this copy.</summary>
    public string ProviderInstance { get; set; } = "";

    /// <summary>
    /// Whether this copy can be played right now. Nullable: queue items carry mappings with these unset (null).
    /// </summary>
    public bool?  Available        { get; set; }

    /// <summary>Whether this copy is in the library; null when the server did not say.</summary>
    public bool?  InLibrary        { get; set; }
}

/// <summary>Results of music/search, grouped by media type.</summary>
public sealed class SearchResults
{
    /// <summary>Matching artists.</summary>
    public List<MediaItem> Artists   { get; set; } = [];

    /// <summary>Matching albums.</summary>
    public List<MediaItem> Albums    { get; set; } = [];

    /// <summary>Matching tracks.</summary>
    public List<MediaItem> Tracks    { get; set; } = [];

    /// <summary>Matching playlists.</summary>
    public List<MediaItem> Playlists { get; set; } = [];

    /// <summary>Matching radio stations.</summary>
    public List<MediaItem> Radio     { get; set; } = [];
}

/// <summary>A provider configured on the server (a music source, player platform, metadata source or plugin).</summary>
public sealed class ProviderInstance
{
    /// <summary>Kind of provider, such as music, player, metadata or plugin.</summary>
    public string       Type                { get; set; } = "";

    /// <summary>Provider type, for example spotify or builtin.</summary>
    public string       Domain              { get; set; } = "";

    /// <summary>Display name of this provider instance.</summary>
    public string       Name                { get; set; } = "";

    /// <summary>Unique id of this configured instance; several instances can share a domain.</summary>
    public string       InstanceId          { get; set; } = "";

    /// <summary>True when the provider is loaded and working.</summary>
    public bool         Available           { get; set; }

    /// <summary>True for streaming service providers such as Spotify or YouTube Music; null on servers that do not report it.</summary>
    public bool?        IsStreamingProvider { get; set; }

    /// <summary>Feature flags the provider supports, such as playlist_create or playlist_tracks_edit.</summary>
    public List<string> SupportedFeatures   { get; set; } = [];

    /// <summary>Check whether the provider supports a feature.</summary>
    /// <param name="feature">The snake_case feature name.</param>
    /// <returns>True when the feature is in SupportedFeatures.</returns>
    public bool Supports(string feature) => SupportedFeatures?.Contains(feature) == true;
}

// =========================================================================
// PLAYERS / QUEUES
// =========================================================================

/// <summary>What a player reports it is playing right now, including media from outside Music Assistant.</summary>
public sealed class PlayerMedia
{
    /// <summary>URI of the playing media.</summary>
    public string? Uri      { get; set; }

    /// <summary>Title of the playing media.</summary>
    public string? Title    { get; set; }

    /// <summary>Artist of the playing media.</summary>
    public string? Artist   { get; set; }

    /// <summary>Album of the playing media.</summary>
    public string? Album    { get; set; }

    /// <summary>Artwork URL of the playing media.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Length of the playing media in seconds.</summary>
    public double? Duration { get; set; }
}

/// <summary>A speaker, group or other playback target registered on the Music Assistant server.</summary>
public sealed class Player
{
    /// <summary>Unique id of the player on the server.</summary>
    public string             PlayerId          { get; set; } = "";

    /// <summary>Provider instance that exposes the player (AirPlay, Chromecast, Sendspin, ...).</summary>
    public string             Provider          { get; set; } = "";

    /// <summary>Kind of player, such as player, group, stereo_pair or source.</summary>
    public string             Type              { get; set; } = "player";

    /// <summary>Display name of the player.</summary>
    public string             Name              { get; set; } = "";

    /// <summary>True while the player is reachable.</summary>
    public bool               Available         { get; set; }

    /// <summary>False when the user disabled the player in the server settings.</summary>
    public bool               Enabled           { get; set; } = true;

    /// <summary>True when the server asks clients not to list the player (web players, for example).</summary>
    public bool               HideInUi          { get; set; }

    /// <summary>Playback state: idle, paused or playing.</summary>
    public string             PlaybackState     { get; set; } = "idle";

    /// <summary>Whether the player is powered on; null when it has no power control.</summary>
    public bool?              Powered           { get; set; }

    /// <summary>Volume from 0 to 100; null when the player has no volume control.</summary>
    public double?            VolumeLevel       { get; set; }

    /// <summary>Whether the player is muted; null when it has no mute control.</summary>
    public bool?              VolumeMuted       { get; set; }

    /// <summary>Id of the source the player plays from: a queue id while Music Assistant drives it, otherwise an external source.</summary>
    public string?            ActiveSource      { get; set; }

    /// <summary>Player id of the group leader this player is synced to, or null.</summary>
    public string?            SyncedTo          { get; set; }

    /// <summary>Id of the group player that is currently playing through this player, or null.</summary>
    public string?            ActiveGroup       { get; set; }

    /// <summary>Player ids that belong to this group or are synced with this player.</summary>
    public List<string>       GroupMembers      { get; set; } = [];

    /// <summary>Feature flags the player supports, such as volume_set, volume_mute, power, seek or next_previous.</summary>
    public List<string>       SupportedFeatures { get; set; } = [];

    /// <summary>What the player reports it is playing, or null when nothing is loaded.</summary>
    public PlayerMedia?       CurrentMedia      { get; set; }

    /// <summary>Icon name the server suggests for the player.</summary>
    public string?            Icon              { get; set; }

    /// <summary>Audio format and quality of the source the player is playing, when the server reports it.</summary>
    public ActiveSourceAudio? ActiveSourceAudio { get; set; }

    /// <summary>
    /// Sendspin client_id of this PC's own speaker. The server lists the speaker as a universal player whose output
    /// protocols include that id; an older app version's hidden web player used it as the player id directly.
    /// </summary>
    public static string? OwnPlayerId { get; set; }

    /// <summary>The ways the server can reach this player (native, Sendspin, AirPlay, ...), for a universal player the protocol players it wraps.</summary>
    public List<OutputProtocol>? OutputProtocols { get; set; }

    /// <summary>True when this player is this PC's own speaker, directly or through a universal player wrapping it.</summary>
    [JsonIgnore]
    public bool    IsThisDevice => (OwnPlayerId is not null) && ((PlayerId == OwnPlayerId) || (OutputProtocols?.Any(p => p.OutputProtocolId == OwnPlayerId) == true));

    /// <summary>The player's name, marked "(This Device)" for this PC's own speaker.</summary>
    [JsonIgnore]
    public string  DisplayName  => IsThisDevice ? $"{Name} (This Device)" : Name;

    /// <summary>True while the player is playing.</summary>
    [JsonIgnore]
    public bool    IsPlaying    => PlaybackState == "playing";

    /// <summary>True while media with artwork is loaded. The server clears current_media when a player really stops.</summary>
    [JsonIgnore]
    public bool    HasMedia     => CurrentMedia?.ImageUrl is { Length: > 0 };

    /// <summary>Artwork of the loaded media, or null. Artwork replaces the type icon while something is loaded, paused included.</summary>
    [JsonIgnore]
    public string? ArtUrl       => HasMedia ? CurrentMedia!.ImageUrl : null;

    /// <summary>True when the player belongs in player lists: enabled, available, not a source, and not hidden unless it is this PC.</summary>
    [JsonIgnore]
    public bool    IsVisible    => Enabled && Available && (Type != "source") && (!HideInUi || IsThisDevice);

    /// <summary>One-line summary of what is playing: title and artist, or Paused or Idle.</summary>
    [JsonIgnore]
    public string NowPlayingText => CurrentMedia is { Title.Length: > 0 } m
        ? (string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} • {m.Artist}")
        : PlaybackState == "paused" ? "Paused" : "Idle";

    /// <summary>Segoe Fluent Icons glyph for the player's type, with its own glyph for this PC.</summary>
    [JsonIgnore]
    public string TypeGlyph => IsThisDevice ? "\uE7F4" : Type switch
    {
        "group"       => "\uE8FD",
        "stereo_pair" => "\uE7F5",
        "display"     => "\uE7F4",
        _             => "\uE7F5",
    };

    /// <summary>Check whether the player supports a feature.</summary>
    /// <param name="feature">The snake_case feature name, for example volume_set.</param>
    /// <returns>True when the feature is in SupportedFeatures.</returns>
    public bool Supports(string feature) => SupportedFeatures.Contains(feature);
}

/// <summary>Technical format of an audio stream.</summary>
public sealed class AudioFormat
{
    /// <summary>Codec or container, for example flac, mp3 or aac.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>Sample rate in hertz.</summary>
    public int    SampleRate  { get; set; }

    /// <summary>Bits per sample; 0 for lossy formats that do not report one.</summary>
    public int    BitDepth    { get; set; }

    /// <summary>Number of audio channels.</summary>
    public int    Channels    { get; set; }

    /// <summary>Bit rate in kilobits per second.</summary>
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
            if ((BitRate > 0) && (BitDepth == 0)) parts.Add($"{BitRate} kbps");
            return string.Join(" • ", parts);
        }
    }
}

/// <summary>Quality tier of an audio source, shown as the quality chip in the player bar.</summary>
public sealed class AudioFidelity
{
    /// <summary>Quality tier: low, standard, lossless, hi_res or unknown.</summary>
    public string Quality    { get; set; } = "unknown";

    /// <summary>Whether the audio reaches the player without resampling or other changes; null when unknown.</summary>
    public bool?  BitPerfect { get; set; }

    /// <summary>Short chip label as shown in the web UI: LQ, SQ, HQ, HR.</summary>
    [JsonIgnore]
    public string Label => Quality switch { "low" => "LQ", "standard" => "SQ", "lossless" => "HQ", "hi_res" => "HR", _ => "" };

    /// <summary>Chip dot color as a hex string matching the web UI tiers.</summary>
    [JsonIgnore]
    public string Color => Quality switch { "low" => "#FFA500", "standard" => "#90EE90", "lossless" => "#2ECC71", "hi_res" => "#00E5FF", _ => "#9E9E9E" };
}

/// <summary>How the server processes a queue item's audio on its way to the player.</summary>
public sealed class AudioProcessingChain
{
    /// <summary>Quality tier of the audio as it came from the provider.</summary>
    public AudioFidelity? InputFidelity { get; set; }
}

/// <summary>Stream details of a queue item that has been (or is being) played.</summary>
public sealed class StreamDetails
{
    /// <summary>Format of the audio the provider delivers.</summary>
    public AudioFormat?          AudioFormat     { get; set; }

    /// <summary>The server's processing of the stream, including its input quality tier.</summary>
    public AudioProcessingChain? AudioProcessing { get; set; }
}

/// <summary>Audio details of the source a player is currently playing, used when no queue item carries stream details.</summary>
public sealed class ActiveSourceAudio
{
    /// <summary>Format of the audio the source delivers.</summary>
    public AudioFormat?   InputFormat   { get; set; }

    /// <summary>Quality tier of the audio the source delivers.</summary>
    public AudioFidelity? InputFidelity { get; set; }
}

/// <summary>One entry in a player queue.</summary>
public sealed class QueueItem : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Raised when IsNowPlaying changes, so bound rows update.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Id of the queue this item belongs to.</summary>
    public string         QueueId       { get; set; } = "";

    /// <summary>Server-assigned id of this entry, unique within the queue.</summary>
    public string         QueueItemId   { get; set; } = "";

    /// <summary>Display name of the entry, usually "Artist - Title".</summary>
    public string         Name          { get; set; } = "";

    /// <summary>Length in seconds, when known.</summary>
    public double?        Duration      { get; set; }

    /// <summary>Zero-based position of the entry in the queue.</summary>
    public int            SortIndex     { get; set; }

    /// <summary>False when the item can no longer be played.</summary>
    public bool           Available     { get; set; } = true;

    /// <summary>The media item this entry plays.</summary>
    public MediaItem?     MediaItem     { get; set; }

    /// <summary>Artwork of the entry, when the server attaches one.</summary>
    public MediaImage?    Image         { get; set; }

    /// <summary>Stream format and quality, once the server has started streaming the entry.</summary>
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

    /// <summary>The track's own title. The queue item's name is "Artist - Title", and the artist is already on the line below it.</summary>
    [JsonIgnore]
    public string Title => MediaItem?.Name is { Length: > 0 } title ? title : Name;

    /// <summary>Second line under the title, taken from the media item.</summary>
    [JsonIgnore]
    public string SubtitleText => MediaItem?.SubtitleText ?? "";

    /// <summary>Duration formatted as m:ss or h:mm:ss, or an empty string when unknown.</summary>
    [JsonIgnore]
    public string DurationText => Duration is > 0 ? Format.Duration(Duration.Value) : "";

    /// <summary>Display URL of the entry's thumbnail at 160 pixels, or null when it has no image.</summary>
    [JsonIgnore]
    public string? ThumbUrl => Images.Resolver(FindImage(), 160);

    /// <summary>Best image for the entry: its own artwork, otherwise the media item's.</summary>
    /// <returns>The image to show, or null when there is none.</returns>
    public MediaImage? FindImage() => Image ?? MediaItem?.FindImage();
}

/// <summary>The play queue of a player (or group), with its settings and playback position.</summary>
public sealed class PlayerQueue
{
    /// <summary>Id of the queue; usually the id of the player it belongs to.</summary>
    public string     QueueId                { get; set; } = "";

    /// <summary>True when the queue is the player's active source.</summary>
    public bool       Active                 { get; set; }

    /// <summary>Display name of the queue, normally the player's name.</summary>
    public string     DisplayName            { get; set; } = "";

    /// <summary>True while the queue's player is available.</summary>
    public bool       Available              { get; set; }

    /// <summary>Number of items in the queue.</summary>
    public int        Items                  { get; set; }

    /// <summary>True when shuffle is on.</summary>
    public bool       ShuffleEnabled         { get; set; }

    /// <summary>True when the server keeps adding similar tracks once the queue runs out.</summary>
    public bool       AutoplayEnabled        { get; set; }

    /// <summary>True when tracks crossfade into each other.</summary>
    public bool       CrossfadeEnabled       { get; set; }

    /// <summary>Repeat mode: off, one or all.</summary>
    public string     RepeatMode             { get; set; } = "off";

    /// <summary>Index of the current item in the queue, or null when nothing is loaded.</summary>
    public int?       CurrentIndex           { get; set; }

    /// <summary>Index of the last item already buffered into the player's stream; items up to it can't be moved or removed.</summary>
    public int?       IndexInBuffer          { get; set; }

    /// <summary>Seconds played into the current item at the last report.</summary>
    public double     ElapsedTime            { get; set; }

    /// <summary>Unix time in seconds when ElapsedTime was reported, stamped with this PC's clock on arrival.</summary>
    public double     ElapsedTimeLastUpdated { get; set; }

    /// <summary>Playback state of the queue: idle, paused or playing.</summary>
    public string     State                  { get; set; } = "idle";

    /// <summary>The item playing now, or null.</summary>
    public QueueItem? CurrentItem            { get; set; }

    /// <summary>The item that plays next, or null.</summary>
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

/// <summary>An error reported by the server for a command, or a client-side connection failure (code 0).</summary>
/// <param name="code">The server's error code, or 0 for client-side failures such as timeouts and lost connections.</param>
/// <param name="message">The server's error details, or a description of the client-side failure.</param>
public sealed class ApiException(int code, string message) : Exception(message)
{
    /// <summary>The server's error code, or 0 for client-side failures.</summary>
    public int Code { get; } = code;

    // Error codes from music_assistant_models/errors.py.

    /// <summary>The command needs an authenticated connection.</summary>
    public const int AuthenticationRequired = 20;

    /// <summary>The credentials were rejected.</summary>
    public const int AuthenticationFailed   = 21;

    /// <summary>The signed-in user may not run the command.</summary>
    public const int InsufficientPermissions = 22;

    /// <summary>The access token is invalid or expired.</summary>
    public const int InvalidToken           = 23;

    /// <summary>The server has not finished its first-run setup yet.</summary>
    public const int SetupRequired          = 503;

    /// <summary>True when the error means the user has to sign in again.</summary>
    [JsonIgnore]
    public bool IsAuthError => Code is AuthenticationRequired or AuthenticationFailed or InvalidToken;
}

/// <summary>Resolves a MediaImage into a display URL. Set by the app once a client exists.</summary>
public static class Images
{
    /// <summary>Turns an image and a pixel size into a display URL; returns null until the app sets it.</summary>
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
    /// <summary>Format a length as m:ss, or h:mm:ss from one hour up.</summary>
    /// <param name="seconds">The length in seconds; negative values count as zero.</param>
    /// <returns>The formatted duration.</returns>
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
