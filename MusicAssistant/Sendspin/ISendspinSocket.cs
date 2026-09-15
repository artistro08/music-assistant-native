namespace MusicAssistant.Sendspin;

/// <summary>
/// The wire under a Sendspin connection: text frames for the cleartext
/// handshake, binary frames once encrypted. Locally this is a WebSocket to the
/// server's authenticated /sendspin proxy; remotely it is a data channel on the
/// WebRTC connection. Callbacks arrive on arbitrary threads.
/// </summary>
public interface ISendspinSocket : IDisposable
{
    /// <summary>Raised for each complete text frame, used for the cleartext handshake.</summary>
    event Action<string>? TextReceived;

    /// <summary>Raised for each complete binary frame, used once the session is encrypted.</summary>
    event Action<byte[]>? BinaryReceived;

    /// <summary>Raised when the wire closes, with the reason.</summary>
    event Action<string>? Closed;

    /// <summary>Opens the wire and returns once frames can be sent.</summary>
    /// <param name="ct">Cancels the open attempt.</param>
    /// <returns>A task that completes when the wire is open.</returns>
    Task OpenAsync(CancellationToken ct);

    /// <summary>Queues a text frame for sending.</summary>
    /// <param name="text">The frame text.</param>
    void SendText(string text);

    /// <summary>Queues a binary frame for sending.</summary>
    /// <param name="data">The frame bytes.</param>
    void SendBinary(byte[] data);

    /// <summary>Closes the wire; no further frames are sent or received.</summary>
    void Close();
}
