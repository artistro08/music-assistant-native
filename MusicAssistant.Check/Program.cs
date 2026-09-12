using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MusicAssistant.Api;

// Fake Music Assistant server on localhost. Exercises: ServerInfo handshake,
// auth/login + auth, partial results, error results, live player events,
// and image URL building. Run with `dotnet run`; a failed assert exits non-zero.

const string Prefix = "http://127.0.0.1:18095/";
const string Token  = "test-token";

var listener = new HttpListener();
listener.Prefixes.Add(Prefix);
listener.Start();
var server = Task.Run(() => ServeAsync(listener));

var client = new MassClient();
var events = new List<string>();
client.EventReceived += (name, _, _) => events.Add(name);

// Handshake
var info = await client.ConnectAsync("127.0.0.1:18095");
Check(info.ServerId == "fake" && info.SchemaVersion == 31, "server info parsed");
Check(client.BaseUrl == "http://127.0.0.1:18095", "base url derived");

// Wrong password is rejected, right password yields token and authenticates
await Throws<ApiException>(() => client.LoginAsync("admin", "wrong"), "bad login rejected");
var token = await client.LoginAsync("admin", "secret");
Check(token == Token, "token returned");
Check(client.CurrentUser?.Username == "admin", "user set after auth");
Check(client.Players.ContainsKey("p1") && client.Queues.ContainsKey("p1"), "initial state fetched");

// Partial results are concatenated in order
var tracks = await client.GetLibraryItemsAsync("tracks", null, false, 10, 0);
Check(tracks.Count == 3 && tracks[2].Name == "Track 3" && tracks[0].ArtistsText == "Artist A", "partial results merged");
Check(tracks[0].SubtitleText == "Artist A • Album X", "track subtitle");

// Server errors surface as ApiException with code
var error = await Throws<ApiException>(() => client.SendAsync<JsonElement>("boom"), "error result");
Check(error!.Code == 2 && error.Message == "Not found", "error code and details");

// Events update in-memory state
await client.PlayerCommandAsync("p1", "play");
await Task.Delay(200);
Check(client.Players["p1"].PlaybackState == "playing", "player_updated applied");
Check(events.Contains("player_updated"), "event raised");

// Image URLs
Check(client.ImageUrl(new MediaImage { ProxyId = "abc", Path = "x", Provider = "spotify" }) == "http://127.0.0.1:18095/imageproxy/abc?size=256", "opaque proxy url");
Check(client.ImageUrl(new MediaImage { Path = "https://cdn/img.jpg", Provider = "spotify", RemotelyAccessible = true }) == "https://cdn/img.jpg", "remote https passthrough");
Check(client.ImageUrl(new MediaImage { Path = "http://cdn/img.jpg", Provider = "spotify", RemotelyAccessible = true })!.StartsWith("http://127.0.0.1:18095/imageproxy?path="), "plain http goes through proxy");

// OAuth loopback: a request with the right nonce and code resolves, others are ignored
using (var loopback = new OAuthLoopback())
{
    var http = new HttpClient();
    var wait = loopback.WaitForCodeAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
    var wrong = await http.GetAsync(loopback.ReturnUrl.Replace("/callback/", "/callback/x") + "?code=stolen");
    Check(wrong.StatusCode == HttpStatusCode.NotFound && !wait.IsCompleted, "loopback rejects wrong nonce");
    var right = await http.GetAsync(loopback.ReturnUrl + "?code=the-token");
    Check(right.IsSuccessStatusCode && await wait == "the-token", "loopback returns code");
}

await client.DisconnectAsync();
listener.Stop();
Console.WriteLine("All checks passed.");
return;

// -------------------------------------------------------------------------
// Helpers
// -------------------------------------------------------------------------

static void Check(bool condition, string what)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {what}");
    if (!condition) Environment.Exit(1);
}

static async Task<T?> Throws<T>(Func<Task> action, string what) where T : Exception
{
    try { await action(); }
    catch (T ex) { Check(true, what); return ex; }
    Check(false, what);
    return null;
}

static async Task ServeAsync(HttpListener listener)
{
    var context = await listener.GetContextAsync();
    var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
    var buffer = new byte[64 * 1024];
    var authenticated = false;

    await Send(ws, new { server_id = "fake", server_version = "2.7.0", schema_version = 31, base_url = "http://127.0.0.1:18095", onboard_done = true });

    while (ws.State == WebSocketState.Open)
    {
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close) break;

        var msg  = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement;
        var id   = msg.GetProperty("message_id").GetString();
        var cmd  = msg.GetProperty("command").GetString();
        var args = msg.TryGetProperty("args", out var a) ? a : default;

        switch (cmd)
        {
            case "auth/login":
                var ok = args.GetProperty("username").GetString() == "admin" && args.GetProperty("password").GetString() == "secret";
                await Send(ws, ok
                    ? new { message_id = id, result = new { success = true, access_token = Token, user = new { user_id = "u1", username = "admin", role = "admin" } } }
                    : new { message_id = id, result = new { success = false, error = "Invalid credentials" } });
                break;

            case "auth":
                authenticated = args.GetProperty("token").GetString() == Token;
                if (authenticated) await Send(ws, new { message_id = id, result = new { authenticated = true, user = new { user_id = "u1", username = "admin", role = "admin" } } });
                else await Send(ws, new { message_id = id, error_code = 23, details = "Invalid or expired token" });
                break;

            case "players/all":
                await Send(ws, new { message_id = id, result = new[] { new { player_id = "p1", name = "Kitchen", available = true, enabled = true, playback_state = "idle", supported_features = new[] { "volume_set" } } } });
                break;

            case "player_queues/all":
                await Send(ws, new { message_id = id, result = new[] { new { queue_id = "p1", display_name = "Kitchen", state = "idle", items = 0, repeat_mode = "off" } } });
                break;

            case "music/tracks/library_items":
                await Send(ws, new { message_id = id, partial = true, result = new[] { Track(1), Track(2) } });
                await Send(ws, new { message_id = id, result = new[] { Track(3) } });
                break;

            case "players/cmd/play":
                await Send(ws, new { message_id = id, result = (object?)null });
                await Send(ws, new { @event = "player_updated", object_id = "p1", data = new { player_id = "p1", name = "Kitchen", available = true, enabled = true, playback_state = "playing" } });
                break;

            case "auth/logout":
                await Send(ws, new { message_id = id, result = (object?)null });
                break;

            default:
                await Send(ws, new { message_id = id, error_code = 2, details = "Not found" });
                break;
        }
    }
}

static object Track(int n) => new
{
    item_id    = n.ToString(),
    provider   = "library",
    name       = $"Track {n}",
    uri        = $"library://track/{n}",
    media_type = "track",
    duration   = 200 + n,
    artists    = new[] { new { item_id = "a1", provider = "library", name = "Artist A", uri = "library://artist/a1", media_type = "artist" } },
    album      = new { item_id = "x1", provider = "library", name = "Album X", uri = "library://album/x1", media_type = "album" },
};

static Task Send(WebSocket ws, object payload)
    => ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);
