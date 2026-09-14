using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MusicAssistant.Api;
using MusicAssistant.Sendspin;

// Fake Music Assistant server on localhost. Exercises: ServerInfo handshake,
// auth/login + auth, partial results, error results, live player events,
// and image URL building. Run with `dotnet run`; a failed assert exits non-zero.

const string Prefix = "http://127.0.0.1:18095/";
const string Token  = "test-token";

// `dotnet run -- --peak` prints the output device's peak level over 3 seconds (live playback checks)
if (args.Length > 0 && args[0] == "--peak")
{
    var peak = MusicAssistant.Check.PeakMeter.Sample(TimeSpan.FromSeconds(3));
    Console.WriteLine($"peak={peak:0.000}");
    return;
}

// Remote ID pinning (same vectors as the old bridge self-test)
var sixteen = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
Check(RemoteId.Decode("AAAQEAYEAUDAOCAJBIFQYDIOB4").SequenceEqual(sixteen), "remote id decodes to 16 bytes");
Check(RemoteId.Decode("AAAQEAYEAUDAOCAJBIFQYDIOB4".Replace('2', '9')).SequenceEqual(sixteen), "nines are read as twos");
var goodFingerprint = string.Join(":", Enumerable.Range(0, 32).Select(i => i.ToString("X2")));
var badFingerprint  = "FF:" + goodFingerprint[3..];
var sdpGood   = $"v=0\r\na=fingerprint:sha-256 {goodFingerprint}\r\na=setup:active\r\n";
var sdpBad    = $"v=0\r\na=fingerprint:sha-256 {badFingerprint}\r\n";
var sdpMixed  = $"v=0\r\na=fingerprint:sha-256 {goodFingerprint}\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 {badFingerprint}\r\n";
var sdpWeak   = $"v=0\r\na=fingerprint:sha-1 AA:BB\r\na=fingerprint:sha-256 {goodFingerprint}\r\n";
var sdpOnlyWeak = "v=0\r\na=fingerprint:sha-1 AA:BB\r\n";
Check(RemoteId.VerifyAndSanitizeSdp(sdpGood, "AAAQEAYEAUDAOCAJBIFQYDIOB4") == sdpGood, "matching fingerprint accepted");
Check(Fails(() => RemoteId.VerifyAndSanitizeSdp(sdpBad, "AAAQEAYEAUDAOCAJBIFQYDIOB4")), "wrong fingerprint rejected");
Check(Fails(() => RemoteId.VerifyAndSanitizeSdp(sdpMixed, "AAAQEAYEAUDAOCAJBIFQYDIOB4")), "session good + media bad rejected");
Check(!RemoteId.VerifyAndSanitizeSdp(sdpWeak, "AAAQEAYEAUDAOCAJBIFQYDIOB4").Contains("sha-1"), "weaker algorithms stripped");
Check(Fails(() => RemoteId.VerifyAndSanitizeSdp(sdpOnlyWeak, "AAAQEAYEAUDAOCAJBIFQYDIOB4")), "only weaker algorithms rejected");
Check(Fails(() => RemoteId.VerifyAndSanitizeSdp("", "AAAQEAYEAUDAOCAJBIFQYDIOB4")), "empty sdp rejected");
Check(Fails(() => RemoteId.Decode("AAAQEAYEAUDAOCAJBIFQYDIOB!")), "bad remote id rejected");

// Sendspin building blocks
Check(Base64Url.Decode(Base64Url.Encode(sixteen)).SequenceEqual(sixteen) && !Base64Url.Encode(new byte[] { 0xFB, 0xFF }).Contains('+'), "base64url round trip");
Check(Base64Url.Encode(NoiseCrypto.Hash("sendspin-psk-id-v1"u8, Identity.SentinelPsk)) == "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo", "sentinel psk_id matches the specification");
{
    // KKpsk2 round trip: a fake server (initiator) and this client (responder) end up with matching transport keys
    var (serverPrivate, serverPublic) = NoiseCrypto.GenerateKeyPair();
    var (clientPrivate, clientPublic) = NoiseCrypto.GenerateKeyPair();
    var psk      = NoiseCrypto.Hash("test psk"u8);
    var prologue = "client-init+server-init"u8.ToArray();
    var noiseServer = new HandshakeState(initiator: true,  prologue, serverPrivate, serverPublic, clientPublic, psk);
    var noiseClient = new HandshakeState(initiator: false, prologue, clientPrivate, clientPublic, serverPublic);
    var message1 = noiseServer.WriteMessage1("{\"psk_id\":\"x\",\"psk_category\":\"sn\"}"u8);
    var payload1 = noiseClient.ReadMessage1(message1);
    noiseClient.SetPsk(psk);
    var message2 = noiseServer.ReadMessage2(noiseClient.WriteMessage2("{}"u8));
    var serverSession = noiseServer.Split();
    var clientSession = noiseClient.Split();
    var roundTrip = clientSession.Decrypt(serverSession.Encrypt("hello from the server"u8));
    var backTrip  = serverSession.Decrypt(clientSession.Encrypt("hello from the client"u8));
    Check(Encoding.UTF8.GetString(payload1).Contains("psk_id") && Encoding.UTF8.GetString(message2) == "{}", "noise handshake payloads");
    Check(Encoding.UTF8.GetString(roundTrip) == "hello from the server" && Encoding.UTF8.GetString(backTrip) == "hello from the client", "noise transport both directions");
    Check(noiseServer.HandshakeHash.SequenceEqual(noiseClient.HandshakeHash), "handshake hash agrees on both sides");
    var wrongPsk = new HandshakeState(initiator: false, prologue, clientPrivate, clientPublic, serverPublic);
    var server2  = new HandshakeState(initiator: true, prologue, serverPrivate, serverPublic, clientPublic, psk);
    wrongPsk.ReadMessage1(server2.WriteMessage1("{}"u8));
    wrongPsk.SetPsk(NoiseCrypto.Hash("another psk"u8));
    Check(Fails(() => server2.ReadMessage2(wrongPsk.WriteMessage2("{}"u8))), "wrong psk fails the handshake");
}
{
    // Time filter: a constant 5 second offset with 40 ppm drift is recovered from noisy measurements
    var filter = new TimeFilter(0, 1.1, 2.0);
    var random = new Random(7);
    for (var i = 0; i < 40; i++)
    {
        long t = 1_000_000L * (i + 1) * 10;
        var trueOffset = 5_000_000.0 + 40e-6 * t;
        filter.Update(trueOffset + random.Next(-400, 400), 1000, t);
    }
    var probe = 1_000_000L * 410;   // 10 s after the last update, the spacing of the real time-sync bursts
    var filterError = Math.Abs(filter.ComputeServerTime(probe) - (probe + 5_000_000.0 + 40e-6 * probe));
    Check(filter.IsSynchronized && filterError < 1000, $"time filter converges (error {filterError:0} us)");
    Check(Math.Abs(filter.ComputeClientTime(filter.ComputeServerTime(probe)) - probe) <= 1, "time filter inverse");
}

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
Check(!client.StateLoaded, "state not loaded before auth");
var token = await client.LoginAsync("admin", "secret");
Check(token == Token, "token returned");
Check(client.CurrentUser?.Username == "admin", "user set after auth");
Check(client.Players.ContainsKey("p1") && client.Queues.ContainsKey("p1"), "initial state fetched");
Check(client.StateLoaded, "state loaded after auth");

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
Check(!client.StateLoaded, "state flag cleared on disconnect");
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

static bool Fails(Action action)
{
    try { action(); return false; }
    catch (Exception) { return true; }
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
