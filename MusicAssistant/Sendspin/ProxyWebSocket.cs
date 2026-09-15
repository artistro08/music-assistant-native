using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MusicAssistant.Sendspin;

/// <summary>
/// Sendspin over the Music Assistant server's authenticated /sendspin WebSocket proxy (local network), used only when
/// the server can't reach this PC's own listener. The first message is {"type":"auth","token","client_id"}; the server
/// answers {"type":"auth_ok"} and from then on relays frames to its internal Sendspin server.
/// </summary>
public sealed class ProxyWebSocket : WebSocketWire
{
    private readonly Uri    uri;
    private readonly string token;
    private readonly string clientId;
    private readonly ClientWebSocket socket = new();

    /// <summary>Prepares a socket to the server's /sendspin proxy; nothing connects until <see cref="OpenAsync"/>.</summary>
    /// <param name="baseUrl">The Music Assistant server's http or https base URL.</param>
    /// <param name="token">The signed-in user's access token, sent in the auth message.</param>
    /// <param name="clientId">This player's Sendspin client_id.</param>
    public ProxyWebSocket(string baseUrl, string token, string clientId)
    {
        string ws = baseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        uri = new Uri($"{ws}/sendspin");
        this.token    = token;
        this.clientId = clientId;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    /// <inheritdoc/>
    protected override WebSocket Socket => socket;

    /// <summary>Connects, authenticates with the proxy and starts the read loop.</summary>
    /// <param name="ct">Cancels the connect and authentication.</param>
    /// <returns>A task that completes once the proxy accepted the session.</returns>
    /// <exception cref="InvalidOperationException">The proxy refused the session or the authentication.</exception>
    public override async Task OpenAsync(CancellationToken ct)
    {
        await socket.ConnectAsync(uri, ct);

        string auth = JsonSerializer.Serialize(new { type = "auth", token, client_id = clientId });
        await socket.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, ct);

        // First reply is auth_ok (or a close with a reason).
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType != WebSocketMessageType.Text)
        {
            throw new InvalidOperationException("Speaker proxy refused the session");
        }

        using var reply = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        if (reply.RootElement.TryGetProperty("type", out JsonElement type) && (type.GetString() != "auth_ok"))
        {
            throw new InvalidOperationException("Speaker proxy authentication failed");
        }

        StartReading();
    }
}
