namespace MusicAssistant.Api;

/// <summary>
/// A bidirectional text message pipe to a Music Assistant server.
///
/// Local connections use a plain websocket; remote connections use a WebRTC
/// data channel. MassClient speaks the same JSON protocol over either.
/// </summary>
public interface IMassTransport : IDisposable
{
    /// <summary>Open the pipe. Completes once messages can flow.</summary>
    Task ConnectAsync(CancellationToken ct);

    Task SendAsync(string message);

    /// <summary>Raised for every whole message from the server. May run on any thread.</summary>
    event Action<string>? MessageReceived;

    /// <summary>Raised once when the pipe closes for any reason after a successful connect.</summary>
    event Action<Exception?>? Closed;

    Task CloseAsync();

    /// <summary>Base URL that images and other HTTP resources of this server are reachable at from this machine.</summary>
    string HttpBaseUrl { get; }

    /// <summary>True when traffic goes through the remote (WebRTC) path rather than the local network.</summary>
    bool IsRemote { get; }
}
