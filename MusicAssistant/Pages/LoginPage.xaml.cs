using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicAssistant.Api;

namespace MusicAssistant.Pages;

/// <summary>
/// Sign-in page following the Music Assistant login flows:
/// builtin username/password, or Home Assistant OAuth in the system browser.
/// </summary>
public sealed partial class LoginPage : Page
{
    private CancellationTokenSource? oauthCts;

    /// <summary>Creates the sign-in page, prefilled with the saved server address and Remote ID.</summary>
    public LoginPage()
    {
        InitializeComponent();
        ServerBox.Text   = App.Settings.ServerAddress ?? "";
        RemoteIdBox.Text = App.Settings.RemoteId ?? "";
        OnTargetChanged(this, null!);
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string message && (message.Length > 0)) ShowError(message);
    }

    /// <inheritdoc/>
    protected override void OnNavigatedFrom(NavigationEventArgs e) => oauthCts?.Cancel();

    // =========================================================================
    // USERNAME / PASSWORD
    // =========================================================================

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnSignIn(sender, e);
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        string server   = ServerBox.Text.Trim();
        string? remoteId = MassClient.NormalizeRemoteId(RemoteIdBox.Text);
        string username = UsernameBox.Text.Trim();
        string password = PasswordBox.Password;

        if ((RemoteIdBox.Text.Trim().Length > 0) && remoteId is null)
        {
            ShowError("That Remote ID does not look right. It has 26 letters and digits, shown as 8-5-5-8 groups.");
            return;
        }
        if (((server.Length == 0) && remoteId is null) || (username.Length == 0) || (password.Length == 0))
        {
            ShowError("A server address or Remote ID, plus username and password, are required.");
            return;
        }

        SetBusy(true, remoteId is not null && (server.Length == 0) ? "Connecting through Music Assistant remote access…" : "Signing in…");
        try
        {
            await App.Window.LoginAsync(server, remoteId, username, password);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            ShowError(Describe(ex, server));
        }
        finally
        {
            SetBusy(false);
            PasswordBox.Password = "";
        }
    }

    // =========================================================================
    // HOME ASSISTANT
    // =========================================================================

    private async void OnSignInWithHomeAssistant(object sender, RoutedEventArgs e)
    {
        string server = ServerBox.Text.Trim();
        if (server.Length == 0)
        {
            ShowError("Enter your Music Assistant server address first.");
            return;
        }

        oauthCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        SetBusy(true, "Finish signing in with Home Assistant in your browser, then come back here.");
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            await App.Window.LoginWithHomeAssistantAsync(server, oauthCts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowError("Home Assistant sign-in was canceled or timed out.");
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            ShowError(Describe(ex, server));
        }
        finally
        {
            oauthCts.Dispose();
            oauthCts = null;
            CancelButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => oauthCts?.Cancel();

    /// <summary>Home Assistant sign-in needs the browser to reach the server, so it is local-only.</summary>
    private void OnTargetChanged(object sender, TextChangedEventArgs e)
    {
        HomeAssistantButton.IsEnabled = ServerBox.Text.Trim().Length > 0;
        ToolTipService.SetToolTip(HomeAssistantButton, HomeAssistantButton.IsEnabled ? null : "Enter the server address to sign in with Home Assistant. Remote-only sign-in uses username and password.");
    }

    // =========================================================================
    // UI STATE
    // =========================================================================

    private static string Describe(Exception ex, string server) => ex switch
    {
        ApiException { Code: ApiException.SetupRequired } => "This server has not finished setup. Open the web interface to create the first user.",
        ApiException api => api.Message,
        _ => $"Could not connect to {server}: {ex.Message}",
    };

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen  = true;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        SignInButton.IsEnabled          = !busy;
        HomeAssistantButton.IsEnabled   = !busy && (ServerBox.Text.Trim().Length > 0);
        RemoteIdBox.IsEnabled           = !busy;
        ServerBox.IsEnabled             = !busy;
        Busy.Visibility                 = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text                 = status ?? "";
        StatusText.Visibility           = busy && status is not null ? Visibility.Visible : Visibility.Collapsed;
        if (busy) ErrorBar.IsOpen = false;
    }
}
