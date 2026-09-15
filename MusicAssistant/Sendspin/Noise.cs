using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace MusicAssistant.Sendspin;

/// <summary>
/// The Noise Protocol pieces Sendspin needs: pattern KKpsk2 with 25519, AES-GCM
/// and SHA-256 (protocol name Noise_KKpsk2_25519_AESGCM_SHA256).
///
/// Written against the Noise specification (revision 34) sections 5 (state
/// objects), 7.2 (handshake patterns) and 9 (PSK modifiers), and checked
/// against the sendspin-js and aiosendspin implementations it must interoperate
/// with. X25519 comes from BouncyCastle (already a dependency of SIPSorcery);
/// AES-GCM, HMAC and SHA-256 come from .NET.
/// </summary>
/// <remarks>
/// @link https://noiseprotocol.org/noise.html
/// @link https://github.com/Sendspin/spec/blob/main/connection.md#encryption
/// </remarks>
public static class NoiseCrypto
{
    /// <summary>Size in bytes of X25519 keys, symmetric keys, hashes and PSKs.</summary>
    public const int KeySize = 32;

    /// <summary>Size in bytes of the AES-GCM authentication tag appended to every ciphertext.</summary>
    public const int TagSize = 16;

    /// <summary>Generates a fresh X25519 key pair.</summary>
    /// <returns>The random private key and its public key.</returns>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        byte[] privateKey = RandomNumberGenerator.GetBytes(KeySize);
        return (privateKey, PublicKey(privateKey));
    }

    /// <summary>Derives the X25519 public key for a private key.</summary>
    /// <param name="privateKey">The 32-byte private key.</param>
    /// <returns>The 32-byte public key.</returns>
    public static byte[] PublicKey(byte[] privateKey)
    {
        byte[] publicKey = new byte[KeySize];
        X25519.ScalarMultBase(privateKey, 0, publicKey, 0);
        return publicKey;
    }

    /// <summary>X25519 Diffie-Hellman between a local private key and a peer's public key.</summary>
    /// <param name="privateKey">The local private key.</param>
    /// <param name="peerPublicKey">The peer's public key.</param>
    /// <returns>The 32-byte shared secret.</returns>
    /// <exception cref="CryptographicException">The peer key produced a degenerate shared secret.</exception>
    public static byte[] Dh(byte[] privateKey, byte[] peerPublicKey)
    {
        byte[] shared = new byte[KeySize];
        // CalculateAgreement returns false for a small-order / all-zero result (RFC 7748 6.1); reject it.
        if (!X25519.CalculateAgreement(privateKey, 0, peerPublicKey, 0, shared, 0))
        {
            throw new CryptographicException("X25519 produced a degenerate shared secret");
        }
        return shared;
    }

    /// <summary>SHA-256 of one input.</summary>
    /// <param name="data">The bytes to hash.</param>
    /// <returns>The 32-byte digest.</returns>
    public static byte[] Hash(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    /// <summary>SHA-256 of two inputs concatenated, without copying them together.</summary>
    /// <param name="first">The leading bytes.</param>
    /// <param name="second">The trailing bytes.</param>
    /// <returns>The 32-byte digest.</returns>
    public static byte[] Hash(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(first);
        sha.AppendData(second);
        return sha.GetHashAndReset();
    }

    /// <summary>AES-GCM with the Noise nonce layout for AESGCM: 4 zero bytes then the 64-bit counter big-endian.</summary>
    /// <param name="key">The 32-byte cipher key.</param>
    /// <param name="counter">The nonce counter.</param>
    /// <param name="ad">Associated data authenticated with the message.</param>
    /// <param name="plaintext">The bytes to encrypt.</param>
    /// <returns>The ciphertext followed by the authentication tag.</returns>
    public static byte[] Encrypt(byte[] key, ulong counter, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        byte[] output = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, TagSize), ad);
        return output;
    }

    /// <summary>Reverses <see cref="Encrypt"/>, checking the authentication tag.</summary>
    /// <param name="key">The 32-byte cipher key.</param>
    /// <param name="counter">The nonce counter the message was encrypted with.</param>
    /// <param name="ad">Associated data the message was authenticated with.</param>
    /// <param name="ciphertext">The ciphertext followed by its tag.</param>
    /// <returns>The decrypted plaintext.</returns>
    /// <exception cref="CryptographicException">The ciphertext is shorter than a tag or fails authentication.</exception>
    public static byte[] Decrypt(byte[] key, ulong counter, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagSize) throw new CryptographicException("Ciphertext shorter than the tag");
        Span<byte> nonce = stackalloc byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        byte[] output = new byte[ciphertext.Length - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext[..output.Length], ciphertext[output.Length..], output, ad);
        return output;
    }

    /// <summary>Noise HKDF (section 4.3): two or three 32-byte outputs from the chaining key and input key material.</summary>
    /// <param name="chainingKey">The current chaining key, used as the HMAC key.</param>
    /// <param name="inputKeyMaterial">The key material to mix in.</param>
    /// <param name="outputs">How many outputs to derive: 2 or 3.</param>
    /// <returns>The derived outputs in order.</returns>
    public static byte[][] Hkdf(byte[] chainingKey, byte[] inputKeyMaterial, int outputs)
    {
        byte[] tempKey = HMACSHA256.HashData(chainingKey, inputKeyMaterial);
        byte[] o1 = HMACSHA256.HashData(tempKey, new byte[] { 1 });
        byte[] o2 = HMACSHA256.HashData(tempKey, (byte[])[.. o1, 2]);
        if (outputs == 2) return [o1, o2];
        byte[] o3 = HMACSHA256.HashData(tempKey, (byte[])[.. o2, 3]);
        return [o1, o2, o3];
    }
}

/// <summary>Noise CipherState (section 5.1): a key and a nonce counter; passes data through until it has a key.</summary>
public sealed class CipherState
{
    private byte[]? key;
    private ulong   nonce;

    /// <summary>Whether a key is set, so data is encrypted instead of passed through.</summary>
    public bool HasKey => key is not null;

    /// <summary>Sets the key (or clears it with <see langword="null"/>) and resets the nonce counter.</summary>
    /// <param name="newKey">The 32-byte key, or <see langword="null"/> for pass-through.</param>
    public void InitializeKey(byte[]? newKey)
    {
        key   = newKey;
        nonce = 0;
    }

    /// <summary>Encrypts with the next nonce, or returns a copy of the plaintext when there is no key.</summary>
    /// <param name="ad">Associated data authenticated with the message.</param>
    /// <param name="plaintext">The bytes to encrypt.</param>
    /// <returns>The ciphertext with its tag, or the plaintext copy.</returns>
    public byte[] EncryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        if (key is null) return plaintext.ToArray();
        return NoiseCrypto.Encrypt(key, nonce++, ad, plaintext);
    }

    /// <summary>Decrypts with the current nonce and advances it only on success, or returns a copy when there is no key.</summary>
    /// <param name="ad">Associated data the message was authenticated with.</param>
    /// <param name="ciphertext">The ciphertext with its tag.</param>
    /// <returns>The plaintext.</returns>
    public byte[] DecryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (key is null) return ciphertext.ToArray();
        // A failed tag leaves the counter untouched.
        byte[] plaintext = NoiseCrypto.Decrypt(key, nonce, ad, ciphertext);
        nonce++;
        return plaintext;
    }
}

/// <summary>Noise SymmetricState (section 5.2): chaining key, handshake hash and the handshake cipher.</summary>
public sealed class SymmetricState
{
    private byte[] chainingKey = [];

    /// <summary>The running handshake hash h.</summary>
    public byte[]      Hash   { get; private set; } = [];

    /// <summary>The cipher used for handshake payloads.</summary>
    public CipherState Cipher { get; } = new();

    /// <summary>Initializes h and the chaining key from the protocol name, with no cipher key.</summary>
    /// <param name="protocolName">The full Noise protocol name.</param>
    public SymmetricState(string protocolName)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(protocolName);
        if (name.Length <= 32)
        {
            Hash = new byte[32];
            name.CopyTo(Hash, 0);
        }
        else
        {
            Hash = NoiseCrypto.Hash(name);
        }
        chainingKey = Hash;
        Cipher.InitializeKey(null);
    }

    /// <summary>Mixes data into the handshake hash.</summary>
    /// <param name="data">The bytes to mix in.</param>
    public void MixHash(ReadOnlySpan<byte> data) => Hash = NoiseCrypto.Hash(Hash, data);

    /// <summary>Mixes key material into the chaining key and rekeys the handshake cipher.</summary>
    /// <param name="inputKeyMaterial">The key material to mix in.</param>
    public void MixKey(byte[] inputKeyMaterial)
    {
        byte[][] outputs = NoiseCrypto.Hkdf(chainingKey, inputKeyMaterial, 2);
        chainingKey = outputs[0];
        Cipher.InitializeKey(outputs[1]);
    }

    /// <summary>Mixes key material into the chaining key, the handshake hash and the cipher key (used for the PSK).</summary>
    /// <param name="inputKeyMaterial">The key material to mix in.</param>
    public void MixKeyAndHash(byte[] inputKeyMaterial)
    {
        byte[][] outputs = NoiseCrypto.Hkdf(chainingKey, inputKeyMaterial, 3);
        chainingKey = outputs[0];
        MixHash(outputs[1]);
        Cipher.InitializeKey(outputs[2]);
    }

    /// <summary>Encrypts a handshake payload with h as associated data, then mixes the ciphertext into h.</summary>
    /// <param name="plaintext">The payload to encrypt.</param>
    /// <returns>The ciphertext.</returns>
    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        byte[] ciphertext = Cipher.EncryptWithAd(Hash, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    /// <summary>Decrypts a handshake payload with h as associated data, then mixes the ciphertext into h.</summary>
    /// <param name="ciphertext">The ciphertext to decrypt.</param>
    /// <returns>The payload.</returns>
    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        byte[] plaintext = Cipher.DecryptWithAd(Hash, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    /// <summary>Transport keys: first for initiator to responder, second for responder to initiator.</summary>
    /// <returns>The two transport ciphers.</returns>
    public (CipherState InitiatorToResponder, CipherState ResponderToInitiator) Split()
    {
        byte[][] outputs = NoiseCrypto.Hkdf(chainingKey, [], 2);
        var first   = new CipherState();
        var second  = new CipherState();
        first.InitializeKey(outputs[0]);
        second.InitializeKey(outputs[1]);
        return (first, second);
    }
}

/// <summary>
/// Noise handshake for the KKpsk2 pattern:
///   -> s   (pre-message: initiator static)
///   &lt;- s   (pre-message: responder static)
///   -> e, es, ss
///   &lt;- e, ee, se, psk
/// In Sendspin the server is the initiator and this client the responder. The
/// PSK is only needed when message 2 is written, which is why it can be picked
/// after reading message 1 (whose payload names it).
/// </summary>
public sealed class HandshakeState
{
    /// <summary>The Noise protocol name Sendspin uses, mixed into the initial handshake hash.</summary>
    public const string ProtocolName = "Noise_KKpsk2_25519_AESGCM_SHA256";

    private readonly SymmetricState symmetric = new(ProtocolName);
    private readonly bool   initiator;
    private readonly byte[] staticPrivate;
    private readonly byte[] staticPublic;
    private readonly byte[] remoteStatic;
    private byte[]? ephemeralPrivate;
    private byte[]? ephemeralPublic;
    private byte[]? remoteEphemeral;
    private byte[]? psk;

    /// <summary>The running handshake hash h; after the handshake it is the prologue of any re-handshake.</summary>
    public byte[] HandshakeHash => symmetric.Hash;

    /// <summary>Starts a handshake: mixes in the prologue and both static keys as the pattern's pre-messages.</summary>
    /// <param name="initiator">Whether this side sends message 1.</param>
    /// <param name="prologue">Bytes both sides must agree on (the cleartext init messages, or the previous handshake hash).</param>
    /// <param name="staticPrivate">This side's static private key.</param>
    /// <param name="staticPublic">This side's static public key.</param>
    /// <param name="remoteStatic">The peer's static public key.</param>
    /// <param name="psk">The pre-shared key, when already known.</param>
    public HandshakeState(bool initiator, ReadOnlySpan<byte> prologue, byte[] staticPrivate, byte[] staticPublic, byte[] remoteStatic, byte[]? psk = null)
    {
        this.initiator     = initiator;
        this.staticPrivate = staticPrivate;
        this.staticPublic  = staticPublic;
        this.remoteStatic  = remoteStatic;
        this.psk           = psk;

        symmetric.MixHash(prologue);
        // Pre-messages: the initiator's static key, then the responder's.
        symmetric.MixHash(initiator ? staticPublic : remoteStatic);
        symmetric.MixHash(initiator ? remoteStatic : staticPublic);
    }

    /// <summary>Sets the pre-shared key once message 1 has named it.</summary>
    /// <param name="value">The 32-byte PSK.</param>
    public void SetPsk(byte[] value) => psk = value;

    /// <summary>Responder side: consume message 1 (e, es, ss) and return its decrypted payload.</summary>
    /// <param name="message">The raw message 1 bytes.</param>
    /// <returns>The decrypted payload.</returns>
    public byte[] ReadMessage1(ReadOnlySpan<byte> message)
    {
        if (initiator) throw new InvalidOperationException("Initiators write message 1");
        if (message.Length < NoiseCrypto.KeySize + NoiseCrypto.TagSize) throw new CryptographicException("Handshake message 1 too short");

        remoteEphemeral = message[..NoiseCrypto.KeySize].ToArray();
        symmetric.MixHash(remoteEphemeral);
        // PSK mode: ephemerals are mixed into the key too.
        symmetric.MixKey(remoteEphemeral);
        // DH es, seen from the responder.
        symmetric.MixKey(NoiseCrypto.Dh(staticPrivate, remoteEphemeral));
        // DH ss.
        symmetric.MixKey(NoiseCrypto.Dh(staticPrivate, remoteStatic));
        return symmetric.DecryptAndHash(message[NoiseCrypto.KeySize..]);
    }

    /// <summary>Responder side: produce message 2 (e, ee, se, psk) carrying the payload. Requires the PSK.</summary>
    /// <param name="payload">The payload to encrypt into the message.</param>
    /// <returns>The raw message 2 bytes.</returns>
    public byte[] WriteMessage2(ReadOnlySpan<byte> payload)
    {
        if (initiator) throw new InvalidOperationException("Initiators read message 2");
        if (psk is null) throw new InvalidOperationException("PSK not set");
        if (remoteEphemeral is null) throw new InvalidOperationException("Message 1 not read");

        (ephemeralPrivate, ephemeralPublic) = NoiseCrypto.GenerateKeyPair();
        symmetric.MixHash(ephemeralPublic);
        symmetric.MixKey(ephemeralPublic);
        // DH ee.
        symmetric.MixKey(NoiseCrypto.Dh(ephemeralPrivate, remoteEphemeral));
        // DH se, seen from the responder.
        symmetric.MixKey(NoiseCrypto.Dh(ephemeralPrivate, remoteStatic));
        // The psk2 modifier.
        symmetric.MixKeyAndHash(psk);
        byte[] encrypted = symmetric.EncryptAndHash(payload);
        return [.. ephemeralPublic, .. encrypted];
    }

    /// <summary>Initiator side (used by the self-check to exercise both halves): produce message 1.</summary>
    /// <param name="payload">The payload to encrypt into the message.</param>
    /// <returns>The raw message 1 bytes.</returns>
    public byte[] WriteMessage1(ReadOnlySpan<byte> payload)
    {
        if (!initiator) throw new InvalidOperationException("Responders read message 1");
        (ephemeralPrivate, ephemeralPublic) = NoiseCrypto.GenerateKeyPair();
        symmetric.MixHash(ephemeralPublic);
        symmetric.MixKey(ephemeralPublic);
        // DH es, seen from the initiator.
        symmetric.MixKey(NoiseCrypto.Dh(ephemeralPrivate, remoteStatic));
        // DH ss.
        symmetric.MixKey(NoiseCrypto.Dh(staticPrivate, remoteStatic));
        byte[] encrypted = symmetric.EncryptAndHash(payload);
        return [.. ephemeralPublic, .. encrypted];
    }

    /// <summary>Initiator side (self-check): consume message 2 and return its payload.</summary>
    /// <param name="message">The raw message 2 bytes.</param>
    /// <returns>The decrypted payload.</returns>
    public byte[] ReadMessage2(ReadOnlySpan<byte> message)
    {
        if (!initiator) throw new InvalidOperationException("Responders write message 2");
        if (psk is null) throw new InvalidOperationException("PSK not set");
        if (ephemeralPrivate is null) throw new InvalidOperationException("Message 1 not written");

        remoteEphemeral = message[..NoiseCrypto.KeySize].ToArray();
        symmetric.MixHash(remoteEphemeral);
        symmetric.MixKey(remoteEphemeral);
        // DH ee.
        symmetric.MixKey(NoiseCrypto.Dh(ephemeralPrivate, remoteEphemeral));
        // DH se, seen from the initiator.
        symmetric.MixKey(NoiseCrypto.Dh(staticPrivate, remoteEphemeral));
        symmetric.MixKeyAndHash(psk);
        return symmetric.DecryptAndHash(message[NoiseCrypto.KeySize..]);
    }

    /// <summary>Transport-mode session for this side once both messages are exchanged.</summary>
    /// <returns>A session whose send and receive ciphers match this side's role.</returns>
    public NoiseSession Split()
    {
        (CipherState toResponder, CipherState toInitiator) = symmetric.Split();
        return initiator ? new NoiseSession(toResponder, toInitiator) : new NoiseSession(toInitiator, toResponder);
    }
}

/// <summary>Transport mode: one cipher per direction, empty associated data, counters advance per message.</summary>
/// <param name="send">The cipher for outgoing messages.</param>
/// <param name="receive">The cipher for incoming messages.</param>
public sealed class NoiseSession(CipherState send, CipherState receive)
{
    /// <summary>Encrypts one outgoing transport message.</summary>
    /// <param name="plaintext">The message to encrypt.</param>
    /// <returns>The ciphertext with its tag.</returns>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)  => send.EncryptWithAd([], plaintext);

    /// <summary>Decrypts one incoming transport message.</summary>
    /// <param name="ciphertext">The ciphertext with its tag.</param>
    /// <returns>The plaintext.</returns>
    /// <exception cref="CryptographicException">The message fails authentication.</exception>
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => receive.DecryptWithAd([], ciphertext);
}
