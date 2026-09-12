using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace MusicAssistant.Remote;

/// <summary>
/// Hosts the WebRTC bridge page in a hidden WebView2 and exchanges JSON
/// messages with it.
///
/// WebView2 ships with the Windows App SDK, so remote access needs no extra
/// package and uses the same Chromium WebRTC stack as the Music Assistant web
/// app. The page is local, loaded from a virtual host mapped to the app's
/// Assets folder, with a strict CSP; navigation anywhere else is blocked.
/// </summary>
public sealed class RemoteBridge
{
    public const string SignalingUrl = "wss://signaling.music-assistant.io/ws";

    private const string VirtualHost = "bridge.musicassistant.local";
    private const string PageUrl     = $"http://{VirtualHost}/bridge.html";   // http so ws:// to the LAN server is not mixed content; the origin is marked secure below

    private readonly WebView2 view;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HttpReply>> httpWaiting = new();
    private Task? initialization;

    public record HttpReply(int Status, Dictionary<string, string> Headers, byte[] Body);

    public event Action<string>? MessageReceived;   // API message from the server
    public event Action?         Opened;
    public event Action<string>? ClosedWithReason;
    public event Action<string>? Errored;

    public record PlayerStatus(bool Running, bool Connected, bool Playing, double Volume, bool Muted, string? ClientId);
    public event Action<PlayerStatus>? PlayerState;
    public event Action<string?>?      PlayerPairing;
    public event Action<string>?       PlayerError;

    public RemoteBridge(WebView2 view)
    {
        this.view = view;
    }

    /// <summary>Create the WebView2, lock it down and load the bridge page. Safe to call repeatedly.</summary>
    public Task InitializeAsync() => initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicAssistant", "WebView2");
        var options  = new CoreWebView2EnvironmentOptions
        {
            // Hidden pages get their timers throttled; keep signaling and reconnects responsive.
            // HardwareMediaKeyHandling off: otherwise Chromium registers its own media session for the
            // speaker's audio element and grabs Play/Pause keys, pausing the PC locally while the server plays on.
            AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding --autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling " + $"--unsafely-treat-insecure-origin-as-secure=http://{VirtualHost}",
        };
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
        await view.EnsureCoreWebView2Async(environment);

        var core = view.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled             = false;
        settings.AreDefaultContextMenusEnabled  = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed          = false;
        settings.IsStatusBarEnabled             = false;
        settings.IsZoomControlEnabled           = false;
        settings.IsGeneralAutofillEnabled       = false;
        settings.IsPasswordAutosaveEnabled      = false;
        settings.IsWebMessageEnabled            = true;

        core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "Assets", "bridge"), CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting  += (_, e) => { if (!e.Uri.StartsWith(PageUrl, StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.NewWindowRequested  += (_, e) => e.Handled = true;
        core.WebMessageReceived  += OnWebMessage;

        core.Navigate(PageUrl);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using (timeout.Token.Register(() => ready.TrySetException(new TimeoutException("Remote bridge did not start"))))
        {
            await ready.Task;
        }
    }

    // Commands to the page

    public void Connect(string remoteId)  => Post(new { type = "connect", remoteId, signalingUrl = SignalingUrl });
    public void Send(string data)         => Post(new { type = "send", data });
    public void Disconnect()              => Post(new { type = "disconnect" });

    // Speaker (Sendspin player) commands
    public void StartPlayer(object config)     => Post(new { type = "player-start", baseUrl = Prop(config, "baseUrl"), remote = Prop(config, "remote"), token = Prop(config, "token"), name = Prop(config, "name"), clientId = Prop(config, "clientId") });
    public void StopPlayer()                   => Post(new { type = "player-stop", reason = "user_request" });
    public void SetPlayerVolume(double volume, bool muted) => Post(new { type = "player-volume", volume, muted });

    private static object? Prop(object source, string name) => source.GetType().GetProperty(name)?.GetValue(source);

    /// <summary>Fetch an HTTP resource from the server through the data channel.</summary>
    public async Task<HttpReply> HttpAsync(string method, string path, IDictionary<string, string>? headers, CancellationToken ct)
    {
        var id  = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<HttpReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        httpWaiting[id] = tcs;
        Post(new { type = "http", id, method, path, headers = headers ?? new Dictionary<string, string>() });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using (timeout.Token.Register(() => { httpWaiting.TryRemove(id, out _); tcs.TrySetCanceled(); }))
        {
            return await tcs.Task;
        }
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message);
        view.DispatcherQueue.TryEnqueue(() => { try { view.CoreWebView2?.PostWebMessageAsJson(json); } catch (Exception ex) { App.Log("Bridge post: " + ex.Message); } });
    }

    // Messages from the page

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); }
        catch (JsonException) { return; }

        var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "ready":   ready.TrySetResult(); break;
            case "open":    Opened?.Invoke(); break;
            case "message": MessageReceived?.Invoke(message.GetProperty("data").GetString() ?? ""); break;
            case "close":   ClosedWithReason?.Invoke(message.TryGetProperty("reason", out var r) ? r.GetString() ?? "Closed" : "Closed"); break;
            case "error":   Errored?.Invoke(message.TryGetProperty("message", out var m) ? m.GetString() ?? "Error" : "Error"); break;
            case "log":     App.Log("Remote bridge: " + (message.TryGetProperty("text", out var l) ? l.GetString() : "")); break;
            case "player-state":
                PlayerState?.Invoke(new PlayerStatus(
                    Bool(message, "running"), Bool(message, "connected"), Bool(message, "playing"),
                    message.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 1,
                    Bool(message, "muted"),
                    message.TryGetProperty("clientId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null));
                break;
            case "player-pairing": PlayerPairing?.Invoke(message.TryGetProperty("token", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() : null); break;
            case "player-error":   PlayerError?.Invoke(message.TryGetProperty("message", out var pe) ? pe.GetString() ?? "Speaker error" : "Speaker error"); break;
            case "http-response":
                var id = message.GetProperty("id").GetString() ?? "";
                if (httpWaiting.TryRemove(id, out var waiter))
                {
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (message.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                        foreach (var p in h.EnumerateObject()) headers[p.Name] = p.Value.ToString();
                    var body   = message.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? Convert.FromBase64String(b.GetString()!) : [];
                    var status = message.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
                    waiter.TrySetResult(new HttpReply(status, headers, body));
                }
                break;
        }
    }
}
