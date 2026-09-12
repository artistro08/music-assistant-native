using System.Security.Cryptography;
using System.Text.Json;
using Windows.Security.Credentials;

namespace MusicAssistant.Sendspin;

/// <summary>
/// This PC's Sendspin identity and pre-shared keys, kept in the Windows Credential Manager.
///
/// The identity is an X25519 key pair whose public key (base64url) is the
/// client_id the server knows this player by. The pairing PSK is the secret
/// the pairing token carries; a long-term PSK per server is the pairing record
/// a successful pairing leaves behind. The Sentinel PSK is the published
/// constant used before any pairing exists. Everything but the Sentinel is
/// secret and lives in PasswordVault, never in plain files.
/// </summary>
/// <remarks>
/// @link https://github.com/Sendspin/spec/blob/main/connection.md#pre-shared-key
/// @link https://github.com/Sendspin/spec/blob/main/pairing.md#pairing-psk-flow
/// </remarks>
public sealed class Identity
{
    private const string VaultResource   = "MusicAssistant.Sendspin";
    private const string IdentityEntry   = "identity";
    private const string PairingPskEntry = "pairing-psk";
    private const string RecordsEntry    = "pairing-records";

    /// <summary>SHA-256("sendspin-sentinel-psk-v1"): the PSK of every unpaired connection.</summary>
    public static readonly byte[] SentinelPsk = SHA256.HashData("sendspin-sentinel-psk-v1"u8);

    public enum PskCategory { Sentinel, Pairing, LongTerm }

    public sealed record PskEntry(byte[] Psk, string PskId, PskCategory Category, string? ServerId);

    private readonly PasswordVault vault = new();
    private readonly Dictionary<string, PskEntry> psks = new();   // by psk_id

    public byte[] PrivateKey { get; }
    public byte[] PublicKey  { get; }
    public string ClientId   => Base64Url.Encode(PublicKey);
    public byte[] PairingPsk { get; }

    public Identity()
    {
        // Static key pair
        var storedKey = Read(IdentityEntry);
        if (storedKey is { Length: NoiseCrypto.KeySize })
        {
            PrivateKey = storedKey;
        }
        else
        {
            (PrivateKey, _) = NoiseCrypto.GenerateKeyPair();
            Write(IdentityEntry, PrivateKey);
        }
        PublicKey = NoiseCrypto.PublicKey(PrivateKey);

        // Pairing PSK: long-lived, per device, minted once
        var storedPairing = Read(PairingPskEntry);
        if (storedPairing is { Length: NoiseCrypto.KeySize })
        {
            PairingPsk = storedPairing;
        }
        else
        {
            PairingPsk = RandomNumberGenerator.GetBytes(NoiseCrypto.KeySize);
            Write(PairingPskEntry, PairingPsk);
        }

        Add(new PskEntry(SentinelPsk, PskIdOf(SentinelPsk), PskCategory.Sentinel, null));
        Add(new PskEntry(PairingPsk,  PskIdOf(PairingPsk),  PskCategory.Pairing,  null));
        LoadRecords();
    }

    /// <summary>psk_id = base64url(SHA-256("sendspin-psk-id-v1" || PSK)), the same label for every category.</summary>
    public static string PskIdOf(byte[] psk) => Base64Url.Encode(NoiseCrypto.Hash("sendspin-psk-id-v1"u8, psk));

    public PskEntry? Lookup(string pskId) => psks.GetValueOrDefault(pskId);

    /// <summary>Version-0 pairing token: "SP:0" + base32(client key || pairing PSK) with every 2 written as 9.</summary>
    public string PairingToken => "SP:0" + Base32([.. PublicKey, .. PairingPsk]).Replace('2', '9');

    /// <summary>Persist the long-term PSK a completed pairing produced for a server, replacing an older record for it.</summary>
    public void AddPairingRecord(string serverId, byte[] longTermPsk)
    {
        foreach (var stale in psks.Values.Where(e => e.Category == PskCategory.LongTerm && e.ServerId == serverId).ToList())
        {
            psks.Remove(stale.PskId);
        }
        Add(new PskEntry(longTermPsk, PskIdOf(longTermPsk), PskCategory.LongTerm, serverId));
        SaveRecords();
    }

    /// <summary>Drop a pairing record (server/unpair).</summary>
    public void RemovePairingRecord(string pskId)
    {
        if (psks.Remove(pskId)) SaveRecords();
    }

    // Storage

    private void Add(PskEntry entry) => psks[entry.PskId] = entry;

    private sealed record StoredRecord(string ServerId, string Psk);

    private void LoadRecords()
    {
        var raw = Read(RecordsEntry);
        if (raw is null) return;
        try
        {
            foreach (var record in JsonSerializer.Deserialize<List<StoredRecord>>(raw) ?? [])
            {
                var psk = Base64Url.Decode(record.Psk);
                if (psk.Length == NoiseCrypto.KeySize) Add(new PskEntry(psk, PskIdOf(psk), PskCategory.LongTerm, record.ServerId));
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            // Unreadable records: start over; the server offers to pair again
            Write(RecordsEntry, "[]"u8.ToArray());
        }
    }

    private void SaveRecords()
    {
        var records = psks.Values
            .Where(e => e.Category == PskCategory.LongTerm && e.ServerId is not null)
            .Select(e => new StoredRecord(e.ServerId!, Base64Url.Encode(e.Psk)))
            .ToList();
        Write(RecordsEntry, JsonSerializer.SerializeToUtf8Bytes(records));
    }

    private byte[]? Read(string entry)
    {
        try
        {
            var credential = vault.Retrieve(VaultResource, entry);
            credential.RetrievePassword();
            return Convert.FromBase64String(credential.Password);
        }
        catch (Exception)   // not found, or an unreadable value
        {
            return null;
        }
    }

    private void Write(string entry, byte[] value)
    {
        try { vault.Remove(vault.Retrieve(VaultResource, entry)); } catch (Exception) { }
        vault.Add(new PasswordCredential(VaultResource, entry, Convert.ToBase64String(value)));
    }

    private static string Base32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new System.Text.StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits  += 8;
            while (bits >= 5)
            {
                output.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }
}
