using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MusicAssistant.Sendspin;

/// <summary>
/// Sendspin over the Music Assistant server's authenticated /sendspin WebSocket
/// proxy (local network). The first message is {"type":"auth","token","client_id"};
/// the server answers {"type":"auth_ok"} and from then on relays frames to its
/// internal Sendspin server. The client_id lets the server tie this player to
/// the signed-in user's session.
/// </summary>
public sealed class ProxyWebSocket : ISendspinSocket
{
    private readonly Uri    uri;
    private readonly string token;
    private readonly string clientId;
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private bool closedRaised;

    public event Action<string>? TextReceived;
    public event Action<byte[]>? BinaryReceived;
    public event Action<string>? Closed;

    public ProxyWebSocket(string baseUrl, string token, string clientId)
    {
        var ws = baseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        uri = new Uri(ws + "/sendspin");
        this.token    = token;
        this.clientId = clientId;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public async Task OpenAsync(CancellationToken ct)
    {
        await socket.ConnectAsync(uri, ct);

        var auth = JsonSerializer.Serialize(new { type = "auth", token, client_id = clientId });
        await socket.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, ct);

        // First reply is auth_ok (or a close with a reason)
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Speaker proxy refused the session");
        using var reply = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        if (reply.RootElement.TryGetProperty("type", out var type) && type.GetString() != "auth_ok")
        {
            throw new InvalidOperationException("Speaker proxy authentication failed");
        }

        _ = Task.Run(() => ReadLoopAsync(lifetime.Token));
    }

    public void SendText(string text)     => _ = SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);
    public void SendBinary(byte[] data)   => _ = SendAsync(data, WebSocketMessageType.Binary);

    private async Task SendAsync(byte[] data, WebSocketMessageType type)
    {
        if (socket.State != WebSocketState.Open) return;
        await sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open) await socket.SendAsync(data, type, true, lifetime.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            RaiseClosed("Speaker connection lost");
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) { RaiseClosed("Speaker connection closed by server"); return; }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var bytes = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Text) TextReceived?.Invoke(Encoding.UTF8.GetString(bytes));
                else BinaryReceived?.Invoke(bytes);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            RaiseClosed(ct.IsCancellationRequested ? "Closed" : "Speaker connection lost");
        }
    }

    public void Close()
    {
        lifetime.Cancel();
        try { socket.Abort(); } catch (Exception) { }
        RaiseClosed("Closed");
    }

    private void RaiseClosed(string reason)
    {
        if (closedRaised) return;
        closedRaised = true;
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        Close();
        socket.Dispose();
        lifetime.Dispose();
    }
}
