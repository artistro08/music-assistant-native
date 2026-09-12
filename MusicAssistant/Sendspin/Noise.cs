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
    public const int KeySize = 32;
    public const int TagSize = 16;

    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var privateKey = RandomNumberGenerator.GetBytes(KeySize);
        return (privateKey, PublicKey(privateKey));
    }

    public static byte[] PublicKey(byte[] privateKey)
    {
        var publicKey = new byte[KeySize];
        X25519.ScalarMultBase(privateKey, 0, publicKey, 0);
        return publicKey;
    }

    public static byte[] Dh(byte[] privateKey, byte[] peerPublicKey)
    {
        var shared = new byte[KeySize];
        // CalculateAgreement returns false for a small-order / all-zero result (RFC 7748 6.1); reject it
        if (!X25519.CalculateAgreement(privateKey, 0, peerPublicKey, 0, shared, 0))
        {
            throw new CryptographicException("X25519 produced a degenerate shared secret");
        }
        return shared;
    }

    public static byte[] Hash(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] Hash(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(first);
        sha.AppendData(second);
        return sha.GetHashAndReset();
    }

    /// <summary>AES-GCM with the Noise nonce layout for AESGCM: 4 zero bytes then the 64-bit counter big-endian.</summary>
    public static byte[] Encrypt(byte[] key, ulong counter, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        var output = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, TagSize), ad);
        return output;
    }

    public static byte[] Decrypt(byte[] key, ulong counter, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagSize) throw new CryptographicException("Ciphertext shorter than the tag");
        Span<byte> nonce = stackalloc byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        var output = new byte[ciphertext.Length - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext[..output.Length], ciphertext[output.Length..], output, ad);
        return output;
    }

    /// <summary>Noise HKDF (section 4.3): two or three 32-byte outputs from the chaining key and input key material.</summary>
    public static byte[][] Hkdf(byte[] chainingKey, byte[] inputKeyMaterial, int outputs)
    {
        var tempKey = HMACSHA256.HashData(chainingKey, inputKeyMaterial);
        var o1 = HMACSHA256.HashData(tempKey, new byte[] { 1 });
        var o2 = HMACSHA256.HashData(tempKey, (byte[])[.. o1, 2]);
        if (outputs == 2) return [o1, o2];
        var o3 = HMACSHA256.HashData(tempKey, (byte[])[.. o2, 3]);
        return [o1, o2, o3];
    }
}

/// <summary>Noise CipherState (section 5.1): a key and a nonce counter; passes data through until it has a key.</summary>
public sealed class CipherState
{
    private byte[]? key;
    private ulong   nonce;

    public bool HasKey => key is not null;

    public void InitializeKey(byte[]? newKey)
    {
        key   = newKey;
        nonce = 0;
    }

    public byte[] EncryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        if (key is null) return plaintext.ToArray();
        return NoiseCrypto.Encrypt(key, nonce++, ad, plaintext);
    }

    public byte[] DecryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (key is null) return ciphertext.ToArray();
        var plaintext = NoiseCrypto.Decrypt(key, nonce, ad, ciphertext);   // a failed tag leaves the counter untouched
        nonce++;
        return plaintext;
    }
}

/// <summary>Noise SymmetricState (section 5.2): chaining key, handshake hash and the handshake cipher.</summary>
public sealed class SymmetricState
{
    private byte[] chainingKey = [];

    public byte[]      Hash   { get; private set; } = [];
    public CipherState Cipher { get; } = new();

    public SymmetricState(string protocolName)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(protocolName);
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

    public void MixHash(ReadOnlySpan<byte> data) => Hash = NoiseCrypto.Hash(Hash, data);

    public void MixKey(byte[] inputKeyMaterial)
    {
        var outputs = NoiseCrypto.Hkdf(chainingKey, inputKeyMaterial, 2);
        chainingKey = outputs[0];
        Cipher.InitializeKey(outputs[1]);
    }

    public void MixKeyAndHash(byte[] inputKeyMaterial)
    {
        var outputs = NoiseCrypto.Hkdf(chainingKey, inputKeyMaterial, 3);
        chainingKey = outputs[0];
        MixHash(outputs[1]);
        Cipher.InitializeKey(outputs[2]);
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = Cipher.EncryptWithAd(Hash, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var plaintext = Cipher.DecryptWithAd(Hash, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    /// <summary>Transport keys: first for initiator to responder, second for responder to initiator.</summary>
    public (CipherState InitiatorToResponder, CipherState ResponderToInitiator) Split()
    {
        var outputs = NoiseCrypto.Hkdf(chainingKey, [], 2);
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
///   <- s   (pre-message: responder static)
///   -> e, es, ss
///   <- e, ee, se, psk
/// In Sendspin the server is the initiator and this client the responder. The
/// PSK is only needed when message 2 is written, which is why it can be picked
/// after reading message 1 (whose payload names it).
/// </summary>
public sealed class HandshakeState
{
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

    public HandshakeState(bool initiator, ReadOnlySpan<byte> prologue, byte[] staticPrivate, byte[] staticPublic, byte[] remoteStatic, byte[]? psk = null)
    {
        this.initiator     = initiator;
        this.staticPrivate = staticPrivate;
        this.staticPublic  = staticPublic;
        this.remoteStatic  = remoteStatic;
        this.psk           = psk;

        symmetric.MixHash(prologue);
        // Pre-messages: the initiator's static key, then the responder's
        symmetric.MixHash(initiator ? staticPublic : remoteStatic);
        symmetric.MixHash(initiator ? remoteStatic : staticPublic);
    }

    public void SetPsk(byte[] value) => psk = value;

    /// <summary>Responder side: consume message 1 (e, es, ss) and return its decrypted payload.</summary>
    public byte[] ReadMessage1(ReadOnlySpan<byte> message)
    {
        if (initiator) throw new InvalidOperationException("Initiators write message 1");
        if (message.Length < NoiseCrypto.KeySize + NoiseCrypto.TagSize) throw new CryptographicException("Handshake message 1 too short");

        remoteEphemeral = message[..NoiseCrypto.KeySize].ToArray();
        symmetric.MixHash(remoteEphemeral);
        symmetric.MixKey(remoteEphemeral);                                        // PSK mode: ephemerals are mixed into the key too
        symmetric.MixKey(NoiseCrypto.Dh(staticPrivate, remoteEphemeral));         // es, seen from the responder
        symmetric.MixKey(NoiseCrypto.Dh(staticPrivate, remoteStatic));            // ss
        return symmetric.DecryptAndHash(message[NoiseCrypto.KeySize..]);
    }

    /// <summary>Responder side: produce message 2 (e, ee, se, psk) carrying the payload. Requires the PSK.</summary>
    public byte[] WriteMessage2(ReadOnlySpan<byte> payload)
    {
        if (initiator) throw new InvalidOperationException("Initiators read message 2");
        if (psk is null) throw new InvalidOperationException("PSK not set");
        if (remoteEphemeral is null) throw new InvalidOperationException("Message 1 not read");

        (ephemeralPrivate, ephemeralPublic) = NoiseCrypto.GenerateKeyPair();
        symmetric.MixHash(ephemeralPublic);
        symmetric.MixKey(ephemeralPublic);
        symmetric.MixKey(NoiseCrypto.Dh(ephemeralPrivate, remoteEphemeral));      // ee
        symmetric.MixKey(NoiseCrypto.Dh(ephemeralPrivate, remoteStatic));         // se, seen from the responder
        symmetric.MixKeyAndHash(psk);                                             // psk2
        var encrypted = symmetric.EncryptAndHash(payload);
        return [.. ephemeralPublic, .. encrypted];
    }

    /// <summary>Initiator side (used by the self-check to exercise both halves): produce message 1.</summary>
    public byte[] WriteMessage1(ReadOnlySpan<byte> payload)
    {
        if (!initiator) throw new InvalidOperationException("Responders read message 1");
        (ephemeralPrivate, ephemeralPublic) = NoiseCrypto.GenerateKeyPair();
        symmetric.MixHash(ephemeralPublic);
        symmetric.MixKey(ephemeralPublic);
        symmetric.MixKey(NoiseCrypto.Dh(ephemeralPrivate, remoteStatic));         // es, seen from the initiator
        symmetric.MixKey(NoiseCrypto.Dh(staticPrivate, remoteStatic));            // ss
        var encrypted = symmetric.EncryptAndHash(payload);
        return [.. ephemeralPublic, .. encrypted];
    }

    /// <summary>Initiator side (self-check): consume message 2 and return its payload.</summary>
    public byte[] ReadMessage2(ReadOnlySpan<byte> message)
    {
        if (!initiator) throw new InvalidOperationException("Responders write message 2");
        if (psk is null) throw new InvalidOperationException("PSK not set");
        if (ephemeralPrivate is null) throw new InvalidOperationException("Message 1 not written");

        remoteEphemeral = message[..NoiseCrypto.KeySize].ToArray();
        symmetric.MixHash(remoteEphemeral);
        symmetric.MixKey(remoteEphemeral);
        symmetric.MixKey(NoiseCrypto.Dh(ephemeralPrivate, remoteEphemeral));      // ee
        symmetric.MixKey(NoiseCrypto.Dh(staticPrivate, remoteEphemeral));         // se, seen from the initiator
        symmetric.MixKeyAndHash(psk);
        return symmetric.DecryptAndHash(message[NoiseCrypto.KeySize..]);
    }

    /// <summary>Transport-mode session for this side once both messages are exchanged.</summary>
    public NoiseSession Split()
    {
        var (toResponder, toInitiator) = symmetric.Split();
        return initiator ? new NoiseSession(toResponder, toInitiator) : new NoiseSession(toInitiator, toResponder);
    }
}

/// <summary>Transport mode: one cipher per direction, empty associated data, counters advance per message.</summary>
public sealed class NoiseSession(CipherState send, CipherState receive)
{
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)  => send.EncryptWithAd([], plaintext);
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => receive.DecryptWithAd([], ciphertext);
}
