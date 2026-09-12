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

    public RemotePeer Peer        { get; }
    public string     HttpBaseUrl => proxy.BaseUrl;
    public bool       IsRemote    => true;

    public event Action<string>?     MessageReceived;
    public event Action<Exception?>? Closed;

    public WebRtcTransport(string remoteId)
    {
        Peer  = new RemotePeer(remoteId);
        proxy = new LocalImageProxy(Peer);
        Peer.ApiMessage += message => MessageReceived?.Invoke(message);
        Peer.Closed     += OnClosed;
    }

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

    public Task SendAsync(string message)
    {
        if (!connected) throw new ApiException(0, "Not connected");
        Peer.SendApi(message);
        return Task.CompletedTask;
    }

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

    public void Dispose()
    {
        proxy.Dispose();
        Peer.Dispose();
    }
}
