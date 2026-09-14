using System.Text.Json;
using Windows.Security.Credentials;

namespace MusicAssistant.Api;

/// <summary>
/// Persisted client settings.
///
/// Non-secret settings (server address, last active player) live in a JSON
/// file under %LOCALAPPDATA%. The access token is stored in the Windows
/// Credential Manager via PasswordVault, never on disk in plain text.
/// </summary>
public sealed class Session
{
    private const string VaultResource = "MusicAssistant";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicAssistant", "settings.json");

    public string? ServerAddress  { get; set; }
    public string? ActivePlayerId { get; set; }

    /// <summary>Music Assistant Remote ID (server certificate fingerprint) used when the local address is unreachable.</summary>
    public string? RemoteId       { get; set; }

    /// <summary>Use this PC as a Music Assistant player (Sendspin).</summary>
    public bool    SpeakerEnabled  { get; set; }
    public string? SpeakerClientId { get; set; }

    /// <summary>Closing the window hides the app and keeps it running, instead of quitting. Default on.</summary>
    public bool    RunInBackground { get; set; } = true;

    /// <summary>Show the notification-area (tray) icon and its menu. Default on. When off, the Quit item appears in the sidebar.</summary>
    public bool    ShowTrayIcon    { get; set; } = true;

    // Window placement (physical pixels)
    public int  WindowX         { get; set; }
    public int  WindowY         { get; set; }
    public int  WindowWidth     { get; set; }
    public int  WindowHeight    { get; set; }
    public bool WindowMaximized { get; set; }

    // Settings File

    public static Session Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<Session>(File.ReadAllText(SettingsPath)) ?? new Session();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new Session();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Token Vault

    /// <summary>One token per server; the vault entry is keyed by the local address, or the Remote ID when there is none.</summary>
    private string? TokenKey => !string.IsNullOrEmpty(ServerAddress) ? ServerAddress : !string.IsNullOrEmpty(RemoteId) ? "remote:" + RemoteId : null;

    public string? GetToken()
    {
        if (TokenKey is not { } key) return null;
        try
        {
            var credential = new PasswordVault().Retrieve(VaultResource, key);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            // PasswordVault throws when no credential exists for this server
            return null;
        }
    }

    public void SetToken(string token)
    {
        if (TokenKey is not { } key) return;
        ClearToken();
        new PasswordVault().Add(new PasswordCredential(VaultResource, key, token));
    }

    public void ClearToken()
    {
        if (TokenKey is not { } key) return;
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(VaultResource, key));
        }
        catch (Exception)
        {
            // nothing stored
        }
    }
}
