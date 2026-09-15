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

    /// <summary>Local server address the user entered, or null when only remote access is set up.</summary>
    public string? ServerAddress  { get; set; }

    /// <summary>Player id the app currently controls, remembered across launches.</summary>
    public string? ActivePlayerId { get; set; }

    /// <summary>Music Assistant Remote ID (server certificate fingerprint) used when the local address is unreachable.</summary>
    public string? RemoteId       { get; set; }

    /// <summary>Use this PC as a Music Assistant player (Sendspin).</summary>
    public bool    SpeakerEnabled  { get; set; }

    /// <summary>Sendspin client id of this PC's speaker, which is also its player id on the server.</summary>
    public string? SpeakerClientId { get; set; }

    /// <summary>server_id of the last Sendspin server this PC's speaker played for, which wins ties between idle servers.</summary>
    public string? SpeakerLastPlaybackServerId { get; set; }

    /// <summary>Closing the window hides the app and keeps it running, instead of quitting. Default on.</summary>
    public bool    RunInBackground { get; set; } = true;

    /// <summary>Show the notification-area (tray) icon and its menu. Default on. When off, the Quit item appears in the sidebar.</summary>
    public bool    ShowTrayIcon    { get; set; } = true;

    // Window placement (physical pixels).

    /// <summary>Left edge of the main window.</summary>
    public int  WindowX         { get; set; }

    /// <summary>Top edge of the main window.</summary>
    public int  WindowY         { get; set; }

    /// <summary>Width of the main window.</summary>
    public int  WindowWidth     { get; set; }

    /// <summary>Height of the main window.</summary>
    public int  WindowHeight    { get; set; }

    /// <summary>True when the main window was maximized.</summary>
    public bool WindowMaximized { get; set; }

    // Settings File.

    /// <summary>Read the settings file, falling back to defaults when it is missing or unreadable.</summary>
    /// <returns>The saved settings, or a new instance with defaults.</returns>
    public static Session Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<Session>(File.ReadAllText(SettingsPath)) ?? new Session();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable settings file falls back to defaults below.
        }
        return new Session();
    }

    /// <summary>Write the non-secret settings to the settings file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Token Vault.

    /// <summary>One token per server; the vault entry is keyed by the local address, or the Remote ID when there is none.</summary>
    private string? TokenKey => !string.IsNullOrEmpty(ServerAddress) ? ServerAddress : !string.IsNullOrEmpty(RemoteId) ? $"remote:{RemoteId}" : null;

    /// <summary>Read the stored access token for the current server from the Windows Credential Manager.</summary>
    /// <returns>The token, or null when none is stored or no server is set.</returns>
    public string? GetToken()
    {
        if (TokenKey is not { } key) return null;
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(VaultResource, key);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // PasswordVault throws when no credential exists for this server.
            return null;
        }
    }

    /// <summary>Store the access token for the current server, replacing any earlier one.</summary>
    /// <param name="token">The access token the server issued.</param>
    public void SetToken(string token)
    {
        if (TokenKey is not { } key) return;
        ClearToken();
        new PasswordVault().Add(new PasswordCredential(VaultResource, key, token));
    }

    /// <summary>Remove the stored access token for the current server, if there is one.</summary>
    public void ClearToken()
    {
        if (TokenKey is not { } key) return;
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(VaultResource, key));
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Nothing stored.
        }
    }
}
