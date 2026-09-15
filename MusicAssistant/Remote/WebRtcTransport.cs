using MusicAssistant.Api;

namespace MusicAssistant.Remote;

/// <summary>
/// Remote transport: the "ma-api" data channel of a <see cref="RemotePeer"/>.
/// Images resolve through a loopback proxy that tunnels HTTP requests over the
/// same connection. The peer is exposed so the speaker can open its own
/// "sendspin" channel on it.
/// </summary>
public sealed class WebRtcTransport : IMassTransport
{
    private readonly LocalImageProxy proxy;
    private bool connected;

    /// <summary>The WebRTC connection this transport runs on; the speaker opens its "sendspin" channel on it.</summary>
    public RemotePeer Peer        { get; }

    /// <summary>Base URL of the loopback image proxy, used in place of the server's own HTTP address.</summary>
    public string     HttpBaseUrl => proxy.BaseUrl;

    /// <summary>Always true: this transport goes through the remote (WebRTC) path.</summary>
    public bool       IsRemote    => true;

    /// <summary>Raised for every whole API message from the server. Runs on SIPSorcery's receive thread.</summary>
    public event Action<string>?     MessageReceived;

    /// <summary>Raised once, with an <see cref="ApiException"/> carrying the reason, when the connection drops after a successful connect.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Creates the transport, its peer and its image proxy for the server with the given Remote ID.</summary>
    /// <param name="remoteId">The home server's Remote ID.</param>
    public WebRtcTransport(string remoteId)
    {
        Peer  = new RemotePeer(remoteId);
        proxy = new LocalImageProxy(Peer);
        Peer.ApiMessage += message => MessageReceived?.Invoke(message);
        Peer.Closed     += OnClosed;
    }

    /// <summary>Connects the peer, giving up after 45 seconds, then starts the image proxy.</summary>
    /// <param name="ct">Cancels the connection attempt.</param>
    /// <returns>A task that completes once API messages can flow.</returns>
    /// <exception cref="ApiException">The connection timed out.</exception>
    public async Task ConnectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await Peer.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(0, "Remote connection timed out");
        }
        connected = true;
        proxy.Start();
    }

    /// <summary>Sends one API message over the peer's API channel.</summary>
    /// <param name="message">The JSON command text to send.</param>
    /// <returns>An already completed task; the channel send is synchronous.</returns>
    /// <exception cref="ApiException">The transport is not connected.</exception>
    public Task SendAsync(string message)
    {
        if (!connected) throw new ApiException(0, "Not connected");
        Peer.SendApi(message);
        return Task.CompletedTask;
    }

    /// <summary>Stops the image proxy and closes the peer connection without raising <see cref="Closed"/>.</summary>
    /// <returns>An already completed task.</returns>
    public Task CloseAsync()
    {
        connected = false;
        proxy.Stop();
        Peer.Close();
        return Task.CompletedTask;
    }

    private void OnClosed(string reason)
    {
        if (!connected) return;
        connected = false;
        proxy.Stop();
        Closed?.Invoke(new ApiException(0, reason));
    }

    /// <summary>Disposes the image proxy and the peer connection.</summary>
    public void Dispose()
    {
        proxy.Dispose();
        Peer.Dispose();
    }
}
