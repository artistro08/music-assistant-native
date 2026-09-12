using MusicAssistant.Api;

namespace MusicAssistant.Remote;

/// <summary>
/// Remote transport: the "ma-api" WebRTC data channel, driven by the bridge
/// page. Images resolve through a loopback proxy that tunnels HTTP requests
/// over the same connection.
/// </summary>
public sealed class WebRtcTransport : IMassTransport
{
    private readonly RemoteBridge bridge;
    private readonly string       remoteId;
    private readonly LocalImageProxy proxy;
    private TaskCompletionSource? opened;
    private bool connected;

    public string HttpBaseUrl => proxy.BaseUrl;
    public bool   IsRemote    => true;

    public event Action<string>?     MessageReceived;
    public event Action<Exception?>? Closed;

    public WebRtcTransport(RemoteBridge bridge, string remoteId)
    {
        this.bridge   = bridge;
        this.remoteId = remoteId;
        proxy = new LocalImageProxy(bridge);

        bridge.MessageReceived  += OnMessage;
        bridge.Opened           += OnOpened;
        bridge.ClosedWithReason += OnClosed;
        bridge.Errored          += OnError;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await bridge.InitializeAsync();
        opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.Connect(remoteId);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using (timeout.Token.Register(() => opened.TrySetException(new ApiException(0, "Remote connection timed out"))))
        {
            await opened.Task;
        }
        connected = true;
        proxy.Start();
    }

    public Task SendAsync(string message)
    {
        if (!connected) throw new ApiException(0, "Not connected");
        bridge.Send(message);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        connected = false;
        proxy.Stop();
        bridge.Disconnect();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        bridge.MessageReceived  -= OnMessage;
        bridge.Opened           -= OnOpened;
        bridge.ClosedWithReason -= OnClosed;
        bridge.Errored          -= OnError;
        proxy.Dispose();
    }

    private void OnMessage(string data) => MessageReceived?.Invoke(data);
    private void OnOpened()             => opened?.TrySetResult();

    private void OnClosed(string reason)
    {
        var error = new ApiException(0, reason);
        if (opened is { Task.IsCompleted: false } pending) { pending.TrySetException(error); return; }
        if (!connected) return;
        connected = false;
        proxy.Stop();
        Closed?.Invoke(error);
    }

    private void OnError(string message) => App.Log("Remote transport: " + message);
}
