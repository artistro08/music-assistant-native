using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using MusicAssistant.Api;
using MusicAssistant.Pages;
using MusicAssistant.Remote;

namespace MusicAssistant;

/// <summary>
/// Application shell: title bar, navigation, content frame, player bar.
///
/// Owns the connection life cycle. On launch it reconnects to the saved
/// server with the stored token; if that fails it shows the login page.
/// A dropped connection is retried with backoff and re-authenticated.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> NavPages = new()
    {
        ["home"]      = typeof(HomePage),
        ["search"]    = typeof(SearchPage),
        ["browse"]    = typeof(BrowsePage),
        ["artists"]   = typeof(LibraryPage),
        ["albums"]    = typeof(LibraryPage),
        ["tracks"]    = typeof(LibraryPage),
        ["playlists"] = typeof(LibraryPage),
        ["radios"]     = typeof(LibraryPage),
        ["audiobooks"] = typeof(LibraryPage),
        ["podcasts"]   = typeof(LibraryPage),
        ["genres"]     = typeof(LibraryPage),
    };

    private readonly TrayIcon tray;
    private int  reconnectAttempt;
    private bool exiting;
    private CancellationTokenSource? reconnectCts;   // one reconnect loop at a time; canceled by sign-out and manual sign-in

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            // Standard 32px caption buttons to match the 32px custom title bar
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
        }
        RestorePlacement();

        // App icon in the title bar / taskbar, and the tray icon with its menu
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        AppWindow.SetIcon(iconPath);
        tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), iconPath, ShowFromTray, ExitApp);
        App.StateChanged += UpdateTrayTip;

        // Closing the window hides to the tray; Exit in the tray menu really quits
        AppWindow.Closing += (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true;
            SavePlacement();
            AppWindow.Hide();
        };

        App.Client.Disconnected += error => DispatcherQueue.TryEnqueue(() => OnDisconnected(error));
        Closed += (_, _) => { SavePlacement(); speaker?.Stop(); tray.Dispose(); App.Client.Dispose(); };

        // Mouse back/forward buttons navigate the content frame (handledEventsToo so child controls cannot swallow them)
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);

        Png.ScheduleSnapshot(Root);
        _ = StartAsync();
    }

    // =========================================================================
    // TRAY
    // =========================================================================

    private void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    private void ExitApp()
    {
        exiting = true;
        Close();                      // Closed handler stops the speaker, removes the tray icon and its menu window, disconnects
        Application.Current.Exit();   // nothing else may keep the process alive
    }

    private void UpdateTrayTip()
    {
        var player = App.ActivePlayer;
        var item   = player is null ? null : App.Client.Queues.GetValueOrDefault(App.Client.QueueIdFor(player))?.CurrentItem;
        var title  = item?.Name ?? player?.CurrentMedia?.Title;
        tray.SetTip(title is null ? "Music Assistant" : $"Music Assistant\n{title}\n{player!.Name}");
    }

    // =========================================================================
    // WINDOW PLACEMENT
    // =========================================================================

    /// <summary>Restore the last size and position when it still lands on a connected display; otherwise use the default size.</summary>
    private void RestorePlacement()
    {
        var s = App.Settings;
        var rect = new Windows.Graphics.RectInt32(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight);

        var onScreen = s.WindowWidth >= 400 && s.WindowHeight >= 300
            && Microsoft.UI.Windowing.DisplayArea.GetFromRect(rect, Microsoft.UI.Windowing.DisplayAreaFallback.None) is { } area
            && rect.X < area.WorkArea.X + area.WorkArea.Width - 100
            && rect.Y < area.WorkArea.Y + area.WorkArea.Height - 100;

        if (onScreen) AppWindow.MoveAndResize(rect);
        else AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));

        if (s.WindowMaximized && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();
    }

    private void SavePlacement()
    {
        var s = App.Settings;
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        s.WindowMaximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;

        // Keep the last normal bounds so un-maximizing later lands where the user left it
        if (!s.WindowMaximized && presenter?.State != Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
        {
            s.WindowX      = AppWindow.Position.X;
            s.WindowY      = AppWindow.Position.Y;
            s.WindowWidth  = AppWindow.Size.Width;
            s.WindowHeight = AppWindow.Size.Height;
        }
        s.Save();
    }

    // =========================================================================
    // CONNECTION FLOW
    // =========================================================================

    private RemoteBridge? bridge;
    private RemoteBridge  Bridge  => bridge ??= new RemoteBridge(BridgeView);
    private Speaker?      speaker;
    public  Speaker       Speaker => speaker ??= new Speaker(Bridge);

    private async Task StartAsync()
    {
        var token = App.Settings.GetToken();
        if (string.IsNullOrEmpty(token) || (string.IsNullOrEmpty(App.Settings.ServerAddress) && string.IsNullOrEmpty(App.Settings.RemoteId)))
        {
            ShowLogin(null);
            return;
        }

        try
        {
            await ConnectBestAsync(CancellationToken.None);
            await App.Client.AuthenticateAsync(token);
            ShowMain();
        }
        catch (ApiException ex) when (ex.IsAuthError)
        {
            App.Settings.ClearToken();
            ShowLogin("Your session expired. Please sign in again.");
        }
        catch (Exception ex)
        {
            ShowLogin($"Could not reach your server: {ex.Message}");
        }
    }

    /// <summary>
    /// Connect the best way available: the local address first (fast on the
    /// home network), then Music Assistant remote access when a Remote ID is
    /// known. The local attempt is kept short when a remote fallback exists.
    /// </summary>
    private async Task ConnectBestAsync(CancellationToken ct)
    {
        var address  = App.Settings.ServerAddress;
        var remoteId = App.Settings.RemoteId;
        Exception? localError = null;

        if (!string.IsNullOrEmpty(address))
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(string.IsNullOrEmpty(remoteId) ? 15 : 5));
            try
            {
                await App.Client.ConnectAsync(address, quick.Token);
                SetRemote(false);
                return;
            }
            catch (ApiException ex) when (ex.Code == ApiException.SetupRequired) { throw; }
            catch (Exception ex) { localError = ex; }
        }

        if (!string.IsNullOrEmpty(remoteId))
        {
            await App.Client.ConnectAsync(new WebRtcTransport(Bridge, remoteId), ct);
            SetRemote(true);
            return;
        }

        throw localError ?? new ApiException(0, "No server address or Remote ID configured.");
    }

    private void SetRemote(bool remote)
    {
        RemoteBadge.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        Images.Resolver = App.Client.ImageUrl;   // base URL changed with the transport
    }

    /// <summary>
    /// Called by LoginPage. Connects by local address, or by Remote ID when no
    /// address is given, logs in, stores the token and shows the main UI.
    /// </summary>
    public async Task LoginAsync(string? address, string? remoteId, string username, string password)
    {
        CancelReconnect();
        await App.Client.DisconnectAsync();
        App.Settings.ServerAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        App.Settings.RemoteId      = remoteId;
        App.Settings.Save();

        await ConnectBestAsync(CancellationToken.None);
        var token = await App.Client.LoginAsync(username, password);
        App.Settings.SetToken(token);
        ShowMain();
    }

    /// <summary>
    /// Called by LoginPage. Browser sign-in through the server's Home Assistant
    /// OAuth provider (works with local HA and Nabu Casa URLs alike, since the
    /// server owns the HA address). Waits for the token on a loopback listener.
    /// Local connections only: the browser must be able to reach the server.
    /// </summary>
    public async Task LoginWithHomeAssistantAsync(string address, CancellationToken ct)
    {
        CancelReconnect();
        await App.Client.DisconnectAsync();
        await App.Client.ConnectAsync(address, ct);
        SetRemote(false);

        var providers = await App.Client.GetAuthProvidersAsync();
        var provider  = providers.FirstOrDefault(p => p.ProviderType == "oauth_homeassistant" || p.ProviderId == "homeassistant")
            ?? throw new ApiException(ApiException.AuthenticationFailed, "This server has no Home Assistant sign-in configured.");

        using var loopback = new OAuthLoopback();
        var authorizeUrl = await App.Client.GetAuthorizationUrlAsync(provider.ProviderId, loopback.ReturnUrl);

        if (!Uri.TryCreate(authorizeUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ApiException(ApiException.AuthenticationFailed, "Server returned an invalid sign-in URL.");
        }

        await Windows.System.Launcher.LaunchUriAsync(uri);
        var token = await loopback.WaitForCodeAsync(ct);

        App.Settings.ServerAddress = address;
        App.Settings.Save();

        await App.Client.AuthenticateAsync(token);
        App.Settings.SetToken(token);
        ShowMain();
    }
    public async Task SignOutAsync()
    {
        CancelReconnect();
        Speaker.Stop();
        try { if (App.Client.IsConnected) await App.Client.LogoutAsync(); } catch (Exception ex) { App.Log("Logout: " + ex.Message); }
        App.Settings.ClearToken();
        await App.Client.DisconnectAsync();
        ShowLogin(null);
    }

    private void OnDisconnected(Exception? error)
    {
        if (LoginFrame.Visibility == Visibility.Visible || reconnectCts is not null) return;
        ShowMessage("Connection lost. Reconnecting…", InfoBarSeverity.Warning, autoClose: false);
        reconnectCts = new CancellationTokenSource();
        _ = ReconnectAsync(reconnectCts.Token);
    }

    /// <summary>Stop a running reconnect loop before a manual sign-in or sign-out takes over the connection.</summary>
    private void CancelReconnect()
    {
        reconnectCts?.Cancel();
        reconnectCts = null;
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                reconnectAttempt++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, reconnectAttempt))), ct);

                // The token is re-read every attempt: a sign-out in between must end the loop, not be undone by it
                var token = App.Settings.GetToken();
                if (token is null) { ShowLogin(null); return; }

                try
                {
                    await ConnectBestAsync(ct);
                    await App.Client.AuthenticateAsync(token);
                    if (ct.IsCancellationRequested) { await App.Client.DisconnectAsync(); return; }
                    reconnectAttempt = 0;
                    MessageBar.IsOpen = false;
                    App.EnsureActivePlayer();
                    App.NotifyStateChanged();
                    _ = Speaker.SyncAsync();
                    return;
                }
                catch (ApiException ex) when (ex.IsAuthError)
                {
                    App.Settings.ClearToken();
                    ShowLogin("Your session expired. Please sign in again.");
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception)
                {
                    // keep retrying
                }
            }
        }
        finally
        {
            if (reconnectCts?.Token == ct) reconnectCts = null;
        }
    }

    // =========================================================================
    // SHELL STATE
    // =========================================================================

    private void ShowLogin(string? message)
    {
        Nav.Visibility        = Visibility.Collapsed;
        Player.Visibility     = Visibility.Collapsed;
        LoginFrame.Visibility = Visibility.Visible;
        LoginFrame.Navigate(typeof(LoginPage), message);
    }

    private void ShowMain()
    {
        App.EnsureActivePlayer();
        LoginFrame.Visibility = Visibility.Collapsed;
        Nav.Visibility        = Visibility.Visible;
        Player.Visibility     = Visibility.Visible;
        ContentFrame.BackStack.Clear();
        Nav.SelectedItem = Nav.MenuItems[1];
        Navigate(typeof(HomePage), null);

        var user = App.Client.CurrentUser;
        UserNameText.Text = user?.DisplayName ?? user?.Username ?? "";
        ToolTipService.SetToolTip(UserItem, user?.Username);
        _ = LoadAvatarAsync(user?.AvatarUrl);

        App.NotifyStateChanged();
        _ = UpdateOptionalNavAsync();
        _ = RememberRemoteIdAsync();
        _ = Speaker.SyncAsync();
    }

    /// <summary>
    /// Avatar from the Music Assistant profile (relative paths are served by the
    /// server), stamped into a circle offscreen and used as the sidebar icon.
    /// Falls back to the app icon.
    /// </summary>
    private async Task LoadAvatarAsync(string? avatarUrl)
    {
        var appIcon = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        UserIcon.UriSource = appIcon;
        if (string.IsNullOrWhiteSpace(avatarUrl)) return;

        var absolute = avatarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? avatarUrl : App.Client.BaseUrl + "/" + avatarUrl.TrimStart('/');
        if (Templates.Decode(absolute, 96) is not { } image) return;

        var opened = new TaskCompletionSource<bool>();
        image.ImageOpened += (_, _) => opened.TrySetResult(true);
        image.ImageFailed += (_, e) => { App.Log($"Avatar failed to load from {absolute}: {e.ErrorMessage}"); opened.TrySetResult(false); };
        AvatarBrush.ImageSource = image;
        if (!await opened.Task) return;

        // Let the brush paint once, then capture the circle to a PNG (BitmapIcon only takes a file)
        await Task.Yield();
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicAssistant", "avatar.png");
        if (!await Png.SaveAsync(AvatarStamp, file, 96, 96)) { App.Log("Avatar circle render produced no pixels; keeping app icon"); return; }
        UserIcon.UriSource = new Uri(file);
    }

    /// <summary>
    /// When connected locally as an admin, read the server's Remote ID and keep
    /// it, so the next launch away from home can fall back to remote access
    /// without the user copying anything.
    /// </summary>
    private async Task RememberRemoteIdAsync()
    {
        if (App.Client.IsRemote || App.Client.CurrentUser?.Role != "admin") return;
        try
        {
            var info = await App.Client.GetRemoteAccessInfoAsync();
            var id   = info.Enabled ? MassClient.NormalizeRemoteId(info.RemoteId) : null;
            if (id == App.Settings.RemoteId) return;
            App.Settings.RemoteId = id;
            App.Settings.Save();
        }
        catch (ApiException) { }
    }

    /// <summary>Show the Audiobooks and Podcasts entries only when the library has some.</summary>
    private async Task UpdateOptionalNavAsync()
    {
        await ShowIfAnyAsync(AudiobooksItem, "audiobooks");
        await ShowIfAnyAsync(PodcastsItem, "podcasts");

        static async Task ShowIfAnyAsync(NavigationViewItem item, string mediaType)
        {
            try { item.Visibility = await App.Client.GetLibraryCountAsync(mediaType) > 0 ? Visibility.Visible : Visibility.Collapsed; }
            catch (ApiException) { item.Visibility = Visibility.Collapsed; }
        }
    }

    public void Navigate(Type page, object? parameter)
    {
        CloseQueue();
        ContentFrame.Navigate(page, parameter, new DrillInNavigationTransitionInfo());
        BackButton.IsEnabled = ContentFrame.CanGoBack;
    }

    // Queue Panel

    private bool QueueOpen => QueueFrame.Visibility == Visibility.Visible;

    public void ToggleQueue()
    {
        if (QueueOpen) { CloseQueue(); return; }
        QueueFrame.Navigate(typeof(QueuePage), null);
        QueueFrame.Visibility = Visibility.Visible;
        Player.SetQueueOpen(true);
        SlideQueue(open: true);
    }

    private void CloseQueue()
    {
        Player.SetQueueOpen(false);
        if (!QueueOpen || queueClosing) return;
        queueClosing = true;
        SlideQueue(open: false, completed: () =>
        {
            queueClosing = false;
            QueueFrame.Visibility = Visibility.Collapsed;
            QueueFrame.Content    = null;
            QueueFrame.BackStack.Clear();
        });
    }

    private bool queueClosing;

    /// <summary>Now Playing slides up from the player bar on open and back down on close, with a fade.</summary>
    private void SlideQueue(bool open, Action? completed = null)
    {
        var transform = QueueFrame.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform ?? new Microsoft.UI.Xaml.Media.TranslateTransform();
        QueueFrame.RenderTransform = transform;

        var distance = Math.Max(120, QueueFrame.ActualHeight);
        var duration = new Duration(TimeSpan.FromMilliseconds(open ? 320 : 240));
        var ease     = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

        var slide = new DoubleAnimation { From = open ? distance : 0, To = open ? 0 : distance, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation { From = open ? 0 : 1, To = open ? 1 : 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, QueueFrame);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        if (completed is not null) storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    /// <summary>
    /// Space toggles play/pause. Without this, Space activates whichever card
    /// button happens to have focus and starts playing it, which reads as
    /// "random playback". Text fields keep Space for typing.
    /// </summary>
    private void OnSpace(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (focused is TextBox or PasswordBox or AutoSuggestBox or RichEditBox || LoginFrame.Visibility == Visibility.Visible)
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;
        if (App.ActivePlayer is { } player)
        {
            _ = App.Client.PlayerCommandAsync(player.PlayerId, "play_pause").ContinueWith(
                t => DispatcherQueue.TryEnqueue(() => ShowMessage(t.Exception!.InnerException?.Message ?? "Command failed")),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    // Mouse Buttons

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Root).Properties;
        if (props.IsXButton1Pressed)      { GoBack();    e.Handled = true; }
        else if (props.IsXButton2Pressed) { GoForward(); e.Handled = true; }
    }

    private void GoBack()
    {
        if (QueueOpen) { CloseQueue(); return; }
        if (!ContentFrame.CanGoBack) return;
        ContentFrame.GoBack();
        BackButton.IsEnabled = ContentFrame.CanGoBack;
    }

    private void GoForward()
    {
        if (!ContentFrame.CanGoForward) return;
        ContentFrame.GoForward();
        BackButton.IsEnabled = ContentFrame.CanGoBack;
    }

    public void ShowMessage(string text, InfoBarSeverity severity = InfoBarSeverity.Error, bool autoClose = true)
    {
        MessageBar.Message  = text;
        MessageBar.Severity = severity;
        MessageBar.IsOpen   = true;
        if (!autoClose) return;

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(6);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => MessageBar.IsOpen = false;
        timer.Start();
    }

    // Navigation Events

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            Navigate(typeof(SettingsPage), null);
            return;
        }

        if (args.InvokedItemContainer?.Tag is string tag && NavPages.TryGetValue(tag, out var page))
        {
            Navigate(page, tag);
        }
        else if (args.InvokedItemContainer?.Tag is "account")
        {
            Navigate(typeof(SettingsPage), null);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();
}
