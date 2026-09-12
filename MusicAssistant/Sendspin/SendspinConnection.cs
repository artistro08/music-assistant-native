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
    private const int  MaxReassembly         = 4 * 1024 * 1024;
    private const byte TypeJson = 0, TypeFragmentMore = 2, TypeFragmentEnd = 3;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

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

    public string  ServerId       { get; private set; } = "";
    public byte[]  HandshakeHash  { get; private set; } = [];
    public Identity.PskEntry? Matched { get; private set; }
    public bool    Ready          => state == State.Transport;
    /// <summary>True between a re-handshake and the server/activate that ends it; periodic traffic is held back.</summary>
    public bool    Quiesced       { get; private set; }

    public event Action<HandshakeInfo>? HandshakeCompleted;
    public event Action<string, JsonElement>? ControlReceived;   // type, payload
    public event Action<byte[]>? BinaryReceived;                 // decrypted frame including its type byte
    public event Action<string>? Closed;

    public SendspinConnection(ISendspinSocket socket, Identity identity)
    {
        this.socket   = socket;
        this.identity = identity;
        socket.TextReceived   += OnText;
        socket.BinaryReceived += OnBinary;
        socket.Closed         += reason => Fail(reason);
    }

    /// <summary>Open the socket, run the handshake and return once transport mode is up.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await socket.OpenAsync(ct);

        var init = JsonSerializer.Serialize(new { type = "client/init", payload = new { client_id = identity.ClientId, version = 1, suite = "25519_AESGCM_SHA256" } });
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

    // Sending

    public void SendControl(string type, object payload)
    {
        if (Quiesced && type is "client/time" or "client/state" or "client/command") return;   // held back during a re-handshake
        var json = JsonSerializer.SerializeToUtf8Bytes(new { type, payload });
        SendPlaintext([TypeJson, .. json]);
    }

    /// <summary>Send a binary frame; the first byte of data is its message type.</summary>
    public void SendBinary(byte[] data) => SendPlaintext(data);

    private void SendPlaintext(byte[] plaintext)
    {
        NoiseSession? current;
        lock (gate)
        {
            if (state != State.Transport || session is null) return;
            current = session;
            if (plaintext.Length <= MaxTransportPlaintext)
            {
                socket.SendBinary(current.Encrypt(plaintext));
                return;
            }

            // Fragment: [2][origType][data...] then [2][data] ... [3][data]; encrypt in send order under the lock
            var body   = plaintext.AsSpan(1);
            var first  = Math.Min(body.Length, MaxTransportPlaintext - 2);
            socket.SendBinary(current.Encrypt([TypeFragmentMore, plaintext[0], .. body[..first]]));
            var rest = body[first..];
            while (rest.Length > 0)
            {
                var take = Math.Min(rest.Length, MaxTransportPlaintext - 1);
                var last = take == rest.Length;
                socket.SendBinary(current.Encrypt([last ? TypeFragmentEnd : TypeFragmentMore, .. rest[..take]]));
                rest = rest[take..];
            }
        }
    }

    // Receiving

    private void OnText(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            var payload = root.GetProperty("payload");

            lock (gate)
            {
                if (state == State.AwaitServerInit && type == "server/init")
                {
                    if (payload.GetProperty("version").GetInt32() != 1) { Fail("Server speaks an unsupported Sendspin version"); return; }
                    ServerId = payload.GetProperty("server_id").GetString() ?? "";
                    if (Base64Url.Decode(ServerId).Length != NoiseCrypto.KeySize) { Fail("Server sent an invalid server_id"); return; }
                    var prologue = (byte[])[.. rawClientInit, .. Encoding.UTF8.GetBytes(text)];
                    handshake = new HandshakeState(initiator: false, prologue, identity.PrivateKey, identity.PublicKey, Base64Url.Decode(ServerId));
                    state = State.AwaitNoise1;
                    ArmHandshakeTimer();
                    return;
                }
                if (state == State.AwaitNoise1 && type == "noise/handshake")
                {
                    CompleteHandshake(handshake!, Base64Url.Decode(payload.GetProperty("data").GetString() ?? ""), rehandshake: false);
                    return;
                }
                if (state == State.AwaitServerInit && type == "server/error")
                {
                    Fail("Server rejected the connection: " + (payload.TryGetProperty("reason", out var r) ? r.GetString() : "error"));
                    return;
                }
            }
            Fail("Unexpected message during handshake");
        }
        catch (Exception ex)
        {
            // Any error handling attacker-controlled handshake input is fatal to this connection, not just the
            // known JSON/crypto types: fail cleanly with a reason instead of leaking the exception up the read loop.
            Fail("Handshake failed: " + ex.Message);
        }
    }

    /// <summary>Read Noise message 1, pick the PSK its payload names, answer with message 2 and switch keys. Caller holds the lock.</summary>
    private void CompleteHandshake(HandshakeState hs, byte[] message1, bool rehandshake)
    {
        var payload1 = hs.ReadMessage1(message1);
        using var document = JsonDocument.Parse(payload1);
        var pskId = document.RootElement.GetProperty("psk_id").GetString() ?? "";
        var category = document.RootElement.TryGetProperty("psk_category", out var c) ? c.GetString() : null;

        var entry = identity.Lookup(pskId);
        if (entry is not null && category is not null && CategoryCode(entry.Category) != category) entry = null;   // held under another category: a miss
        if (entry is { Category: Identity.PskCategory.LongTerm } && entry.ServerId != ServerId) { Fail("Pairing record belongs to another server"); return; }
        if (entry is null)
        {
            if (rehandshake) { Fail("Server referenced an unknown key"); return; }
            entry = identity.Lookup(Identity.PskIdOf(Identity.SentinelPsk))!;   // Sentinel fallback: the server learns we lost the record
        }

        hs.SetPsk(entry.Psk);
        var message2 = Base64Url.Encode(hs.WriteMessage2("{}"u8));
        var frame = new { type = "noise/handshake", payload = new { data = message2 } };
        if (rehandshake)
        {
            // Message 2 travels under the old keys; everything after it under the new ones
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
        byte[] plaintext;
        lock (gate)
        {
            if (state != State.Transport || session is null) { Fail("Binary frame before the handshake finished"); return; }
            try { plaintext = session.Decrypt(frame); }
            catch (System.Security.Cryptography.CryptographicException) { Fail("Encrypted frame failed authentication"); return; }
        }
        if (plaintext.Length == 0) { Fail("Empty frame"); return; }

        switch (plaintext[0])
        {
            case TypeFragmentMore:
            case TypeFragmentEnd:
                if (!Reassemble(plaintext, out var whole)) return;
                Dispatch(whole);
                break;
            default:
                if (fragment is not null) { Fail("Frame received inside a fragmented message"); return; }
                Dispatch(plaintext);
                break;
        }
    }

    private bool Reassemble(byte[] plaintext, out byte[] whole)
    {
        whole = [];
        if (fragment is null)
        {
            if (plaintext[0] != TypeFragmentMore || plaintext.Length < 2 || plaintext[1] is TypeFragmentMore or TypeFragmentEnd) { Fail("Malformed fragment"); return false; }
            fragmentType = plaintext[1];
            fragment = new MemoryStream();
            fragment.Write(plaintext, 2, plaintext.Length - 2);
            return false;
        }
        fragment.Write(plaintext, 1, plaintext.Length - 1);
        if (fragment.Length > MaxReassembly) { fragment = null; Fail("Fragmented message too large"); return false; }
        if (plaintext[0] == TypeFragmentMore) return false;

        whole = [fragmentType, .. fragment.ToArray()];
        fragment = null;
        return true;
    }

    private void Dispatch(byte[] plaintext)
    {
        if (plaintext[0] != TypeJson)
        {
            BinaryReceived?.Invoke(plaintext);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(plaintext.AsMemory(1));
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;

            switch (type)
            {
                case "noise/handshake":
                    // A re-handshake failure is fatal, not a droppable message, so it fails the connection
                    try
                    {
                        lock (gate)
                        {
                            var next = new HandshakeState(initiator: false, HandshakeHash, identity.PrivateKey, identity.PublicKey, Base64Url.Decode(ServerId));
                            CompleteHandshake(next, Base64Url.Decode(payload.GetProperty("data").GetString() ?? ""), rehandshake: true);
                        }
                    }
                    catch (Exception ex) { Fail("Re-handshake failed: " + ex.Message); }
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
            // A malformed control message is dropped; the connection stays up
            App.Log("Sendspin: dropped malformed message: " + ex.Message);
        }
    }

    // Lifecycle

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
    public void Close(string reason, string goodbye = "user_request")
    {
        if (Ready) { try { SendControl("client/goodbye", new { reason = goodbye }); } catch (Exception) { } }
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

    public void Dispose()
    {
        Fail("Disposed");
        socket.Dispose();
    }
}
