// Self-test for the certificate pinning in bridge.js.
// A Remote ID encodes the first 16 bytes of the server's SHA-256 certificate
// fingerprint. Every SHA-256 fingerprint in the SDP answer must match it and
// weaker algorithms must be stripped, or the connection is refused.

"use strict";

const results = [];
const check = (name, condition) => results.push(`${condition ? "PASS" : "FAIL"}  ${name}`);
const throws = (fn) => { try { fn(); return false; } catch { return true; } };

// Known vector: 16 bytes 00 01 02 ... 0f encode as this Remote ID (base32, 2 written as 9)
const REMOTE_ID = "AAAQEAYEAUDAOCAJBIFQYDIOB4";
const good = "00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F";
const tail = ":10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F";
const bad  = "00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:FF";

const sdp = (...fingerprints) => "v=0\r\n" + fingerprints.map((f) => `a=fingerprint:${f}\r\n`).join("") + "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n";

check("remote id decodes to 16 bytes", decodeRemoteId(REMOTE_ID).join(",") === [...Array(16).keys()].join(","));
check("nines are read as twos",       decodeRemoteId(REMOTE_ID.replace(/2/g, "9")).length === 16);
check("matching fingerprint accepted", verifyAndSanitizeSdp(sdp("sha-256 " + good + tail), REMOTE_ID).includes("sha-256"));
check("wrong fingerprint rejected",    throws(() => verifyAndSanitizeSdp(sdp("sha-256 " + bad + tail), REMOTE_ID)));
check("session good + media bad rejected", throws(() => verifyAndSanitizeSdp(sdp("sha-256 " + good + tail, "sha-256 " + bad + tail), REMOTE_ID)));
check("weaker algorithms stripped",    !verifyAndSanitizeSdp(sdp("sha-1 AA:BB", "sha-256 " + good + tail), REMOTE_ID).includes("sha-1"));
check("only weaker algorithms rejected", throws(() => verifyAndSanitizeSdp(sdp("sha-1 AA:BB"), REMOTE_ID)));
check("empty sdp rejected",            throws(() => verifyAndSanitizeSdp("", REMOTE_ID)));
check("bad remote id rejected",        throws(() => verifyAndSanitizeSdp(sdp("sha-256 " + good + tail), "SHORT")));

document.getElementById("out").textContent = results.join("\n") + (results.some((r) => r.startsWith("FAIL")) ? "\nSELFTEST FAILED" : "\nSELFTEST OK");
