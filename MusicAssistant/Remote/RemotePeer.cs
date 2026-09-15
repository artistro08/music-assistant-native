using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MusicAssistant.Api;
using MusicAssistant.Sendspin;
using SIPSorcery.Net;
using ChunkGroup = (int Count, string?[] Parts, int Received, System.DateTime Started, long Bytes);

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
    /// <summary>WebSocket address of Music Assistant's signaling server, which introduces this client to the home server by Remote ID.</summary>
    public const string SignalingUrl = "wss://signaling.music-assistant.io/ws";

    private static readonly RTCIceServer[] FallbackIce =
    [
        new() { urls = "stun:stun.l.google.com:19302" },
        new() { urls = "stun:stun.cloudflare.com:3478" },
    ];

    /// <summary>An HTTP response the home server sent back through the proxy on the API channel.</summary>
    /// <param name="Status">HTTP status code, or 0 when the reply carried none.</param>
    /// <param name="Headers">Response headers, looked up case-insensitively.</param>
    /// <param name="Body">Response body, decoded from the hex text the server sends.</param>
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

    /// <summary>A stalled or hostile peer must not accumulate reassembly buffers without bound.</summary>
    private const int MaxChunkGroups = 32;

    /// <summary>Signaling frames (SDP, ICE) are a few KB; cap the untrusted relay well above that.</summary>
    private const int MaxSignalingMessage = 1 * 1024 * 1024;

    /// <summary>One reassembled API/image payload; a peer must not grow a group without bound.</summary>
    private const int MaxChunkGroupBytes  = 16 * 1024 * 1024;

    private static readonly TimeSpan ChunkGroupMaxAge = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<long, ChunkGroup> chunkGroups = new();
    private readonly SemaphoreSlim signalingSend = new(1, 1);
    private readonly object gate = new();
    private bool closed;

    /// <summary>True once the "ma-api" data channel has opened, until the connection fails or is closed.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised with each complete API message from the server, after chunked frames are reassembled. Runs on SIPSorcery's receive thread.</summary>
    public event Action<string>? ApiMessage;

    /// <summary>Raised once, with the reason, when the connection fails or is closed.</summary>
    public event Action<string>? Closed;

    /// <summary>Creates a peer for the home server with the given Remote ID; nothing connects until <see cref="ConnectAsync"/>.</summary>
    /// <param name="remoteId">The server's Remote ID; surrounding whitespace is trimmed and letters are upper-cased.</param>
    public RemotePeer(string remoteId)
    {
        this.remoteId = remoteId.Trim().ToUpperInvariant();
    }

    // =========================================================================
    // CONNECT
    // =========================================================================

    /// <summary>
    /// Reaches the home server through the signaling server, negotiates the peer connection and completes once the
    /// "ma-api" data channel is open.
    /// </summary>
    /// <param name="ct">Cancels the connection attempt and, through a linked token, the signaling loop.</param>
    /// <returns>A task that completes when the API channel is ready for <see cref="SendApi"/>.</returns>
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
        api.onopen    += () =>
        {
            IsConnected = true;
            apiOpen.TrySetResult();
        };
        api.onclose   += () => Fail("Connection closed");
        api.onmessage += (_, protocol, data) => OnApiMessage(protocol, data);

        RTCSessionDescriptionInit offer = pc.createOffer();
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
            foreach (JsonElement server in servers.EnumerateArray())
            {
                string? username   = server.TryGetProperty("username", out JsonElement u) ? u.GetString() : null;
                string? credential = server.TryGetProperty("credential", out JsonElement c) ? c.GetString() : null;
                if (!server.TryGetProperty("urls", out JsonElement urls)) continue;
                List<string?> urlList = urls.ValueKind == JsonValueKind.Array ? [.. urls.EnumerateArray().Select(x => x.GetString())] : [urls.GetString()];
                foreach (string? url in urlList)
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
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
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
        byte[] buffer = new byte[64 * 1024];
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
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        OnSignalingClosed();
                        return;
                    }

                    // The relay is untrusted (that is why the server is cert-pinned); a huge message must not drive unbounded allocation.
                    if (message.Length + result.Count > MaxSignalingMessage)
                    {
                        Fail("Signaling message too large");
                        return;
                    }
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
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            switch (type)
            {
                case "connected":
                    sessionId = root.TryGetProperty("sessionId", out JsonElement sid) ? sid.GetString() : null;
                    connectedSignal?.TrySetResult(root.TryGetProperty("iceServers", out JsonElement ice) ? ice.Clone() : default);
                    break;
                case "answer":
                    if (root.TryGetProperty("data", out JsonElement answerData)) await HandleAnswerAsync(answerData);
                    break;
                case "ice-candidate":
                    if (root.TryGetProperty("data", out JsonElement candidateData)) HandleCandidate(candidateData);
                    break;
                case "peer-disconnected":
                    Fail("Server disconnected");
                    break;
                case "error":
                    string error = root.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "Signaling error" : "Signaling error";
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
            // Pinning happens here, before the description is accepted.
            string sdp = RemoteId.VerifyAndSanitizeSdp(answer.GetProperty("sdp").GetString(), remoteId);
            SetDescriptionResultEnum result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
            if (result != SetDescriptionResultEnum.OK)
            {
                Fail($"Answer rejected: {result}");
                return Task.CompletedTask;
            }

            lock (gate)
            {
                remoteDescribed = true;
                foreach (RTCIceCandidateInit candidate in pendingCandidates) pc.addIceCandidate(candidate);
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
            candidate     = data.TryGetProperty("candidate", out JsonElement c) ? c.GetString() ?? "" : "",
            sdpMid        = data.TryGetProperty("sdpMid", out JsonElement m) && (m.ValueKind == JsonValueKind.String) ? m.GetString() : null,
            sdpMLineIndex = data.TryGetProperty("sdpMLineIndex", out JsonElement i) && (i.ValueKind == JsonValueKind.Number) ? (ushort)i.GetInt32() : (ushort)0,
        };
        if (string.IsNullOrEmpty(init.candidate)) return;
        lock (gate)
        {
            if (!remoteDescribed)
            {
                pendingCandidates.Add(init);
                return;
            }
        }
        try
        {
            pc.addIceCandidate(init);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Remote: addIceCandidate: {ex.Message}");
        }
    }

    // =========================================================================
    // API CHANNEL
    // =========================================================================

    /// <summary>Sends one API message to the server over the "ma-api" data channel.</summary>
    /// <param name="text">The JSON command text to send.</param>
    /// <exception cref="ApiException">The API channel is not connected.</exception>
    public void SendApi(string text)
    {
        if (api is null || !IsConnected) throw new ApiException(0, "Not connected");
        api.send(text);
    }

    /// <summary>
    /// Runs on SIPSorcery's SCTP receive thread: any exception that escapes kills the whole transport,
    /// so the entire body is guarded and a bad message is dropped instead.
    /// </summary>
    private void OnApiMessage(DataChannelPayloadProtocols protocol, byte[] data)
    {
        if (protocol != DataChannelPayloadProtocols.WebRTC_String) return;
        try
        {
            string text = Encoding.UTF8.GetString(data);
            if ((text.Length > 0) && (text[0] == '{'))
            {
                // Oversized messages arrive as "__chunk__" frames; HTTP proxy replies come back on this channel too.
                using var document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                string? type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
                if (type == "__chunk__")
                {
                    HandleChunk(root);
                    return;
                }
                if (type == "http-proxy-response")
                {
                    FinishHttp(root);
                    return;
                }
            }
            ApiMessage?.Invoke(text);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Remote: dropped API message: {ex.Message}");
        }
    }

    private void HandleChunk(JsonElement frame)
    {
        long id    = frame.GetProperty("id").GetInt64();
        int seq   = frame.GetProperty("seq").GetInt32();
        int count = frame.GetProperty("count").GetInt32();
        string piece = frame.GetProperty("b64").GetString() ?? "";
        if ((count <= 0) || (count > 100_000) || (seq < 0) || (seq >= count)) return;

        // Drop groups that never completed (a dropped final frame, or a hostile peer opening many ids) so
        // reassembly state cannot grow without bound.
        if (chunkGroups.Count >= MaxChunkGroups)
        {
            DateTime cutoff = DateTime.UtcNow - ChunkGroupMaxAge;
            foreach ((long staleId, ChunkGroup g) in chunkGroups)
            {
                if (g.Started < cutoff) chunkGroups.TryRemove(staleId, out _);
            }

            // Still full: refuse a new group.
            if ((chunkGroups.Count >= MaxChunkGroups) && !chunkGroups.ContainsKey(id)) return;
        }

        // Each frame is base64-encoded on its own, so decode per frame and join the bytes; joining the base64
        // strings first would put '=' padding mid-string and throw, killing the SCTP transport.
        byte[][] parts;

        // The Parts array reference is stable for the group's life, so it is a safe lock target; the tuple's counters
        // are re-read from the dictionary inside the lock so concurrent frames for one id cannot lose an update.
        ChunkGroup slot = chunkGroups.GetOrAdd(id, _ => (count, new string?[count], 0, DateTime.UtcNow, 0L));
        lock (slot.Parts)
        {
            ChunkGroup group = chunkGroups.TryGetValue(id, out ChunkGroup current) ? current : slot;
            if (group.Parts[seq] is null)
            {
                group.Received++;
                group.Bytes += piece.Length;
            }

            // A peer must not grow one group past a sane payload size, even within the frame-count cap.
            if (group.Bytes > MaxChunkGroupBytes)
            {
                chunkGroups.TryRemove(id, out _);
                return;
            }
            group.Parts[seq] = piece;
            chunkGroups[id] = group;
            if (group.Received < group.Count) return;
            parts = [.. group.Parts.Select(p => Convert.FromBase64String(p ?? ""))];
        }
        chunkGroups.TryRemove(id, out _);
        string text = Encoding.UTF8.GetString([.. parts.SelectMany(b => b)]);
        OnApiMessage(DataChannelPayloadProtocols.WebRTC_String, Encoding.UTF8.GetBytes(text));
    }

    // =========================================================================
    // HTTP PROXY (artwork)
    // =========================================================================

    /// <summary>
    /// Sends an HTTP request to the home server through its proxy on the API channel and waits up to 30 seconds for
    /// the reply.
    /// </summary>
    /// <param name="method">HTTP method, for example "GET".</param>
    /// <param name="path">Server path and query, for example an /imageproxy URL.</param>
    /// <param name="headers">Request headers to forward, or <see langword="null"/> for none.</param>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>The server's status, headers and body.</returns>
    /// <exception cref="ApiException">The API channel is not connected.</exception>
    public async Task<HttpReply> HttpAsync(string method, string path, IDictionary<string, string>? headers, CancellationToken ct)
    {
        if (api is null || !IsConnected) throw new ApiException(0, "Not connected");
        string id  = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<HttpReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        httpWaiting[id] = tcs;
        api.send(JsonSerializer.Serialize(new { type = "http-proxy-request", id, method, path, headers = headers ?? new Dictionary<string, string>() }));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using (timeout.Token.Register(() =>
        {
            httpWaiting.TryRemove(id, out _);
            tcs.TrySetCanceled();
        }))
        {
            return await tcs.Task;
        }
    }

    private void FinishHttp(JsonElement response)
    {
        string id = response.GetProperty("id").GetString() ?? "";
        if (!httpWaiting.TryRemove(id, out TaskCompletionSource<HttpReply>? waiter)) return;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.TryGetProperty("headers", out JsonElement h) && (h.ValueKind == JsonValueKind.Object))
        {
            foreach (JsonProperty header in h.EnumerateObject()) headers[header.Name] = header.Value.ToString();
        }
        int status = response.TryGetProperty("status", out JsonElement s) ? s.GetInt32() : 0;
        byte[] body   = response.TryGetProperty("body", out JsonElement b) && (b.ValueKind == JsonValueKind.String) ? Convert.FromHexString(b.GetString()!) : [];
        waiter.TrySetResult(new HttpReply(status, headers, body));
    }

    // =========================================================================
    // EXTRA CHANNELS
    // =========================================================================

    /// <summary>Open a labeled data channel (for example "sendspin") as a Sendspin socket on this connection.</summary>
    /// <param name="label">The data channel label the server expects.</param>
    /// <returns>A socket for the channel; call its OpenAsync before sending.</returns>
    /// <exception cref="ApiException">The peer connection is not connected.</exception>
    public ISendspinSocket OpenChannelSocket(string label)
    {
        if (pc is null || !IsConnected) throw new ApiException(0, "Not connected");
        return new DataChannelSocket(pc, label);
    }

    /// <summary>Current state of the underlying peer connection; closed when there is none yet.</summary>
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
            channel.onerror   += error => RaiseClosed($"Channel error: {error}");
            channel.onmessage += (_, protocol, data) =>
            {
                // On SIPSorcery's SCTP receive thread: never let an exception escape or the transport dies.
                try
                {
                    if (protocol == DataChannelPayloadProtocols.WebRTC_String) TextReceived?.Invoke(Encoding.UTF8.GetString(data));
                    else BinaryReceived?.Invoke(data);
                }
                catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
                {
                    App.Log($"Speaker channel message dropped: {ex.Message}");
                }
            };

            // On an established connection the channel can be open before the handler above is attached.
            if (channel.readyState == RTCDataChannelState.open) open.TrySetResult();
            using (ct.Register(() => open.TrySetCanceled(ct)))
            {
                await open.Task;
            }
        }

        public void SendText(string text)
        {
            try
            {
                channel?.send(text);
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                RaiseClosed(ex.Message);
            }
        }

        public void SendBinary(byte[] data)
        {
            try
            {
                channel?.send(data);
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                RaiseClosed(ex.Message);
            }
        }

        public void Close()
        {
            try
            {
                channel?.close();
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
            }
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

    /// <summary>Tears the connection down and raises <see cref="Closed"/> with "Closed" if it has not closed already.</summary>
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
        foreach (string? id in httpWaiting.Keys.ToArray())
        {
            if (httpWaiting.TryRemove(id, out TaskCompletionSource<HttpReply>? waiter)) waiter.TrySetCanceled();
        }
        try
        {
            lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            api?.close();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
        }
        try
        {
            pc?.Close(reason);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
        }
        try
        {
            signaling?.Abort();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
        }
        Closed?.Invoke(reason);
    }

    /// <summary>Closes the connection, then disposes the signaling socket and the lifetime token source.</summary>
    public void Dispose()
    {
        Fail("Disposed");
        signaling?.Dispose();
        lifetime?.Dispose();
    }
}
