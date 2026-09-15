namespace MusicAssistant.Sendspin;

/// <summary>base64url without padding, the encoding Sendspin uses for keys, PSK ids and handshake bytes.</summary>
public static class Base64Url
{
    /// <summary>Encodes bytes as base64url with the trailing padding removed.</summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>The unpadded base64url text.</returns>
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes unpadded base64url text, restoring the padding standard base64 needs.</summary>
    /// <param name="text">The base64url text to decode.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="FormatException">The text has an impossible length or contains invalid characters.</exception>
    public static byte[] Decode(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 1 => throw new FormatException("Invalid base64url length"), _ => "" };
        return Convert.FromBase64String(padded);
    }
}
