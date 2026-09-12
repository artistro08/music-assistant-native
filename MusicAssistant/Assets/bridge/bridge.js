// Music Assistant remote bridge
//
// Runs inside a hidden WebView2. Owns the WebRTC side of a remote connection:
// signaling, DTLS certificate pinning (the Remote ID is the server certificate
// fingerprint), the "ma-api" data channel and the "http_proxy" channel used for
// artwork. Talks to the host app only through postMessage with plain JSON.
//
// Mirrors music-assistant/frontend src/plugins/remote/*. Reconnection policy is
// deliberately left to the host: this file reports state, the host decides.

"use strict";

// Host Messaging

const host = window.chrome && window.chrome.webview;   // absent when the page runs outside WebView2 (self-test)
const post = (message) => { if (host) host.postMessage(message); };
const log  = (text) => post({ type: "log", text });

// Constants

const API_CHANNEL        = "ma-api";
const HTTP_PROXY_CHANNEL = "http_proxy";
const HTTP_PROXY_MIN_SCHEMA = 49;
const FALLBACK_ICE = [
    { urls: "stun:stun.l.google.com:19302" },
    { urls: "stun:stun.cloudflare.com:3478" },
];
const CONNECT_TIMEOUT_MS = 30000;

// State

let signaling = null;
let sessionId = null;
let remoteId  = null;
let pc        = null;
let api       = null;
let proxy     = null;
let proxyRequested   = false;
let remoteDescribed  = false;
let pendingCandidates = [];
let connectTimer     = null;

const chunkGroups   = new Map();   // id -> { count, parts, received, dispatch }
const httpWaiting   = new Map();   // request id -> true while unanswered
let   proxyPending  = null;        // { id, status, headers, size, parts, received }

// =========================================================================
// CERTIFICATE PINNING
// =========================================================================

// Remote ID is base32 (no padding) of the first 16 bytes of the SHA-256
// certificate fingerprint, with every "2" written as "9".
function decodeRemoteId(id) {
    const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    const text = id.toUpperCase().replace(/9/g, "2");
    let bits = 0, value = 0;
    const out = [];
    for (const ch of text) {
        const index = alphabet.indexOf(ch);
        if (index < 0) throw new Error("Invalid Remote ID");
        value = (value << 5) | index;
        bits += 5;
        if (bits >= 8) {
            out.push((value >>> (bits - 8)) & 0xff);
            bits -= 8;
        }
    }
    if (out.length !== 16) throw new Error("Invalid Remote ID length");
    return out;
}

// Keep only SHA-256 fingerprints and require every one of them to match.
function verifyAndSanitizeSdp(sdp, expectedId) {
    if (!sdp) throw new Error("No SDP in answer");
    const expected  = decodeRemoteId(expectedId);
    const sanitized = sdp.replace(/^a=fingerprint:(?!sha-256).*\r?\n?/gim, "");
    const matches   = [...sanitized.matchAll(/a=fingerprint:sha-256\s+([A-Fa-f0-9:]+)/gi)];
    if (matches.length === 0) throw new Error("No SHA-256 fingerprint in answer");

    for (const match of matches) {
        const hex = match[1].replace(/:/g, "");
        for (let i = 0; i < 16; i++) {
            if (parseInt(hex.substring(i * 2, i * 2 + 2), 16) !== expected[i]) {
                throw new Error("Server certificate does not match the Remote ID");
            }
        }
    }
    return sanitized;
}

// =========================================================================
// SIGNALING
// =========================================================================

function openSignaling(url) {
    return new Promise((resolve, reject) => {
        const ws = new WebSocket(url);
        ws.onopen  = () => resolve(ws);
        ws.onerror = () => reject(new Error("Could not reach the signaling server"));
        ws.onclose = () => { if (signaling === ws) fail("Signaling connection closed"); };
        ws.onmessage = (event) => {
            let message;
            try { message = JSON.parse(event.data); } catch { return; }
            handleSignaling(message);
        };
    });
}

function sendSignaling(message) {
    if (signaling && signaling.readyState === WebSocket.OPEN) signaling.send(JSON.stringify(message));
}

let onConnected = null;   // resolver for the connect-request round trip

function handleSignaling(message) {
    switch (message.type) {
        case "connected":
            sessionId = message.sessionId || null;
            if (onConnected) { onConnected.resolve(message.iceServers); onConnected = null; }
            break;
        case "answer":
            handleAnswer(message.data);
            break;
        case "ice-candidate":
            handleCandidate(message.data);
            break;
        case "peer-disconnected":
            fail("Server disconnected");
            break;
        case "error":
            if (onConnected) { onConnected.reject(new Error(message.error || "Signaling error")); onConnected = null; }
            else fail(message.error || "Signaling error");
            break;
    }
}

// =========================================================================
// CONNECTION
// =========================================================================

async function connect(id, signalingUrl) {
    cleanup();
    remoteId = id.trim().toUpperCase();
    post({ type: "state", state: "connecting" });

    connectTimer = setTimeout(() => fail("Connection timed out"), CONNECT_TIMEOUT_MS);
    try {
        signaling = await openSignaling(signalingUrl);

        const iceServers = await new Promise((resolve, reject) => {
            onConnected = { resolve, reject };
            sendSignaling({ type: "connect-request", remoteId });
        });

        pc = new RTCPeerConnection({ iceServers: iceServers || FALLBACK_ICE, iceCandidatePoolSize: 4 });
        pc.onicecandidate = (event) => {
            if (event.candidate) sendSignaling({ type: "ice-candidate", remoteId, sessionId, data: event.candidate.toJSON() });
        };
        pc.onconnectionstatechange = () => {
            if (pc && pc.connectionState === "failed") fail("Peer connection failed");
        };

        api = pc.createDataChannel(API_CHANNEL, { ordered: true });
        api.onopen  = () => { clearTimeout(connectTimer); connectTimer = null; post({ type: "state", state: "connected" }); post({ type: "open" }); };
        api.onclose = () => fail("Connection closed");
        api.onmessage = (event) => receive(event.data, dispatchApi);

        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        sendSignaling({ type: "offer", remoteId, sessionId, data: offer });
    } catch (error) {
        fail(error.message || String(error));
    }
}

async function handleAnswer(answer) {
    if (!pc) return;
    try {
        const sdp = verifyAndSanitizeSdp(answer.sdp, remoteId);
        await pc.setRemoteDescription({ type: answer.type, sdp });
        remoteDescribed = true;
        for (const candidate of pendingCandidates) await pc.addIceCandidate(candidate);
        pendingCandidates = [];
    } catch (error) {
        fail(error.message || String(error));
    }
}

async function handleCandidate(candidate) {
    if (!pc) return;
    if (!remoteDescribed) { pendingCandidates.push(candidate); return; }
    try { await pc.addIceCandidate(candidate); } catch (error) { log("addIceCandidate: " + error.message); }
}

function fail(reason) {
    if (!pc && !signaling) return;   // already torn down
    cleanup();
    post({ type: "state", state: "disconnected" });
    post({ type: "close", reason });
}

function cleanup() {
    clearTimeout(connectTimer); connectTimer = null;
    for (const channel of [api, proxy]) {
        if (!channel) continue;
        channel.onopen = channel.onclose = channel.onerror = channel.onmessage = null;
        try { channel.close(); } catch { }
    }
    api = proxy = null;
    proxyRequested = false;
    if (pc) { pc.onicecandidate = pc.onconnectionstatechange = null; try { pc.close(); } catch { } pc = null; }
    if (signaling) { const ws = signaling; signaling = null; ws.onclose = null; try { ws.close(); } catch { } }
    onConnected = null;
    sessionId = null;
    remoteDescribed = false;
    pendingCandidates = [];
    chunkGroups.clear();
    proxyPending = null;
    for (const id of httpWaiting.keys()) post({ type: "http-response", id, status: 0, headers: {}, body: "" });
    httpWaiting.clear();
}

// =========================================================================
// MESSAGES
// =========================================================================

// Oversized messages arrive as "__chunk__" frames; reassemble before dispatching.
function receive(data, dispatch) {
    if (typeof data === "string") {
        let frame = null;
        try { frame = JSON.parse(data); } catch { }
        if (frame && frame.type === "__chunk__") { handleChunk(frame, dispatch); return; }
    }
    dispatch(data);
}

function handleChunk(frame, dispatch) {
    let group = chunkGroups.get(frame.id);
    if (!group) {
        group = { count: frame.count, parts: new Array(frame.count), received: 0, dispatch };
        chunkGroups.set(frame.id, group);
    }
    if (group.parts[frame.seq] === undefined) group.received++;
    group.parts[frame.seq] = frame.b64;
    if (group.received < group.count) return;

    chunkGroups.delete(frame.id);
    const bytes = base64ToBytes(group.parts.join(""));
    group.dispatch(new TextDecoder().decode(bytes));
}

function dispatchApi(data) {
    let parsed = null;
    try { parsed = JSON.parse(data); } catch { }
    if (parsed && parsed.type === "http-proxy-response") { finishHexResponse(parsed); return; }
    if (parsed && typeof parsed.schema_version === "number" && parsed.schema_version >= HTTP_PROXY_MIN_SCHEMA) openProxyChannel();
    post({ type: "message", data });
}

function send(data) {
    if (!api || api.readyState !== "open") throw new Error("Not connected");
    api.send(data);
}

// =========================================================================
// HTTP PROXY (artwork and previews)
// =========================================================================

function openProxyChannel() {
    if (proxyRequested || !pc) return;
    proxyRequested = true;
    const channel = pc.createDataChannel(HTTP_PROXY_CHANNEL, { ordered: true });
    channel.binaryType = "arraybuffer";
    channel.onopen    = () => { proxy = channel; };
    channel.onmessage = (event) => receiveProxy(event.data);
    channel.onclose   = () => {
        if (proxy === channel) proxy = null;
        proxyPending = null;
        for (const [id, group] of chunkGroups) if (group.dispatch === dispatchProxy) chunkGroups.delete(id);
    };
}

function http(id, method, path, headers) {
    const channel = proxy && proxy.readyState === "open" ? proxy : api;
    if (!channel || channel.readyState !== "open") {
        post({ type: "http-response", id, status: 0, headers: {}, body: "" });
        return;
    }
    httpWaiting.set(id, true);
    channel.send(JSON.stringify({ type: "http-proxy-request", id, method, path, headers: headers || {} }));
}

function receiveProxy(data) {
    if (typeof data !== "string") {
        if (!proxyPending) return;
        proxyPending.parts.push(new Uint8Array(data));
        proxyPending.received += data.byteLength;
        if (proxyPending.received >= proxyPending.size) finishBinaryResponse();
        return;
    }
    receive(data, dispatchProxy);
}

function dispatchProxy(data) {
    let parsed = null;
    try { parsed = JSON.parse(data); } catch { return; }
    if (parsed.type !== "http-proxy-response") return;
    if (typeof parsed.size !== "number") { finishHexResponse(parsed); return; }
    if (!httpWaiting.has(parsed.id)) return;
    proxyPending = { id: parsed.id, status: parsed.status, headers: parsed.headers || {}, size: parsed.size, parts: [], received: 0 };
    if (parsed.size === 0) finishBinaryResponse();
}

function finishBinaryResponse() {
    const pending = proxyPending;
    proxyPending = null;
    if (!pending || !httpWaiting.delete(pending.id)) return;
    const body = new Uint8Array(pending.received);
    let offset = 0;
    for (const part of pending.parts) { body.set(part, offset); offset += part.byteLength; }
    post({ type: "http-response", id: pending.id, status: pending.status, headers: pending.headers, body: bytesToBase64(body.subarray(0, pending.size)) });
}

function finishHexResponse(parsed) {
    if (!httpWaiting.delete(parsed.id)) return;
    const hex = parsed.body || "";
    const bytes = new Uint8Array(hex.length / 2);
    for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
    post({ type: "http-response", id: parsed.id, status: parsed.status, headers: parsed.headers || {}, body: bytesToBase64(bytes) });
}

// Helpers

function base64ToBytes(b64) {
    const binary = atob(b64);
    const out = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
    return out;
}

function bytesToBase64(bytes) {
    let binary = "";
    const step = 0x8000;
    for (let i = 0; i < bytes.length; i += step) binary += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
    return btoa(binary);
}

// =========================================================================
// HOST COMMANDS
// =========================================================================

if (host) host.addEventListener("message", (event) => {
    const command = event.data;
    try {
        switch (command.type) {
            case "connect":    connect(command.remoteId, command.signalingUrl); break;
            case "send":       send(command.data); break;
            case "http":       http(command.id, command.method, command.path, command.headers); break;
            case "disconnect": cleanup(); post({ type: "state", state: "disconnected" }); break;
            case "player-start": startPlayer(command); break;
            case "player-stop":  stopPlayer(command.reason); break;
            case "player-volume": if (player) { player.setVolume(command.volume); player.setMuted(!!command.muted); } break;
        }
    } catch (error) {
        post({ type: "error", message: error.message || String(error) });
    }
});

// =========================================================================
// SPEAKER (Sendspin player, this PC as an output)
// =========================================================================
//
// sendspin-js (Apache-2.0, vendored as sendspin.js) implements the Sendspin
// protocol: Noise-encrypted session, pairing, clock sync, decoding and
// scheduled playback through Web Audio. It opens `new WebSocket(baseUrl +
// "/sendspin")` itself, so WebSocket is intercepted here: locally the socket is
// the server's authenticated /sendspin proxy (token first, wait for auth_ok),
// remotely it is a "sendspin" data channel on the existing peer connection.

const NativeWebSocket = window.WebSocket;
let player       = null;
let playerConfig = null;   // { baseUrl, token, name, remote }

class SendspinSocket {
    constructor() {
        this.binaryType = "arraybuffer";
        this.onopen = this.onmessage = this.onerror = this.onclose = null;
        this.readyState = 0;
        this.queue = [];
        this.inner = null;
        this.closed = false;
        Promise.resolve().then(() => this.attach()).catch((error) => { log("sendspin socket: " + error.message); this.fireClose(); });
    }

    async attach() {
        if (!playerConfig) throw new Error("no player config");
        if (playerConfig.remote) {
            if (!pc || pc.connectionState !== "connected") throw new Error("remote connection not ready");
            const channel = pc.createDataChannel("sendspin", { ordered: true });
            channel.binaryType = "arraybuffer";
            channel.onopen    = () => this.fireOpen();
            channel.onmessage = (event) => this.onmessage && this.onmessage(event);
            channel.onclose   = () => this.fireClose();
            channel.onerror   = () => this.onerror && this.onerror(new Event("error"));
            this.inner = { send: (d) => channel.send(d), close: () => channel.close(), get open() { return channel.readyState === "open"; } };
            return;
        }

        const url = playerConfig.baseUrl.replace(/^http/, "ws").replace(/\/$/, "") + "/sendspin";
        const ws  = new NativeWebSocket(url);
        ws.binaryType = "arraybuffer";
        let authed = false;
        ws.onopen    = () => ws.send(JSON.stringify({ type: "auth", token: playerConfig.token, client_id: playerConfig.clientId || "" }));
        ws.onmessage = (event) => {
            if (!authed) { authed = true; this.fireOpen(); return; }   // first reply is auth_ok
            if (this.onmessage) this.onmessage(event);
        };
        ws.onclose = () => this.fireClose();
        ws.onerror = () => this.onerror && this.onerror(new Event("error"));
        this.inner = { send: (d) => ws.send(d), close: () => ws.close(), get open() { return ws.readyState === 1; } };
    }

    send(data) {
        if (this.closed) return;
        if (this.inner && this.inner.open && this.readyState === 1) this.inner.send(data);
        else this.queue.push(data);
    }

    close() {
        this.closed = true;
        this.readyState = 2;
        if (this.inner) this.inner.close();
        else this.fireClose();
    }

    fireOpen() {
        if (this.closed || this.readyState !== 0) return;
        this.readyState = 1;
        for (const data of this.queue) this.inner.send(data);
        this.queue = [];
        if (this.onopen) this.onopen(new Event("open"));
    }

    fireClose() {
        if (this.readyState === 3) return;
        this.readyState = 3;
        if (this.onclose) this.onclose(new CloseEvent("close"));
    }

    addEventListener(type, fn) { this["on" + type] = fn; }
    removeEventListener() { }
}
SendspinSocket.CONNECTING = 0; SendspinSocket.OPEN = 1; SendspinSocket.CLOSING = 2; SendspinSocket.CLOSED = 3;

window.WebSocket = function (url, protocols) {
    return String(url).includes("/sendspin") ? new SendspinSocket() : new NativeWebSocket(url, protocols);
};
Object.assign(window.WebSocket, { CONNECTING: 0, OPEN: 1, CLOSING: 2, CLOSED: 3 });

function reportPlayer() {
    if (!player) { post({ type: "player-state", running: false }); return; }
    post({ type: "player-state", running: true, connected: player.isConnected, playing: player.isPlaying, volume: player.volume, muted: player.muted, state: player.playerState, clientId: player.clientId });
}

async function startPlayer(config) {
    stopPlayer("restart");
    playerConfig = config;
    try {
        player = new Sendspin.SendspinPlayer({
            baseUrl:            "http://sendspin.local",   // placeholder; the interceptor supplies the real socket
            clientName:         config.name,
            productName:        "Web Player",              // how the server recognizes its built-in player and auto-pairs it
            codecs:             ["opus", "flac"],
            requiredLeadTimeMs: 250,
            minBufferMs:        500,
            correctionMode:     "quality-local",
            onStateChange:      () => reportPlayer(),
            onPairing:          (event, detail) => log(`pairing ${event} ${detail || ""}`),
            reconnect:          { baseDelayMs: 1000, maxDelayMs: 30000, onReconnected: () => post({ type: "player-pairing", token: player && player.pairingToken }) },
        });
        await player.unlock();
        await player.connect();
        post({ type: "player-pairing", token: player.pairingToken });
        reportPlayer();
    } catch (error) {
        log("player start failed: " + (error.message || error));
        post({ type: "player-error", message: error.message || String(error) });
        stopPlayer("restart");
    }
}

function stopPlayer(reason) {
    if (!player) return;
    try { player.disconnect(reason || "user_request"); } catch { }
    player = null;
    playerConfig = null;
    reportPlayer();
}

post({ type: "ready" });
