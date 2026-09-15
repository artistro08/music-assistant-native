using System.Text;
using System.Text.Json;

namespace MusicAssistant.Sendspin;

/// <summary>
/// One Sendspin session over a socket: the cleartext init exchange, the Noise
/// handshake (server initiates, we respond), then encrypted transport with
/// fragment reassembly and in-band re-handshakes. Hands decrypted control
/// messages and binary frames up; knows nothing about roles.
///
/// Wire details follow aiosendspin (what Music Assistant runs): fragments use
/// type 2 (more) and 3 (last) with the original type in the first frame, and
/// a transport plaintext is at most 65519 bytes.
/// </summary>
/// <remarks>
/// @link https://github.com/Sendspin/spec/blob/main/messaging.md#communication
/// @link https://github.com/Sendspin/aiosendspin/blob/main/aiosendspin/noise/wire.py
/// </remarks>
public sealed class SendspinConnection : IDisposable
{
    private const int  MaxTransportPlaintext = 65535 - 16;

    /// <summary>A Noise transport message is at most 65535 bytes; reject larger before allocating.</summary>
    private const int  MaxTransportFrame     = 65535;
    private const int  MaxReassembly         = 4 * 1024 * 1024;
    private const byte TypeJson              = 0;
    private const byte TypeFragmentMore      = 2;
    private const byte TypeFragmentEnd       = 3;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What a completed handshake agreed on.</summary>
    /// <param name="ServerId">The server's server_id from server/init.</param>
    /// <param name="Matched">The pre-shared key the handshake used.</param>
    /// <param name="IsRehandshake">Whether this was an in-band re-handshake rather than the first one.</param>
    public sealed record HandshakeInfo(string ServerId, Identity.PskEntry Matched, bool IsRehandshake);

    private enum State { Idle, AwaitServerInit, AwaitNoise1, Transport }

    private readonly ISendspinSocket socket;
    private readonly Identity        identity;
    private readonly object          gate = new();

    private State           state = State.Idle;
    private HandshakeState? handshake;
    private NoiseSession?   session;
    private byte[]          rawClientInit = [];
    private TaskCompletionSource? established;
    private CancellationTokenSource? handshakeTimer;
    private MemoryStream?   fragment;
    private byte            fragmentType;
    private bool            closed;

    /// <summary>The server's server_id from server/init; empty until it arrives.</summary>
    public string  ServerId       { get; private set; } = "";

    /// <summary>The handshake hash of the latest handshake, the prologue of the next re-handshake.</summary>
    public byte[]  HandshakeHash  { get; private set; } = [];

    /// <summary>The pre-shared key the latest handshake used; <see langword="null"/> before the first handshake.</summary>
    public Identity.PskEntry? Matched { get; private set; }

    /// <summary>Whether the session is in encrypted transport mode.</summary>
    public bool    Ready          => state == State.Transport;

    /// <summary>True between a re-handshake and the server/activate that ends it; periodic traffic is held back.</summary>
    public bool    Quiesced       { get; private set; }

    /// <summary>Raised under the connection lock after each handshake, including re-handshakes.</summary>
    public event Action<HandshakeInfo>? HandshakeCompleted;

    /// <summary>Raised for each decrypted control message, with its type and payload.</summary>
    public event Action<string, JsonElement>? ControlReceived;

    /// <summary>Raised for each decrypted binary frame, including its type byte.</summary>
    public event Action<byte[]>? BinaryReceived;

    /// <summary>Raised once when the session ends, with the reason.</summary>
    public event Action<string>? Closed;

    /// <summary>Wraps a socket; nothing is sent until <see cref="StartAsync"/>.</summary>
    /// <param name="socket">The wire to run the session over.</param>
    /// <param name="identity">This PC's keys and PSKs.</param>
    public SendspinConnection(ISendspinSocket socket, Identity identity)
    {
        this.socket   = socket;
        this.identity = identity;
        socket.TextReceived   += OnText;
        socket.BinaryReceived += OnBinary;
        socket.Closed         += reason => Fail(reason);
    }

    /// <summary>Open the socket, run the handshake and return once transport mode is up.</summary>
    /// <param name="ct">Cancels the wait for the handshake.</param>
    /// <returns>A task that completes when the session is encrypted, or faults with the failure reason.</returns>
    public async Task StartAsync(CancellationToken ct)
    {
        established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await socket.OpenAsync(ct);

        string init = JsonSerializer.Serialize(new { type = "client/init", payload = new { client_id = identity.ClientId, version = 1, suite = "25519_AESGCM_SHA256" } });
        lock (gate)
        {
            rawClientInit = Encoding.UTF8.GetBytes(init);
            state = State.AwaitServerInit;
            ArmHandshakeTimer();
        }
        socket.SendText(init);

        using (ct.Register(() => established.TrySetCanceled(ct)))
        {
            await established.Task;
        }
    }

    // Sending.

    /// <summary>Sends an encrypted control message; periodic types are dropped while the session is quiesced.</summary>
    /// <param name="type">The message type, such as client/state.</param>
    /// <param name="payload">The object serialized as the message payload.</param>
    public void SendControl(string type, object payload)
    {
        // Held back during a re-handshake.
        if (Quiesced && type is "client/time" or "client/state" or "client/command") return;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { type, payload });
        SendPlaintext([TypeJson, .. json]);
    }

    /// <summary>Send a binary frame; the first byte of data is its message type.</summary>
    /// <param name="data">The frame, starting with its type byte.</param>
    public void SendBinary(byte[] data) => SendPlaintext(data);

    private void SendPlaintext(byte[] plaintext)
    {
        NoiseSession? current;
        lock (gate)
        {
            if ((state != State.Transport) || session is null) return;
            current = session;
            if (plaintext.Length <= MaxTransportPlaintext)
            {
                socket.SendBinary(current.Encrypt(plaintext));
                return;
            }

            // Fragment: [2][origType][data...] then [2][data] ... [3][data]; encrypt in send order under the lock.
            Span<byte> body   = plaintext.AsSpan(1);
            int first  = Math.Min(body.Length, MaxTransportPlaintext - 2);
            socket.SendBinary(current.Encrypt([TypeFragmentMore, plaintext[0], .. body[..first]]));
            Span<byte> rest = body[first..];
            while (rest.Length > 0)
            {
                int take = Math.Min(rest.Length, MaxTransportPlaintext - 1);
                bool last = take == rest.Length;
                socket.SendBinary(current.Encrypt([last ? TypeFragmentEnd : TypeFragmentMore, .. rest[..take]]));
                rest = rest[take..];
            }
        }
    }

    // Receiving.

    private void OnText(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            string? type = root.GetProperty("type").GetString();
            JsonElement payload = root.GetProperty("payload");

            lock (gate)
            {
                if ((state == State.AwaitServerInit) && (type == "server/init"))
                {
                    if (payload.GetProperty("version").GetInt32() != 1)
                    {
                        Fail("Server speaks an unsupported Sendspin version");
                        return;
                    }
                    ServerId = payload.GetProperty("server_id").GetString() ?? "";
                    if (Base64Url.Decode(ServerId).Length != NoiseCrypto.KeySize)
                    {
                        Fail("Server sent an invalid server_id");
                        return;
                    }
                    byte[] prologue = (byte[])[.. rawClientInit, .. Encoding.UTF8.GetBytes(text)];
                    handshake = new HandshakeState(initiator: false, prologue, identity.PrivateKey, identity.PublicKey, Base64Url.Decode(ServerId));
                    state = State.AwaitNoise1;
                    ArmHandshakeTimer();
                    return;
                }
                if ((state == State.AwaitNoise1) && (type == "noise/handshake"))
                {
                    CompleteHandshake(handshake!, Base64Url.Decode(payload.GetProperty("data").GetString() ?? ""), rehandshake: false);
                    return;
                }
                if ((state == State.AwaitServerInit) && (type == "server/error"))
                {
                    Fail($"Server rejected the connection: {(payload.TryGetProperty("reason", out JsonElement r) ? r.GetString() : "error")}");
                    return;
                }
            }
            Fail("Unexpected message during handshake");
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Any error handling attacker-controlled handshake input is fatal to this connection, not just the
            // known JSON/crypto types: fail cleanly with a reason instead of leaking the exception up the read loop.
            Fail($"Handshake failed: {ex.Message}");
        }
    }

    /// <summary>Read Noise message 1, pick the PSK its payload names, answer with message 2 and switch keys. Caller holds the lock.</summary>
    private void CompleteHandshake(HandshakeState hs, byte[] message1, bool rehandshake)
    {
        byte[] payload1 = hs.ReadMessage1(message1);
        using var document = JsonDocument.Parse(payload1);
        string pskId = document.RootElement.GetProperty("psk_id").GetString() ?? "";
        string? category = document.RootElement.TryGetProperty("psk_category", out JsonElement c) ? c.GetString() : null;

        Identity.PskEntry? entry = identity.Lookup(pskId);
        // Held under another category: a miss.
        if (entry is not null && category is not null && (CategoryCode(entry.Category) != category)) entry = null;
        if (entry is { Category: Identity.PskCategory.LongTerm } && (entry.ServerId != ServerId))
        {
            Fail("Pairing record belongs to another server");
            return;
        }
        if (entry is null)
        {
            if (rehandshake)
            {
                Fail("Server referenced an unknown key");
                return;
            }
            // Sentinel fallback: the server learns we lost the record.
            entry = identity.Lookup(Identity.PskIdOf(Identity.SentinelPsk))!;
        }

        hs.SetPsk(entry.Psk);
        string message2 = Base64Url.Encode(hs.WriteMessage2("{}"u8));
        var frame = new { type = "noise/handshake", payload = new { data = message2 } };
        if (rehandshake)
        {
            // Message 2 travels under the old keys; everything after it under the new ones.
            SendPlaintext([TypeJson, .. JsonSerializer.SerializeToUtf8Bytes(frame)]);
            Quiesced = true;
        }
        else
        {
            socket.SendText(JsonSerializer.Serialize(frame));
        }

        session       = hs.Split();
        HandshakeHash = hs.HandshakeHash;
        Matched       = entry;
        state         = State.Transport;
        ClearHandshakeTimer();
        established?.TrySetResult();
        HandshakeCompleted?.Invoke(new HandshakeInfo(ServerId, entry, rehandshake));
    }

    private static string CategoryCode(Identity.PskCategory category) => category switch
    {
        Identity.PskCategory.LongTerm => "lt",
        Identity.PskCategory.Pairing  => "pr",
        _                             => "sn",
    };

    private void OnBinary(byte[] frame)
    {
        // A transport frame cannot exceed 65535 bytes; reject a larger one before Decrypt allocates for it (the socket is not fully trusted).
        if (frame.Length > MaxTransportFrame)
        {
            Fail("Transport frame too large");
            return;
        }

        // Decrypt and reassembly both touch shared state (session counter, fragment buffer), so they run under the
        // gate; dispatch runs outside it, because it fires callbacks that may be slow or re-enter the connection.
        byte[] toDispatch;
        lock (gate)
        {
            if ((state != State.Transport) || session is null)
            {
                Fail("Binary frame before the handshake finished");
                return;
            }
            byte[] plaintext;
            try
            {
                plaintext = session.Decrypt(frame);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                Fail("Encrypted frame failed authentication");
                return;
            }
            if (plaintext.Length == 0)
            {
                Fail("Empty frame");
                return;
            }

            switch (plaintext[0])
            {
                case TypeFragmentMore:
                case TypeFragmentEnd:
                    if (!Reassemble(plaintext, out byte[]? whole)) return;
                    toDispatch = whole;
                    break;
                default:
                    if (fragment is not null)
                    {
                        Fail("Frame received inside a fragmented message");
                        return;
                    }
                    toDispatch = plaintext;
                    break;
            }
        }
        Dispatch(toDispatch);
    }

    private bool Reassemble(byte[] plaintext, out byte[] whole)
    {
        whole = [];
        if (fragment is null)
        {
            if ((plaintext[0] != TypeFragmentMore) || (plaintext.Length < 2) || plaintext[1] is TypeFragmentMore or TypeFragmentEnd)
            {
                Fail("Malformed fragment");
                return false;
            }
            fragmentType = plaintext[1];
            fragment = new MemoryStream();
            fragment.Write(plaintext, 2, plaintext.Length - 2);
            return false;
        }
        fragment.Write(plaintext, 1, plaintext.Length - 1);
        if (fragment.Length > MaxReassembly)
        {
            fragment = null;
            Fail("Fragmented message too large");
            return false;
        }
        if (plaintext[0] == TypeFragmentMore) return false;

        whole = [fragmentType, .. fragment.ToArray()];
        fragment = null;
        return true;
    }

    private void Dispatch(byte[] plaintext)
    {
        if (plaintext[0] != TypeJson)
        {
            // Audio decode runs on peer-supplied bytes; a bad frame is dropped, never allowed to escape the receive thread.
            try
            {
                BinaryReceived?.Invoke(plaintext);
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                App.Log($"Sendspin: dropped audio frame: {ex.Message}");
            }
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(plaintext.AsMemory(1));
            JsonElement root = document.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";
            JsonElement payload = root.TryGetProperty("payload", out JsonElement p) ? p.Clone() : default;

            switch (type)
            {
                case "noise/handshake":
                    // A re-handshake failure is fatal, not a droppable message, so it fails the connection.
                    try
                    {
                        lock (gate)
                        {
                            var next = new HandshakeState(initiator: false, HandshakeHash, identity.PrivateKey, identity.PublicKey, Base64Url.Decode(ServerId));
                            CompleteHandshake(next, Base64Url.Decode(payload.GetProperty("data").GetString() ?? ""), rehandshake: true);
                        }
                    }
                    catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
                    {
                        Fail($"Re-handshake failed: {ex.Message}");
                    }
                    return;
                case "server/activate":
                    Quiesced = false;
                    break;
                case "server/unpair":
                    if (Matched is { Category: Identity.PskCategory.LongTerm } record)
                    {
                        identity.RemovePairingRecord(record.PskId);
                        SendControl("client/goodbye", new { reason = "unpaired" });
                        Close("Unpaired by the server");
                    }
                    return;
            }
            ControlReceived?.Invoke(type, payload);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // A malformed control message is dropped; the connection stays up.
            App.Log($"Sendspin: dropped malformed message: {ex.Message}");
        }
    }

    // Lifecycle.

    private void ArmHandshakeTimer()
    {
        ClearHandshakeTimer();
        handshakeTimer = new CancellationTokenSource(HandshakeTimeout);
        handshakeTimer.Token.Register(() => Fail("Sendspin handshake timed out"));
    }

    private void ClearHandshakeTimer()
    {
        handshakeTimer?.Dispose();
        handshakeTimer = null;
    }

    /// <summary>Send client/goodbye and close.</summary>
    /// <param name="reason">The reason passed to <see cref="Closed"/>.</param>
    /// <param name="goodbye">The reason sent to the server in client/goodbye.</param>
    public void Close(string reason, string goodbye = "user_request")
    {
        if (Ready)
        {
            try
            {
                SendControl("client/goodbye", new { reason = goodbye });
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
            }
        }
        Fail(reason);
    }

    private void Fail(string reason)
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            state  = State.Idle;
            ClearHandshakeTimer();
        }
        established?.TrySetException(new InvalidOperationException(reason));
        socket.Close();
        Closed?.Invoke(reason);
    }

    /// <summary>Ends the session and disposes the socket.</summary>
    public void Dispose()
    {
        Fail("Disposed");
        socket.Dispose();
    }
}
