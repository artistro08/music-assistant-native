using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicAssistant.Api;

namespace MusicAssistant.Pages;

/// <summary>Connection details, remote access, sign-out and a link to the server's web interface for admin tasks.</summary>
public sealed partial class SettingsPage : Page
{
    private bool loadingRemote;

    /// <summary>Creates the settings page, fills in the connection and app details, and starts loading the server's remote access state.</summary>
    public SettingsPage()
    {
        InitializeComponent();

        ServerInfo? info = App.Client.ServerInfo;
        User? user = App.Client.CurrentUser;

        ServerText.Text  = App.Settings.ServerAddress ?? "(remote only)";
        VersionText.Text = info is null ? "" : $"{info.Name ?? "Music Assistant"} • server {info.ServerVersion} • schema {info.SchemaVersion}";
        UserText.Text    = user is null ? "" : $"{user.DisplayName ?? user.Username} ({user.Role})";
        AppVersionText.Text = $"Music Assistant for Windows {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        ConnectionText.Text = App.Client.IsRemote ? "Currently connected remotely through the relay." : "Currently connected over your local network.";
        RemoteIdBox.Text    = App.Settings.RemoteId ?? "";

        if (!App.Client.IsRemote && Uri.TryCreate(App.Settings.ServerAddress, UriKind.Absolute, out Uri? uri)) WebLink.NavigateUri = uri;
        else WebLink.Visibility = Visibility.Collapsed;

        _ = LoadRemoteAccessAsync();

        SpeakerSwitch.IsOn = App.Settings.SpeakerEnabled;
        App.Window.Speaker.Changed += RefreshSpeaker;
        Unloaded += (_, _) => App.Window.Speaker.Changed -= RefreshSpeaker;
        RefreshSpeaker();

        BackgroundSwitch.IsOn = App.Settings.RunInBackground;
        TraySwitch.IsOn       = App.Settings.ShowTrayIcon;
    }

    // =========================================================================
    // GENERAL
    // =========================================================================

    private void OnRunBackgroundToggled(object sender, RoutedEventArgs e)
    {
        if (BackgroundSwitch.IsOn == App.Settings.RunInBackground) return;
        App.Settings.RunInBackground = BackgroundSwitch.IsOn;
        App.Settings.Save();
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        if (TraySwitch.IsOn == App.Settings.ShowTrayIcon) return;
        App.Settings.ShowTrayIcon = TraySwitch.IsOn;
        App.Settings.Save();
        App.Window.ApplyWindowSettings();
    }

    // =========================================================================
    // SPEAKER
    // =========================================================================

    private void RefreshSpeaker() => SpeakerStatusText.Text = App.Window.Speaker.Status;

    private async void OnSpeakerToggled(object sender, RoutedEventArgs e)
    {
        if (SpeakerSwitch.IsOn == App.Settings.SpeakerEnabled) return;
        App.Settings.SpeakerEnabled = SpeakerSwitch.IsOn;
        App.Settings.Save();
        await App.Window.Speaker.SyncAsync();
    }

    // =========================================================================
    // REMOTE ACCESS
    // =========================================================================

    /// <summary>Server-side state is admin only; other users just see the local Remote ID field.</summary>
    private async Task LoadRemoteAccessAsync()
    {
        if (App.Client.CurrentUser?.Role != "admin") return;
        try
        {
            Show(await App.Client.GetRemoteAccessInfoAsync());
        }
        catch (ApiException)
        {
            ServerRemotePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Show(RemoteAccessInfo info)
    {
        loadingRemote = true;
        ServerRemotePanel.Visibility = Visibility.Visible;
        RemoteEnabledSwitch.IsOn     = info.Enabled;
        ServerRemoteIdText.Text      = info.Enabled ? info.RemoteIdDisplay : "";
        CopyRemoteIdButton.IsEnabled = info.Enabled;
        RemoteStatusText.Text = !info.Enabled
            ? "Off. Turn it on to reach this server from anywhere."
            : info.Connected
                ? (info.UsingHaCloud ? "Connected to the relay (using Home Assistant Cloud for the best route)." : "Connected to the relay.")
                : "Enabled, waiting for the relay connection…";
        loadingRemote = false;

        // Keep this PC in sync with the server so roaming works without copying.
        string? id = info.Enabled ? MassClient.NormalizeRemoteId(info.RemoteId) : null;
        if (id is not null && (id != App.Settings.RemoteId))
        {
            App.Settings.RemoteId = id;
            App.Settings.Save();
            RemoteIdBox.Text = id;
        }
    }

    private async void OnRemoteToggled(object sender, RoutedEventArgs e)
    {
        if (loadingRemote) return;
        try
        {
            Show(await App.Client.ConfigureRemoteAccessAsync(RemoteEnabledSwitch.IsOn));
        }
        catch (ApiException ex)
        {
            App.Window.ShowMessage(ex.Message);
            await LoadRemoteAccessAsync();
        }
    }

    private void OnCopyRemoteId(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ServerRemoteIdText.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        App.Window.ShowMessage("Remote ID copied.", InfoBarSeverity.Success);
    }

    private void OnRemoteIdEdited(object sender, RoutedEventArgs e)
    {
        string text = RemoteIdBox.Text.Trim();
        string? id   = MassClient.NormalizeRemoteId(text);
        if ((text.Length > 0) && id is null)
        {
            App.Window.ShowMessage("That Remote ID does not look right. It has 26 letters and digits.");
            return;
        }
        App.Settings.RemoteId = id;
        App.Settings.Save();
    }

    private async void OnSignOut(object sender, RoutedEventArgs e) => await App.Window.SignOutAsync();
}
