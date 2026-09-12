using System.Text.RegularExpressions;

namespace MusicAssistant.Api;

/// <summary>
/// Certificate pinning for remote access.
///
/// A Remote ID is the base32 encoding (no padding, every "2" written as "9")
/// of the first 16 bytes of the SHA-256 fingerprint of the server's DTLS
/// certificate. Before the WebRTC answer is accepted, every SHA-256
/// fingerprint it carries must match those 16 bytes and weaker fingerprints
/// are removed, so neither the signaling relay nor anyone between can swap in
/// their own certificate. Mirrors music-assistant/frontend src/plugins/remote.
/// </summary>
public static partial class RemoteId
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    [GeneratedRegex(@"^a=fingerprint:(?!sha-256).*\r?\n?", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WeakFingerprintLine();

    [GeneratedRegex(@"a=fingerprint:sha-256\s+([A-Fa-f0-9:]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Fingerprint();

    /// <summary>The 16 fingerprint bytes a Remote ID encodes.</summary>
    public static byte[] Decode(string remoteId)
    {
        var text = remoteId.Trim().ToUpperInvariant().Replace('9', '2');
        var output = new List<byte>(16);
        int value = 0, bits = 0;
        foreach (var ch in text)
        {
            var index = Alphabet.IndexOf(ch);
            if (index < 0) throw new FormatException("Invalid Remote ID");
            value = (value << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        if (output.Count != 16) throw new FormatException("Invalid Remote ID length");
        return [.. output];
    }

    /// <summary>
    /// Strip non-SHA-256 fingerprints from the SDP and require every remaining
    /// one to start with the Remote ID's bytes. Throws when the answer cannot be trusted.
    /// </summary>
    public static string VerifyAndSanitizeSdp(string? sdp, string remoteId)
    {
        if (string.IsNullOrEmpty(sdp)) throw new InvalidOperationException("No SDP in answer");
        var expected  = Decode(remoteId);
        var sanitized = WeakFingerprintLine().Replace(sdp, "");
        var matches   = Sha256Fingerprint().Matches(sanitized);
        if (matches.Count == 0) throw new InvalidOperationException("No SHA-256 fingerprint in answer");

        foreach (Match match in matches)
        {
            var hex = match.Groups[1].Value.Replace(":", "");
            if (hex.Length < 32) throw new InvalidOperationException("Server certificate does not match the Remote ID");
            for (var i = 0; i < 16; i++)
            {
                if (Convert.ToByte(hex.Substring(i * 2, 2), 16) != expected[i]) throw new InvalidOperationException("Server certificate does not match the Remote ID");
            }
        }
        return sanitized;
    }
}
