using System.Net.WebSockets;
using System.Text;

namespace MusicAssistant.Api;

/// <summary>Local-network transport: the server's /ws websocket.</summary>
public sealed class WebSocketTransport : IMassTransport
{
    private const int ReceiveBufferSize = 64 * 1024;

    private readonly Uri  wsUrl;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private ClientWebSocket?         socket;
    private CancellationTokenSource? readCts;

    /// <inheritdoc/>
    public string HttpBaseUrl { get; }

    /// <inheritdoc/>
    public bool   IsRemote    => false;

    /// <inheritdoc/>
    public event Action<string>?     MessageReceived;

    /// <inheritdoc/>
    public event Action<Exception?>? Closed;

    /// <summary>Accepts http(s):// or ws(s):// addresses, with or without a trailing /ws.</summary>
    /// <param name="serverAddress">The server address as the user typed it.</param>
    public WebSocketTransport(string serverAddress)
    {
        (HttpBaseUrl, wsUrl) = NormalizeAddress(serverAddress);
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken ct)
    {
        socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(wsUrl, ct);

        readCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(readCts.Token));
    }

    /// <inheritdoc/>
    public async Task SendAsync(string message)
    {
        if (socket is not { State: WebSocketState.Open }) throw new ApiException(0, "Not connected");
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task CloseAsync()
    {
        readCts?.Cancel();
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                // Best-effort goodbye; the socket is disposed below either way.
            }
        }
        socket?.Dispose();
        socket = null;
    }

    /// <summary>Start closing the socket without waiting for it to finish.</summary>
    public void Dispose() => _ = CloseAsync();

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested && socket is { State: WebSocketState.Open })
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Server closed the connection");
                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                MessageReceived?.Invoke(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
            }
        }
        catch (OperationCanceledException)
        {
            // Closed on purpose; CloseAsync cancelled the read.
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            error = ex;
        }

        if (!ct.IsCancellationRequested) Closed?.Invoke(error);
    }

    private static (string httpBase, Uri wsUrl) NormalizeAddress(string address)
    {
        address = address.Trim().TrimEnd('/');
        if (!address.Contains("://")) address = $"http://{address}";

        var uri = new Uri(address, UriKind.Absolute);
        (string httpScheme, string wsScheme) = uri.Scheme switch
        {
            "http" or "ws"   => ("http",  "ws"),
            "https" or "wss" => ("https", "wss"),
            _ => throw new ArgumentException("Server address must use http, https, ws or wss."),
        };

        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/ws", StringComparison.Ordinal)) path = path[..^3];

        return ($"{httpScheme}://{uri.Authority}{path}", new Uri($"{wsScheme}://{uri.Authority}{path}/ws"));
    }
}
