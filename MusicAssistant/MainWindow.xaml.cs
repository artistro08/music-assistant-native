using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using MusicAssistant.Api;
using MusicAssistant.Pages;
using MusicAssistant.Remote;
using MusicAssistant.Sendspin;

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

    /// <summary>Ordered teardown started (ExitApp).</summary>
    private bool exiting;

    /// <summary>Teardown done; the next Closing may pass.</summary>
    private bool closeAllowed;

    /// <summary>One reconnect loop at a time; canceled by sign-out and manual sign-in.</summary>
    private CancellationTokenSource? reconnectCts;

    /// <summary>Builds the shell, restores the window placement, sets up the tray icon and close behavior, and starts connecting.</summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            // Tall (48px) caption buttons: the Windows size for a custom title bar, and some breathing room around the logo and title.
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

            // Windows doesn't recolor the caption buttons for a custom title bar; follow the app's light or dark theme.
            ApplyCaptionButtonColors();
            Root.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
        }
        RestorePlacement();
        SyncTitleBarHeight();

        // DPI or caption height changes; a move between screens with different scaling also moves the search box's
        // pass-through rectangle, which is in physical pixels.
        AppWindow.Changed += (_, _) =>
        {
            SyncTitleBarHeight();
            UpdateTitleSearchPassthrough();
        };

        // App icon in the title bar / taskbar, and the tray icon with its menu.
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        AppWindow.SetIcon(iconPath);
        tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), iconPath, ShowFromTray, ExitApp, App.Settings.ShowTrayIcon);
        App.StateChanged += UpdateTrayTip;
        UpdateQuitItem();

        // Closing the window keeps the app running in the background (hidden) when that setting is on; otherwise it quits.
        AppWindow.Closing += (_, args) =>
        {
            // ExitApp's own Close() after the ordered teardown.
            if (closeAllowed) return;

            // Every other close request is decided here, never by the default close.
            args.Cancel = true;

            // Teardown already running; extra clicks on X must not close a window it still uses.
            if (exiting) return;
            if (App.Settings.RunInBackground)
            {
                SavePlacement();
                AppWindow.Hide();
            }
            else
            {
                // Off the Closing callback, so Close() is not re-entered from inside it.
                DispatcherQueue.TryEnqueue(ExitApp);
            }
        };

        App.Client.Disconnected += error => DispatcherQueue.TryEnqueue(() => OnDisconnected(error));
        Closed += (_, _) =>
        {
            SavePlacement();
            speaker?.Stop();
            tray.Dispose();
            App.Client.Dispose();
        };

        // Mouse back/forward buttons navigate the content frame (handledEventsToo so child controls cannot swallow them).
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);

        Png.ScheduleSnapshot(Root);
        _ = StartAsync();
    }

    // =========================================================================
    // TRAY
    // =========================================================================

    /// <summary>Apply the tray-icon and background settings live after they change in Settings.</summary>
    public void ApplyWindowSettings()
    {
        tray.SetVisible(App.Settings.ShowTrayIcon);

        // A re-added icon starts with the plain app name; give it the now-playing tip straight away.
        UpdateTrayTip();
        UpdateQuitItem();
    }

    /// <summary>The sidebar Quit item is the only in-app way out when the tray icon (with its Exit) is hidden, so show it exactly then.</summary>
    private void UpdateQuitItem() => QuitItem.Visibility = App.Settings.ShowTrayIcon ? Visibility.Collapsed : Visibility.Visible;

    private void ShowFromTray()
    {
        // If the window is open on another virtual desktop, plain Activate would switch the user to that desktop.
        // Hiding then showing re-places it on the desktop the user is on now, so launching from the Start menu (or the
        // tray) brings the app to them instead of yanking them away.
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.IsVisible && !IsOnCurrentDesktop(hwnd)) AppWindow.Hide();

        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    /// <summary>Whether the window currently lives on the virtual desktop the user is viewing. True (do nothing) if the desktop manager is unavailable or the state can't be read.</summary>
    private static bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        try
        {
            var manager = (IVirtualDesktopManager)new VirtualDesktopManagerClass();
            try
            {
                return (manager.IsWindowOnCurrentVirtualDesktop(hwnd, out int onCurrent) == 0) && (onCurrent != 0);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(manager);
            }
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // No shell virtual-desktop support: leave the window where it is and just activate.
            return true;
        }
    }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManagerClass { }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [System.Runtime.InteropServices.PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

        [System.Runtime.InteropServices.PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [System.Runtime.InteropServices.PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    private async void ExitApp()
    {
        // Tray Exit, sidebar Quit and X can all land while the teardown below is awaiting.
        if (exiting) return;
        exiting = true;

        // The PC speaker goes away with the app, so end its playback, not leave a zombie queue.
        await StopOwnSpeakerAsync();
        closeAllowed = true;

        // Closed handler stops the speaker, removes the tray icon and its menu window, disconnects.
        Close();

        // Nothing else may keep the process alive.
        Application.Current.Exit();
    }

    /// <summary>
    /// Stop playback on this PC's own speaker queue when quitting, if it is the one playing. Otherwise the server
    /// keeps that queue in a "playing" state with no speaker behind it, and the next launch opens looking like it
    /// is already playing. Bounded so a slow or dropped server never blocks the exit.
    /// </summary>
    private static async Task StopOwnSpeakerAsync()
    {
        string? id = App.Settings.SpeakerClientId;
        if (!App.Settings.SpeakerEnabled || string.IsNullOrEmpty(id)) return;
        if (!App.Client.Players.TryGetValue(id, out Player? pc) || !pc.IsPlaying) return;
        try
        {
            await App.Client.PlayerCommandAsync(id, "stop").WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Exit stop: {ex.Message}");
        }
    }

    private void UpdateTrayTip()
    {
        Player? player = App.ActivePlayer;
        QueueItem? item   = player is null ? null : App.Client.Queues.GetValueOrDefault(App.Client.QueueIdFor(player))?.CurrentItem;
        string? title  = item?.Title ?? player?.CurrentMedia?.Title;
        tray.SetTip(title is null ? "Music Assistant" : $"Music Assistant\n{title}\n{player!.Name}");
    }

    // =========================================================================
    // WINDOW PLACEMENT
    // =========================================================================

    /// <summary>Restore the last size and position when it still lands on a connected display; otherwise use the default size.</summary>
    private void RestorePlacement()
    {
        Session s = App.Settings;
        var rect = new Windows.Graphics.RectInt32(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight);

        bool onScreen = (s.WindowWidth >= 400) && (s.WindowHeight >= 300)
            && Microsoft.UI.Windowing.DisplayArea.GetFromRect(rect, Microsoft.UI.Windowing.DisplayAreaFallback.None) is { } area
            && (rect.X < area.WorkArea.X + area.WorkArea.Width - 100)
            && (rect.Y < area.WorkArea.Y + area.WorkArea.Height - 100);

        if (onScreen) AppWindow.MoveAndResize(rect);
        else AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));

        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            // Below this the player bar's Narrow state and the Home rows stop fitting; the limit is in physical pixels.
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            presenter.PreferredMinimumWidth  = (int)(720 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);

            if (s.WindowMaximized) presenter.Maximize();
        }
    }

    /// <summary>
    /// Colors the minimize, maximize and close buttons for the current theme: dark glyphs in light mode, light glyphs in
    /// dark mode, with the same subtle hover and press fills as the app's other title bar buttons.
    /// </summary>
    private void ApplyCaptionButtonColors()
    {
        bool light = Root.ActualTheme == ElementTheme.Light;
        Microsoft.UI.Windowing.AppWindowTitleBar bar = AppWindow.TitleBar;

        // The Fluent text fill colors: primary for the glyphs, disabled while the window is inactive, secondary while
        // pressed, and the subtle fills for hover and press backgrounds.
        bar.ButtonBackgroundColor         = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor         = light ? Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00) : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        bar.ButtonInactiveForegroundColor = light ? Windows.UI.Color.FromArgb(0x5C, 0x00, 0x00, 0x00) : Windows.UI.Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF);
        bar.ButtonHoverBackgroundColor    = light ? Windows.UI.Color.FromArgb(0x09, 0x00, 0x00, 0x00) : Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
        bar.ButtonHoverForegroundColor    = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor  = light ? Windows.UI.Color.FromArgb(0x06, 0x00, 0x00, 0x00) : Windows.UI.Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);
        bar.ButtonPressedForegroundColor  = light ? Windows.UI.Color.FromArgb(0x9E, 0x00, 0x00, 0x00) : Windows.UI.Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>
    /// The caption buttons are laid out from the window's top edge, but in a normal window XAML content starts one
    /// physical pixel lower, below the window's top border line. A fixed-height row therefore sat a pixel below the
    /// buttons' center. Size the row to the caption height minus that border (none while maximized) and center the
    /// content in it, level with the buttons. Measured at 125% in both states.
    /// </summary>
    private void SyncTitleBarHeight()
    {
        int height = AppWindow.TitleBar.Height;
        if (height <= 0) return;
        bool maximized = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized };
        double scale     = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        double logical   = (height - (maximized ? 0 : 1)) / scale;
        if (double.IsNaN(TitleBarRow.Height) || (Math.Abs(TitleBarRow.Height - logical) > 0.01)) TitleBarRow.Height = logical;

        // Windows draws the caption glyphs a pixel above their buttons' center; lifting the title bar content one physical
        // pixel lines the back arrow, logo, title and search box up with them (measured at 125%, maximized).
        double lift = -1 / scale;
        if ((TitleBarRow.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform)?.Y != lift)
        {
            TitleBarRow.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform { Y = lift };
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SavePlacement()
    {
        Session s = App.Settings;
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        s.WindowMaximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;

        // Keep the last normal bounds so un-maximizing later lands where the user left it.
        if (!s.WindowMaximized && (presenter?.State != Microsoft.UI.Windowing.OverlappedPresenterState.Minimized))
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

    private Speaker? speaker;

    /// <summary>This PC's own Sendspin speaker, created on first use.</summary>
    public  Speaker  Speaker => speaker ??= new Speaker();

    /// <summary>True only when the local speaker is actually streaming; never instantiates the speaker just to ask.</summary>
    public bool SpeakerPlaying => speaker?.Playing == true;

    private async Task StartAsync()
    {
        string? token = App.Settings.GetToken();
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
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
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
        string? address  = App.Settings.ServerAddress;
        string? remoteId = App.Settings.RemoteId;
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
            catch (ApiException ex) when (ex.Code == ApiException.SetupRequired)
            {
                throw;
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                localError = ex;
            }
        }

        if (!string.IsNullOrEmpty(remoteId))
        {
            await App.Client.ConnectAsync(new WebRtcTransport(remoteId), ct);
            SetRemote(true);
            return;
        }

        throw localError ?? new ApiException(0, "No server address or Remote ID configured.");
    }

    /// <summary>Base URL the cached images and resolved card URLs were built against.</summary>
    private string? imageBaseUrl;

    private void SetRemote(bool remote)
    {
        RemoteBadge.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;

        // Base URL changed with the transport.
        Images.Resolver = App.Client.ImageUrl;

        // Every remote connect builds a fresh loopback image proxy on a new port and nonce, so the cached bitmaps and
        // any already-resolved card URLs go stale on a remote-to-remote reconnect too, not only on a mode switch.
        // Key on the base URL itself; a plain local reconnect keeps the same address and skips the refetch.
        if (imageBaseUrl != App.Client.BaseUrl)
        {
            imageBaseUrl = App.Client.BaseUrl;
            Templates.InvalidateImages();
        }
    }

    /// <summary>
    /// Called by LoginPage. Connects by local address, or by Remote ID when no
    /// address is given, logs in, stores the token and shows the main UI.
    /// </summary>
    /// <param name="address">The server's local address, or null or blank to connect by Remote ID only.</param>
    /// <param name="remoteId">The server's Remote ID for remote access, or null.</param>
    /// <param name="username">The Music Assistant user name.</param>
    /// <param name="password">The Music Assistant password.</param>
    /// <returns>A task that completes once signed in and the main UI is showing.</returns>
    public async Task LoginAsync(string? address, string? remoteId, string username, string password)
    {
        CancelReconnect();
        await App.Client.DisconnectAsync();
        App.Settings.ServerAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        App.Settings.RemoteId      = remoteId;
        App.Settings.Save();

        await ConnectBestAsync(CancellationToken.None);
        string token = await App.Client.LoginAsync(username, password);
        App.Settings.SetToken(token);
        ShowMain();
    }

    /// <summary>
    /// Called by LoginPage. Browser sign-in through the server's Home Assistant
    /// OAuth provider (works with local HA and Nabu Casa URLs alike, since the
    /// server owns the HA address). Waits for the token on a loopback listener.
    /// Local connections only: the browser must be able to reach the server.
    /// </summary>
    /// <param name="address">The server's local address.</param>
    /// <param name="ct">Cancels the connection attempt and the wait for the browser sign-in.</param>
    /// <returns>A task that completes once signed in and the main UI is showing.</returns>
    public async Task LoginWithHomeAssistantAsync(string address, CancellationToken ct)
    {
        CancelReconnect();
        await App.Client.DisconnectAsync();
        await App.Client.ConnectAsync(address, ct);
        SetRemote(false);

        List<AuthProvider> providers = await App.Client.GetAuthProvidersAsync();
        AuthProvider provider  = providers.FirstOrDefault(p => (p.ProviderType == "oauth_homeassistant") || (p.ProviderId == "homeassistant"))
            ?? throw new ApiException(ApiException.AuthenticationFailed, "This server has no Home Assistant sign-in configured.");

        using var loopback = new OAuthLoopback();
        string authorizeUrl = await App.Client.GetAuthorizationUrlAsync(provider.ProviderId, loopback.ReturnUrl);

        if (!Uri.TryCreate(authorizeUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ApiException(ApiException.AuthenticationFailed, "Server returned an invalid sign-in URL.");
        }

        await Windows.System.Launcher.LaunchUriAsync(uri);
        string token = await loopback.WaitForCodeAsync(ct);

        App.Settings.ServerAddress = address;
        App.Settings.Save();

        await App.Client.AuthenticateAsync(token);
        App.Settings.SetToken(token);
        ShowMain();
    }

    /// <summary>Stops the local speaker, logs out of the server, forgets the token, disconnects and shows the login page.</summary>
    /// <returns>A task that completes once the login page is showing.</returns>
    public async Task SignOutAsync()
    {
        CancelReconnect();
        Speaker.Stop();
        try
        {
            if (App.Client.IsConnected) await App.Client.LogoutAsync();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Logout: {ex.Message}");
        }
        App.Settings.ClearToken();
        await App.Client.DisconnectAsync();
        ShowLogin(null);
    }

    private void OnDisconnected(Exception? error)
    {
        if ((LoginFrame.Visibility == Visibility.Visible) || reconnectCts is not null) return;
        App.Log($"Connection lost: {error?.Message ?? "closed by server"}");
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

                // The token is re-read every attempt: a sign-out in between must end the loop, not be undone by it.
                string? token = App.Settings.GetToken();
                if (token is null)
                {
                    ShowLogin(null);
                    return;
                }

                try
                {
                    await ConnectBestAsync(ct);
                    await App.Client.AuthenticateAsync(token);
                    if (ct.IsCancellationRequested)
                    {
                        await App.Client.DisconnectAsync();
                        return;
                    }
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
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
                {
                    // Keep retrying.
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
        TitleSearchHost.Visibility = Visibility.Collapsed;
        TitleSearch.Text           = "";
        UpdateTitleSearchPassthrough();

        // The pages of the signed-out session are not somewhere to go back to.
        ContentFrame.BackStack.Clear();
        UpdateBackButton();

        LoginFrame.Visibility = Visibility.Visible;
        LoginFrame.Navigate(typeof(LoginPage), message);
    }

    private void ShowMain()
    {
        App.EnsureActivePlayer();
        LoginFrame.Visibility = Visibility.Collapsed;
        Nav.Visibility        = Visibility.Visible;
        Player.Visibility     = Visibility.Visible;
        TitleSearchHost.Visibility = Visibility.Visible;
        Nav.SelectedItem = Nav.MenuItems[1];
        Navigate(typeof(HomePage), null);

        // Cleared after navigating, or the page left over from before signing in would become the back entry.
        ContentFrame.BackStack.Clear();
        UpdateBackButton();

        User? user = App.Client.CurrentUser;
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

        string absolute = avatarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? avatarUrl : $"{App.Client.BaseUrl}/{avatarUrl.TrimStart('/')}";
        if (Templates.Decode(absolute, 96) is not { } image) return;

        var opened = new TaskCompletionSource<bool>();
        image.ImageOpened += (_, _) => opened.TrySetResult(true);
        image.ImageFailed += (_, e) =>
        {
            App.Log($"Avatar failed to load from {absolute}: {e.ErrorMessage}");
            opened.TrySetResult(false);
        };
        AvatarBrush.ImageSource = image;

        // The art cache evicts an image it cannot fetch without raising ImageFailed, so do not wait on it forever.
        try
        {
            if (!await opened.Task.WaitAsync(TimeSpan.FromMinutes(1))) return;
        }
        catch (TimeoutException)
        {
            App.Log($"Avatar did not load from {absolute}; keeping app icon");
            return;
        }

        // Let the brush paint once, then capture the circle to a PNG (BitmapIcon only takes a file).
        await Task.Yield();
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicAssistant", "avatar.png");
        if (!await Png.SaveAsync(AvatarStamp, file, 96, 96))
        {
            App.Log("Avatar circle render produced no pixels; keeping app icon");
            return;
        }
        UserIcon.UriSource = new Uri(file);
    }

    /// <summary>
    /// When connected locally as an admin, read the server's Remote ID and keep
    /// it, so the next launch away from home can fall back to remote access
    /// without the user copying anything.
    /// </summary>
    private async Task RememberRemoteIdAsync()
    {
        if (App.Client.IsRemote || (App.Client.CurrentUser?.Role != "admin")) return;
        try
        {
            RemoteAccessInfo info = await App.Client.GetRemoteAccessInfoAsync();
            string? id   = info.Enabled ? MassClient.NormalizeRemoteId(info.RemoteId) : null;
            if (id == App.Settings.RemoteId) return;
            App.Settings.RemoteId = id;
            App.Settings.Save();
        }
        catch (ApiException)
        {
        }
    }

    /// <summary>Show the Audiobooks and Podcasts entries only when the library has some.</summary>
    private async Task UpdateOptionalNavAsync()
    {
        await ShowIfAnyAsync(AudiobooksItem, "audiobooks");
        await ShowIfAnyAsync(PodcastsItem, "podcasts");

        static async Task ShowIfAnyAsync(NavigationViewItem item, string mediaType)
        {
            try
            {
                item.Visibility = await App.Client.GetLibraryCountAsync(mediaType) > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (ApiException)
            {
                item.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Closes the queue panel and navigates the content frame to a page with a drill-in transition.</summary>
    /// <param name="page">The page type to show.</param>
    /// <param name="parameter">The navigation parameter passed to the page, or null.</param>
    public void Navigate(Type page, object? parameter)
    {
        CloseQueue();
        ContentFrame.Navigate(page, parameter, new DrillInNavigationTransitionInfo());
        UpdateBackButton();
    }

    // Queue Panel

    private bool QueueOpen => QueueFrame.Visibility == Visibility.Visible;

    /// <summary>Opens the Now Playing queue panel with a slide-up animation, or closes it when it is already open.</summary>
    public void ToggleQueue()
    {
        if (QueueOpen)
        {
            CloseQueue();
            return;
        }
        QueueFrame.Navigate(typeof(QueuePage), null);

        // Start offscreen and transparent so there is no flash before the slide, then animate in on the next tick.
        // The first open has never been laid out, so beginning the storyboard in this same tick would snap; the
        // enqueue lets the frame realize its visual and measure its height first.
        Microsoft.UI.Xaml.Media.TranslateTransform transform = QueueFrame.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform ?? new Microsoft.UI.Xaml.Media.TranslateTransform();
        QueueFrame.RenderTransform = transform;
        transform.Y           = QueueDistance;
        QueueFrame.Opacity    = 0;
        QueueFrame.Visibility = Visibility.Visible;
        Player.SetQueueOpen(true);
        DispatcherQueue.TryEnqueue(() => SlideQueue(open: true));
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

    /// <summary>Keep the sliding queue inside its row: a clip on the host, which does not move, cuts off whatever of the panel is still below it.</summary>
    private void OnQueueHostSizeChanged(object sender, SizeChangedEventArgs e)
        => QueueHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    /// <summary>How far the panel travels: the content row's height, which is valid even on the first open (the frame itself has ActualHeight 0 until laid out).</summary>
    private double QueueDistance => Math.Max(120, Nav.ActualHeight);

    /// <summary>Now Playing slides up from the player bar on open and back down on close, with a fade.</summary>
    private void SlideQueue(bool open, Action? completed = null)
    {
        Microsoft.UI.Xaml.Media.TranslateTransform transform = QueueFrame.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform ?? new Microsoft.UI.Xaml.Media.TranslateTransform();
        QueueFrame.RenderTransform = transform;

        double distance = QueueDistance;
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

    // Title Bar.

    /// <summary>Width of the back button's slot when shown: the 36px square button plus the 4px gap before the logo.</summary>
    private const double BackSlotWidth = 40;

    /// <summary>Widest the title bar search box gets, in effective pixels.</summary>
    private const double TitleSearchMaxWidth = 460;

    /// <summary>Narrowest the title bar search box gets before it's allowed to crowd the title.</summary>
    private const double TitleSearchMinWidth = 240;

    /// <summary>Pause after the last keystroke before the title bar search runs, so a search doesn't go out per letter.</summary>
    private static readonly TimeSpan TitleSearchDelay = TimeSpan.FromMilliseconds(450);

    /// <summary>Runs the search once typing pauses; restarted by every change to the text.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? titleSearchTimer;

    /// <summary>Searches as the user types, once they pause, the way Task Manager's search box filters.</summary>
    /// <param name="sender">The title bar search box.</param>
    /// <param name="e">Unused.</param>
    private void OnTitleSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (titleSearchTimer is null)
        {
            titleSearchTimer             = DispatcherQueue.CreateTimer();
            titleSearchTimer.Interval    = TitleSearchDelay;
            titleSearchTimer.IsRepeating = false;
            titleSearchTimer.Tick       += (_, _) => RunTitleSearch();
        }
        titleSearchTimer.Stop();
        titleSearchTimer.Start();
    }

    /// <summary>Runs the search right away when Enter is pressed in the title bar search box.</summary>
    /// <param name="sender">The title bar search box.</param>
    /// <param name="e">The key that was pressed.</param>
    private void OnTitleSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        titleSearchTimer?.Stop();
        RunTitleSearch();
    }

    /// <summary>
    /// Shows results for the search box text: updates the search page in place while it's open, or opens it. Queries
    /// shorter than two characters are ignored, like the server does.
    /// </summary>
    private void RunTitleSearch()
    {
        string query = TitleSearch.Text.Trim();
        if (query.Length < 2)
        {
            return;
        }

        if (ContentFrame.Content is SearchPage page)
        {
            CloseQueue();
            page.ShowResults(query);
            return;
        }

        Navigate(typeof(SearchPage), query);
    }

    /// <summary>Ctrl+F puts keyboard focus in the title bar search box, with its text selected for a new search.</summary>
    /// <param name="sender">The accelerator.</param>
    /// <param name="args">Marked handled when the search box took focus.</param>
    private void OnFindShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TitleSearchHost.Visibility != Visibility.Visible)
        {
            return;
        }

        args.Handled = true;
        TitleSearch.Focus(FocusState.Keyboard);
        TitleSearch.SelectAll();
    }

    /// <summary>
    /// Keeps the search box centered in the window without running into the title on the left or the caption buttons
    /// on the right.
    /// </summary>
    /// <param name="sender">The title bar row.</param>
    /// <param name="e">The row's new size.</param>
    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => FitTitleSearch(e.NewSize.Width);

    /// <summary>Sizes the search box to fit between the title and the caption buttons, up to its widest.</summary>
    /// <param name="rowWidth">Width of the title bar row.</param>
    private void FitTitleSearch(double rowWidth)
    {
        double scale      = TitleBarRow.XamlRoot?.RasterizationScale ?? 1;
        double rightInset = AppWindow.TitleBar.RightInset / scale;

        // The title's right edge, with the back button shown: its slot, the row padding, the logo, and the app name.
        double leftInset = BackSlotWidth + 4 + AppTitleBar.Padding.Left + 16 + AppTitleBar.ColumnSpacing + AppTitleContent.ActualWidth;
        double side      = Math.Max(leftInset, rightInset) + 16;
        TitleSearchHost.Width = Math.Clamp(rowWidth - (2 * side), TitleSearchMinWidth, TitleSearchMaxWidth);
        UpdateTitleSearchPassthrough();
    }

    /// <summary>Keeps the pass-through region on top of the search box whenever the box changes size.</summary>
    /// <param name="sender">The search box host.</param>
    /// <param name="e">Unused.</param>
    private void OnTitleSearchSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleSearchPassthrough();

    /// <summary>Re-fits the search box when the title's width changes, for example when the REMOTE badge appears.</summary>
    /// <param name="sender">The title text and badge.</param>
    /// <param name="e">Unused.</param>
    private void OnAppTitleSizeChanged(object sender, SizeChangedEventArgs e) => FitTitleSearch(TitleBarRow.ActualWidth);

    /// <summary>Puts text in the title bar search box, for a page that shows results for it again.</summary>
    /// <param name="query">The query to show.</param>
    public void SetSearchText(string query)
    {
        if (TitleSearch.Text != query)
        {
            TitleSearch.Text = query;
        }
    }

    /// <summary>
    /// Marks the search box as a pass-through region of the title bar. The title bar element around it is a drag region,
    /// which would otherwise take the clicks meant for the box.
    /// </summary>
    private void UpdateTitleSearchPassthrough()
    {
        var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        if ((TitleSearchHost.Visibility != Visibility.Visible) || (TitleSearchHost.XamlRoot is null))
        {
            source.ClearRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough);
            return;
        }

        double scale = TitleSearchHost.XamlRoot.RasterizationScale;
        Windows.Foundation.Rect bounds = TitleSearchHost.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, TitleSearchHost.ActualWidth, TitleSearchHost.ActualHeight));
        var rect = new Windows.Graphics.RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
        source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [rect]);
    }

    /// <summary>
    /// Space toggles play/pause. Without this, Space activates whichever card
    /// button happens to have focus and starts playing it, which reads as
    /// "random playback". Text fields keep Space for typing.
    /// </summary>
    private void OnSpace(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        object focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (focused is TextBox or PasswordBox or AutoSuggestBox or RichEditBox || (LoginFrame.Visibility == Visibility.Visible))
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;
        if (App.ActivePlayer is { } player) App.SendPlayerCommand(player, "play_pause");
    }

    // Mouse Buttons

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPointProperties props = e.GetCurrentPoint(Root).Properties;
        if (props.IsXButton1Pressed)
        {
            GoBack();
            e.Handled = true;
        }
        else if (props.IsXButton2Pressed)
        {
            GoForward();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Shows the back button only while there's a page to go back to, sliding and fading it in or out of the title bar.
    /// </summary>
    /// <remarks>
    /// The button's slot widens or narrows at the same time, so the logo and title glide over instead of jumping. Each
    /// animation starts from the current value, so a change of direction midway reverses smoothly.
    /// </remarks>
    private void UpdateBackButton()
    {
        bool canGoBack = ContentFrame.CanGoBack;
        if (BackButton.IsEnabled == canGoBack)
        {
            return;
        }

        // Disabled, and collapsed once the hide animation ends, so keyboard focus and Narrator skip it while hidden.
        BackButton.IsEnabled = canGoBack;
        if (canGoBack)
        {
            BackButton.Visibility = Visibility.Visible;
        }

        var easing     = new CubicEase { EasingMode = canGoBack ? EasingMode.EaseOut : EasingMode.EaseIn };
        var duration   = TimeSpan.FromMilliseconds(canGoBack ? 250 : 180);
        var storyboard = new Storyboard();
        storyboard.Children.Add(BackButtonAnimation(BackSlot, "Width", canGoBack ? BackSlotWidth : 0, duration, easing));
        storyboard.Children.Add(BackButtonAnimation(BackButton, "Opacity", canGoBack ? 1 : 0, duration, easing));
        storyboard.Children.Add(BackButtonAnimation(BackButtonShift, "X", canGoBack ? 0 : -12, duration, easing));
        storyboard.Completed += (_, _) =>
        {
            // A navigation during the animation may have brought the button back.
            if (!BackButton.IsEnabled)
            {
                BackButton.Visibility = Visibility.Collapsed;
            }
        };
        storyboard.Begin();
    }

    /// <summary>Builds one timeline of the back button's show or hide animation.</summary>
    /// <param name="target">The element or transform to animate.</param>
    /// <param name="property">The animated property.</param>
    /// <param name="to">The value the property ends at.</param>
    /// <param name="duration">How long the animation runs.</param>
    /// <param name="easing">The easing curve.</param>
    /// <returns>A timeline ready to add to a storyboard.</returns>
    private static DoubleAnimation BackButtonAnimation(DependencyObject target, string property, double to, TimeSpan duration, EasingFunctionBase easing)
    {
        // Width is a layout property, which only animates with dependent animation turned on.
        var animation = new DoubleAnimation
        {
            To                       = to,
            Duration                 = duration,
            EasingFunction           = easing,
            EnableDependentAnimation = property == "Width",
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }
    private void GoBack()
    {
        if (QueueOpen)
        {
            CloseQueue();
            return;
        }
        if (!ContentFrame.CanGoBack) return;
        ContentFrame.GoBack();
        UpdateBackButton();
    }

    private void GoForward()
    {
        if (!ContentFrame.CanGoForward) return;
        ContentFrame.GoForward();
        UpdateBackButton();
    }

    /// <summary>Shows a message in the shell's info bar, closing it after six seconds unless told to stay open.</summary>
    /// <param name="text">The message to show.</param>
    /// <param name="severity">The info bar style; errors by default.</param>
    /// <param name="autoClose">Whether the bar closes itself after six seconds.</param>
    public void ShowMessage(string text, InfoBarSeverity severity = InfoBarSeverity.Error, bool autoClose = true)
    {
        MessageBar.Message  = text;
        MessageBar.Severity = severity;
        MessageBar.IsOpen   = true;
        if (!autoClose) return;

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(6);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => MessageBar.IsOpen = false;
        timer.Start();
    }

    // Navigation Events

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is "quit")
        {
            ExitApp();
            return;
        }

        if (args.InvokedItemContainer?.Tag is "settings" or "account")
        {
            Navigate(typeof(SettingsPage), null);
            return;
        }

        if (args.InvokedItemContainer?.Tag is string tag && NavPages.TryGetValue(tag, out Type? page))
        {
            Navigate(page, tag);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    /// <summary>Alt+Left goes back, as in other Windows apps (the mouse's back button is handled in OnPointerPressed).</summary>
    /// <param name="sender">The accelerator.</param>
    /// <param name="args">Marked handled when there was somewhere to go back to.</param>
    private void OnBackShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!QueueOpen && !ContentFrame.CanGoBack)
        {
            return;
        }

        args.Handled = true;
        GoBack();
    }
}
