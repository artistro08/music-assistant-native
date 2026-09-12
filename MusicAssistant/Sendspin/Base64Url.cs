namespace MusicAssistant.Sendspin;

/// <summary>base64url without padding, the encoding Sendspin uses for keys, PSK ids and handshake bytes.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 1 => throw new FormatException("Invalid base64url length"), _ => "" };
        return Convert.FromBase64String(padded);
    }
}
