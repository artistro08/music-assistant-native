namespace MusicAssistant.Sendspin;

/// <summary>
/// The wire under a Sendspin connection: text frames for the cleartext
/// handshake, binary frames once encrypted. Locally this is a WebSocket to the
/// server's authenticated /sendspin proxy; remotely it is a data channel on the
/// WebRTC connection. Callbacks arrive on arbitrary threads.
/// </summary>
public interface ISendspinSocket : IDisposable
{
    event Action<string>? TextReceived;
    event Action<byte[]>? BinaryReceived;
    event Action<string>? Closed;

    Task OpenAsync(CancellationToken ct);
    void SendText(string text);
    void SendBinary(byte[] data);
    void Close();
}
