using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MusicAssistant.Api;
using MusicAssistant.Sendspin;
using SIPSorcery.Net;

namespace MusicAssistant.Remote;

/// <summary>
/// The WebRTC side of a remote connection, in process with SIPSorcery.
///
/// Talks to Music Assistant's signaling server to reach the home server by
/// Remote ID, pins the answer to the certificate the Remote ID names, and
/// brings up data channels on the peer connection: "ma-api" for the API,
/// "sendspin" for this PC's speaker. Artwork is fetched with HTTP-proxy
/// requests over the API channel (the server answers with a hex body, in
/// chunked frames when large). Reconnection policy stays with the caller.
/// </summary>
/// <remarks>
/// @link https://github.com/music-assistant/server/blob/dev/music_assistant/controllers/webserver/remote_access/gateway.py
/// </remarks>
public sealed class RemotePeer : IDisposable
{
    public const string SignalingUrl = "wss://signaling.music-assistant.io/ws";
    private static readonly RTCIceServer[] FallbackIce =
    [
        new() { urls = "stun:stun.l.google.com:19302" },
        new() { urls = "stun:stun.cloudflare.com:3478" },
    ];

    public sealed record HttpReply(int Status, Dictionary<string, string> Headers, byte[] Body);

    private readonly string remoteId;
    private ClientWebSocket? signaling;
    private CancellationTokenSource? lifetime;
    private RTCPeerConnection? pc;
    private RTCDataChannel? api;
    private string? sessionId;
    private bool remoteDescribed;
    private readonly List<RTCIceCandidateInit> pendingCandidates = [];
    private TaskCompletionSource<JsonElement>? connectedSignal;
    private TaskCompletionSource? apiOpen;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HttpReply>> httpWaiting = new();
    private const int MaxChunkGroups = 32;   // a stalled or hostile peer must not accumulate reassembly buffers without bound
    private static readonly TimeSpan ChunkGroupMaxAge = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<long, (int Count, string?[] Parts, int Received, DateTime Started)> chunkGroups = new();
    private readonly SemaphoreSlim signalingSend = new(1, 1);
    private readonly object gate = new();
    private bool closed;

    public bool IsConnected { get; private set; }

    public event Action<string>? ApiMessage;
    public event Action<string>? Closed;

    public RemotePeer(string remoteId)
    {
        this.remoteId = remoteId.Trim().ToUpperInvariant();
    }

    // =========================================================================
    // CONNECT
    // =========================================================================

    public async Task ConnectAsync(CancellationToken ct)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        signaling = new ClientWebSocket();
        signaling.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await signaling.ConnectAsync(new Uri(SignalingUrl), ct);
        _ = Task.Run(() => SignalingLoopAsync(lifetime.Token));

        connectedSignal = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendSignalingAsync(new { type = "connect-request", remoteId });
        JsonElement iceServers;
        using (ct.Register(() => connectedSignal.TrySetCanceled(ct)))
        {
            iceServers = await connectedSignal.Task;
        }

        var config = new RTCConfiguration { iceServers = ParseIceServers(iceServers) };
        pc = new RTCPeerConnection(config);
        pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            using var json = JsonDocument.Parse(candidate.toJSON());
            _ = SendSignalingAsync(new { type = "ice-candidate", remoteId, sessionId, data = json.RootElement.Clone() });
        };
        pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) Fail(state == RTCPeerConnectionState.failed ? "Peer connection failed" : "Connection closed");
        };

        apiOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api = await pc.createDataChannel("ma-api", new RTCDataChannelInit { ordered = true });
        api.onopen    += () => { IsConnected = true; apiOpen.TrySetResult(); };
        api.onclose   += () => Fail("Connection closed");
        api.onmessage += (_, protocol, data) => OnApiMessage(protocol, data);

        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);
        await SendSignalingAsync(new { type = "offer", remoteId, sessionId, data = new { type = "offer", sdp = offer.sdp } });

        using (ct.Register(() => apiOpen.TrySetCanceled(ct)))
        {
            await apiOpen.Task;
        }
    }

    private static List<RTCIceServer> ParseIceServers(JsonElement servers)
    {
        var list = new List<RTCIceServer>();
        if (servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                var username   = server.TryGetProperty("username", out var u) ? u.GetString() : null;
                var credential = server.TryGetProperty("credential", out var c) ? c.GetString() : null;
                if (!server.TryGetProperty("urls", out var urls)) continue;
                var urlList = urls.ValueKind == JsonValueKind.Array ? urls.EnumerateArray().Select(x => x.GetString()).ToList() : [urls.GetString()];
                foreach (var url in urlList)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    list.Add(new RTCIceServer { urls = url, username = username, credential = credential, credentialType = RTCIceCredentialType.password });
                }
            }
        }
        return list.Count > 0 ? list : [.. FallbackIce];
    }

    // =========================================================================
    // SIGNALING
    // =========================================================================

    private async Task SendSignalingAsync(object message)
    {
        if (signaling is not { State: WebSocketState.Open }) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await signalingSend.WaitAsync();
        try
        {
            if (signaling.State == WebSocketState.Open) await signaling.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            Fail("Signaling connection lost");
        }
        finally
        {
            signalingSend.Release();
        }
    }

    private async Task SignalingLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && signaling is { State: WebSocketState.Open })
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await signaling.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) { OnSignalingClosed(); return; }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                await HandleSignalingAsync(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested) OnSignalingClosed();
        }
    }

    /// <summary>Once the peer connection is up the signaling socket is not needed; before that, losing it is fatal.</summary>
    private void OnSignalingClosed()
    {
        if (!IsConnected) Fail("Signaling connection closed");
    }

    private async Task HandleSignalingAsync(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { return; }

        using (document)
        {
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "connected":
                    sessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
                    connectedSignal?.TrySetResult(root.TryGetProperty("iceServers", out var ice) ? ice.Clone() : default);
                    break;
                case "answer":
                    await HandleAnswerAsync(root.GetProperty("data"));
                    break;
                case "ice-candidate":
                    HandleCandidate(root.GetProperty("data"));
                    break;
                case "peer-disconnected":
                    Fail("Server disconnected");
                    break;
                case "error":
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "Signaling error" : "Signaling error";
                    if (connectedSignal is { Task.IsCompleted: false } pending) pending.TrySetException(new ApiException(0, error));
                    else Fail(error);
                    break;
            }
        }
    }

    private Task HandleAnswerAsync(JsonElement answer)
    {
        if (pc is null) return Task.CompletedTask;
        try
        {
            // Pinning happens here, before the description is accepted
            var sdp = RemoteId.VerifyAndSanitizeSdp(answer.GetProperty("sdp").GetString(), remoteId);
            var result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
            if (result != SetDescriptionResultEnum.OK) { Fail("Answer rejected: " + result); return Task.CompletedTask; }

            lock (gate)
            {
                remoteDescribed = true;
                foreach (var candidate in pendingCandidates) pc.addIceCandidate(candidate);
                pendingCandidates.Clear();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            Fail(ex.Message);
        }
        return Task.CompletedTask;
    }

    private void HandleCandidate(JsonElement data)
    {
        if (pc is null) return;
        var init = new RTCIceCandidateInit
        {
            candidate     = data.TryGetProperty("candidate", out var c) ? c.GetString() ?? "" : "",
            sdpMid        = data.TryGetProperty("sdpMid", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
            sdpMLineIndex = data.TryGetProperty("sdpMLineIndex", out var i) && i.ValueKind == JsonValueKind.Number ? (ushort)i.GetInt32() : (ushort)0,
        };
        if (string.IsNullOrEmpty(init.candidate)) return;
        lock (gate)
        {
            if (!remoteDescribed) { pendingCandidates.Add(init); return; }
        }
        try { pc.addIceCandidate(init); }
        catch (Exception ex) { App.Log("Remote: addIceCandidate: " + ex.Message); }
    }

    // =========================================================================
    // API CHANNEL
    // =========================================================================

    public void SendApi(string text)
    {
        if (api is null || !IsConnected) throw new ApiException(0, "Not connected");
        api.send(text);
    }

    // Runs on SIPSorcery's SCTP receive thread: any exception that escapes kills the whole transport,
    // so the entire body is guarded and a bad message is dropped instead.
    private void OnApiMessage(DataChannelPayloadProtocols protocol, byte[] data)
    {
        if (protocol != DataChannelPayloadProtocols.WebRTC_String) return;
        try
        {
            var text = Encoding.UTF8.GetString(data);
            if (text.Length > 0 && text[0] == '{')
            {
                // Oversized messages arrive as "__chunk__" frames; HTTP proxy replies come back on this channel too
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "__chunk__") { HandleChunk(root); return; }
                if (type == "http-proxy-response") { FinishHttp(root); return; }
            }
            ApiMessage?.Invoke(text);
        }
        catch (Exception ex)
        {
            App.Log("Remote: dropped API message: " + ex.Message);
        }
    }

    private void HandleChunk(JsonElement frame)
    {
        var id    = frame.GetProperty("id").GetInt64();
        var seq   = frame.GetProperty("seq").GetInt32();
        var count = frame.GetProperty("count").GetInt32();
        var piece = frame.GetProperty("b64").GetString() ?? "";
        if (count <= 0 || count > 100_000 || seq < 0 || seq >= count) return;

        // Drop groups that never completed (a dropped final frame, or a hostile peer opening many ids) so
        // reassembly state cannot grow without bound
        if (chunkGroups.Count >= MaxChunkGroups)
        {
            var cutoff = DateTime.UtcNow - ChunkGroupMaxAge;
            foreach (var (staleId, g) in chunkGroups)
            {
                if (g.Started < cutoff) chunkGroups.TryRemove(staleId, out _);
            }
            if (chunkGroups.Count >= MaxChunkGroups && !chunkGroups.ContainsKey(id)) return;   // still full: refuse a new group
        }

        // Each frame is base64-encoded on its own, so decode per frame and join the bytes; joining the base64
        // strings first would put '=' padding mid-string and throw, killing the SCTP transport.
        byte[][] parts;
        var group = chunkGroups.GetOrAdd(id, _ => (count, new string?[count], 0, DateTime.UtcNow));
        lock (group.Parts)
        {
            if (group.Parts[seq] is null) group.Received++;
            group.Parts[seq] = piece;
            chunkGroups[id] = group;
            if (group.Received < group.Count) return;
            parts = group.Parts.Select(p => Convert.FromBase64String(p ?? "")).ToArray();
        }
        chunkGroups.TryRemove(id, out _);
        var text = Encoding.UTF8.GetString(parts.SelectMany(b => b).ToArray());
        OnApiMessage(DataChannelPayloadProtocols.WebRTC_String, Encoding.UTF8.GetBytes(text));
    }

    // =========================================================================
    // HTTP PROXY (artwork)
    // =========================================================================

    public async Task<HttpReply> HttpAsync(string method, string path, IDictionary<string, string>? headers, CancellationToken ct)
    {
        if (api is null || !IsConnected) throw new ApiException(0, "Not connected");
        var id  = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<HttpReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        httpWaiting[id] = tcs;
        api.send(JsonSerializer.Serialize(new { type = "http-proxy-request", id, method, path, headers = headers ?? new Dictionary<string, string>() }));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using (timeout.Token.Register(() => { httpWaiting.TryRemove(id, out _); tcs.TrySetCanceled(); }))
        {
            return await tcs.Task;
        }
    }

    private void FinishHttp(JsonElement response)
    {
        var id = response.GetProperty("id").GetString() ?? "";
        if (!httpWaiting.TryRemove(id, out var waiter)) return;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in h.EnumerateObject()) headers[header.Name] = header.Value.ToString();
        }
        var status = response.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
        var body   = response.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? Convert.FromHexString(b.GetString()!) : [];
        waiter.TrySetResult(new HttpReply(status, headers, body));
    }

    // =========================================================================
    // EXTRA CHANNELS
    // =========================================================================

    /// <summary>Open a labeled data channel (for example "sendspin") as a Sendspin socket on this connection.</summary>
    public ISendspinSocket OpenChannelSocket(string label)
    {
        if (pc is null || !IsConnected) throw new ApiException(0, "Not connected");
        return new DataChannelSocket(pc, label);
    }

    public RTCPeerConnectionState PeerState => pc?.connectionState ?? RTCPeerConnectionState.closed;

    private sealed class DataChannelSocket(RTCPeerConnection pc, string label) : ISendspinSocket
    {
        private RTCDataChannel? channel;
        private bool closedRaised;

        public event Action<string>? TextReceived;
        public event Action<byte[]>? BinaryReceived;
        public event Action<string>? Closed;

        public async Task OpenAsync(CancellationToken ct)
        {
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            channel = await pc.createDataChannel(label, new RTCDataChannelInit { ordered = true });
            channel.onopen    += () => open.TrySetResult();
            channel.onclose   += () => RaiseClosed("Channel closed");
            channel.onerror   += error => RaiseClosed("Channel error: " + error);
            channel.onmessage += (_, protocol, data) =>
            {
                // On SIPSorcery's SCTP receive thread: never let an exception escape or the transport dies
                try
                {
                    if (protocol == DataChannelPayloadProtocols.WebRTC_String) TextReceived?.Invoke(Encoding.UTF8.GetString(data));
                    else BinaryReceived?.Invoke(data);
                }
                catch (Exception ex) { App.Log("Speaker channel message dropped: " + ex.Message); }
            };
            // On an established connection the channel can be open before the handler above is attached
            if (channel.readyState == RTCDataChannelState.open) open.TrySetResult();
            using (ct.Register(() => open.TrySetCanceled(ct)))
            {
                await open.Task;
            }
        }

        public void SendText(string text)   { try { channel?.send(text); } catch (Exception ex) { RaiseClosed(ex.Message); } }
        public void SendBinary(byte[] data) { try { channel?.send(data); } catch (Exception ex) { RaiseClosed(ex.Message); } }

        public void Close()
        {
            try { channel?.close(); } catch (Exception) { }
            RaiseClosed("Closed");
        }

        private void RaiseClosed(string reason)
        {
            if (closedRaised) return;
            closedRaised = true;
            Closed?.Invoke(reason);
        }

        public void Dispose() => Close();
    }

    // =========================================================================
    // LIFECYCLE
    // =========================================================================

    public void Close() => Fail("Closed");

    private void Fail(string reason)
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
        }
        IsConnected = false;
        connectedSignal?.TrySetException(new ApiException(0, reason));
        apiOpen?.TrySetException(new ApiException(0, reason));
        foreach (var id in httpWaiting.Keys.ToArray())
        {
            if (httpWaiting.TryRemove(id, out var waiter)) waiter.TrySetCanceled();
        }
        try { lifetime?.Cancel(); } catch (ObjectDisposedException) { }
        try { api?.close(); } catch (Exception) { }
        try { pc?.Close(reason); } catch (Exception) { }
        try { signaling?.Abort(); } catch (Exception) { }
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        Fail("Disposed");
        signaling?.Dispose();
        lifetime?.Dispose();
    }
}
