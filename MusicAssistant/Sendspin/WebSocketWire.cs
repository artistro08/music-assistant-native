using System.Net.WebSockets;
using System.Text;

namespace MusicAssistant.Sendspin;

/// <summary>
/// The shared half of a Sendspin wire over a WebSocket: queued sends, the read loop that reassembles messages, and a
/// single Closed notification. Subclasses supply the socket and how it opens.
/// </summary>
public abstract class WebSocketWire : ISendspinSocket
{
    /// <summary>Hard cap on one inbound WebSocket message.</summary>
    private const int MaxMessage = 128 * 1024;

    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private bool closedRaised;

    /// <inheritdoc/>
    public event Action<string>? TextReceived;

    /// <inheritdoc/>
    public event Action<byte[]>? BinaryReceived;

    /// <inheritdoc/>
    public event Action<string>? Closed;

    /// <summary>The underlying WebSocket.</summary>
    protected abstract WebSocket Socket { get; }

    /// <summary>Cancelled when the wire closes; subclasses tie their own waits to it.</summary>
    protected CancellationToken Lifetime => lifetime.Token;

    /// <inheritdoc/>
    public abstract Task OpenAsync(CancellationToken ct);

    /// <inheritdoc/>
    public void SendText(string text) => _ = SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

    /// <inheritdoc/>
    public void SendBinary(byte[] data) => _ = SendAsync(data, WebSocketMessageType.Binary);

    /// <summary>Starts the background read loop; call once the socket is open.</summary>
    protected void StartReading() => _ = Task.Run(() => ReadLoopAsync(lifetime.Token));

    private async Task SendAsync(byte[] data, WebSocketMessageType type)
    {
        if (Socket.State != WebSocketState.Open)
        {
            return;
        }

        await sendLock.WaitAsync();
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                await Socket.SendAsync(data, type, true, lifetime.Token);
            }
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
        byte[] buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && (Socket.State == WebSocketState.Open))
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await Socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        RaiseClosed("Speaker connection closed by server");
                        return;
                    }

                    // A Noise transport frame is at most 65535 bytes and control messages are small; cap well above
                    // that so no single message drives unbounded allocation.
                    if ((message.Length + result.Count) > MaxMessage)
                    {
                        RaiseClosed("Speaker message too large");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                byte[] bytes = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    TextReceived?.Invoke(Encoding.UTF8.GetString(bytes));
                }
                else
                {
                    BinaryReceived?.Invoke(bytes);
                }
            }
        }
        catch (OperationCanceledException)
        {
            RaiseClosed("Closed");
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Any other escape (socket fault, or an unexpected error from a receive callback) must fail the connection
            // so Speaker notices, rather than the read loop dying silently and hanging the session.
            App.Log($"Speaker read loop stopped: {ex.Message}");
            RaiseClosed("Speaker connection lost");
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        lifetime.Cancel();
        try
        {
            Socket.Abort();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Best-effort: the socket may already be gone.
        }
        RaiseClosed("Closed");
    }

    private void RaiseClosed(string reason)
    {
        if (closedRaised)
        {
            return;
        }

        closedRaised = true;
        Closed?.Invoke(reason);
    }

    /// <summary>Closes the socket and releases it.</summary>
    public void Dispose()
    {
        Close();
        Socket.Dispose();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
