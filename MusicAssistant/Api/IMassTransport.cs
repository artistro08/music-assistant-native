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
    /// <param name="ct">Cancels the connection attempt.</param>
    /// <returns>A task that completes when the pipe is open.</returns>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Send one whole JSON text message to the server.</summary>
    /// <param name="message">The serialized command to send.</param>
    /// <returns>A task that completes when the message was handed to the pipe.</returns>
    Task SendAsync(string message);

    /// <summary>Raised for every whole message from the server. May run on any thread.</summary>
    event Action<string>? MessageReceived;

    /// <summary>Raised once when the pipe closes for any reason after a successful connect.</summary>
    event Action<Exception?>? Closed;

    /// <summary>Close the pipe and stop reading from it.</summary>
    /// <returns>A task that completes when the pipe is closed.</returns>
    Task CloseAsync();

    /// <summary>Base URL that images and other HTTP resources of this server are reachable at from this machine.</summary>
    string HttpBaseUrl { get; }

    /// <summary>True when traffic goes through the remote (WebRTC) path rather than the local network.</summary>
    bool IsRemote { get; }
}
