using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicAssistant.Api;

namespace MusicAssistant.Controls;

/// <summary>
/// Bottom transport bar: now playing, play controls, progress, volume and player picker.
///
/// Reads the active player and its queue from the API client on every
/// StateChanged event and ticks the progress slider once per second.
/// </summary>
public sealed partial class PlayerBar : UserControl
{
    private readonly DispatcherQueueTimer tickTimer;
    private readonly DispatcherQueueTimer volumeTimer;
    private readonly DispatcherQueueTimer volumeTipTimer;

    private bool    isSeeking;
    private bool    suppressVolume;
    private int     pendingVolume;
    private string? lastImageUrl;

    // Value tooltip shown while scrolling the volume; the Slider's own thumb tooltip only appears on a pointer drag
    private ToolTip?          volumeTip;
    private FrameworkElement? volumeTipOwner;
    private object?           volumeTipSaved;
    private DateTime          volumeSentAt;   // last volume_set send; the server echo is expected shortly after

    public PlayerBar()
    {
        InitializeComponent();

        tickTimer = DispatcherQueue.CreateTimer();
        tickTimer.Interval = TimeSpan.FromSeconds(1);
        tickTimer.Tick += (_, _) => UpdateProgress();
        tickTimer.Start();

        volumeTimer = DispatcherQueue.CreateTimer();
        volumeTimer.Interval = TimeSpan.FromMilliseconds(200);
        volumeTimer.IsRepeating = false;
        volumeTimer.Tick += (_, _) => _ = SendVolumeAsync();

        volumeTipTimer = DispatcherQueue.CreateTimer();
        volumeTipTimer.Interval = TimeSpan.FromMilliseconds(900);
        volumeTipTimer.IsRepeating = false;
        volumeTipTimer.Tick += (_, _) => HideVolumeTip();

        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => isSeeking = true), true);


        App.StateChanged += Refresh;
        Refresh();
    }

    private static Player? Player => App.ActivePlayer;

    private static PlayerQueue? Queue
        => Player is { } p && App.Client.Queues.TryGetValue(App.Client.QueueIdFor(p), out var q) ? q : null;

    // =========================================================================
    // RENDER
    // =========================================================================

    private void Refresh()
    {
        var player = Player;
        var queue  = Queue;

        PlayerNameText.Text = player?.DisplayName ?? "No player";
        PlayPauseButton.IsEnabled = player is not null;
        // Skipping is a queue operation when MA is driving the player; the player feature only matters for
        // external sources (a WiiM playing from its own app reports no next_previous at all)
        PreviousButton.IsEnabled  = queue is not null || player?.Supports("next_previous") == true;
        NextButton.IsEnabled      = PreviousButton.IsEnabled;

        // Now playing: prefer the MA queue item, fall back to whatever the player reports
        var item  = queue?.CurrentItem;
        var media = player?.CurrentMedia;

        TitleText.Text    = item?.Name ?? media?.Title ?? "Nothing playing";
        SubtitleText.Text = item?.SubtitleText ?? JoinNonEmpty(media?.Artist, media?.Album);
        TrimTip(TitleText);
        TrimTip(SubtitleText);

        var imageUrl = item is not null ? App.Client.ImageUrl(item.FindImage(), 160) : media?.ImageUrl;
        if (imageUrl != lastImageUrl)
        {
            lastImageUrl = imageUrl;
            Templates.Show(ArtImage, imageUrl, 64);
        }

        var playing = player?.IsPlaying == true;
        PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";

        // Loading overlay while a clicked item is being started
        var loading = App.PendingItem is not null;
        LoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive      = loading;

        // Favorite state of the current track
        var track = item?.MediaItem;
        LikeButton.IsEnabled = track is not null;
        LikeIcon.Glyph       = track?.Favorite == true ? "\uEB52" : "\uEB51";
        LikeIcon.Foreground  = track?.Favorite == true ? AccentBrush : DefaultBrush;

        // Sound quality chip: queue item stream details, else the player's live external source
        var fidelity = item?.Streamdetails?.AudioProcessing?.InputFidelity ?? player?.ActiveSourceAudio?.InputFidelity;
        var format   = item?.Streamdetails?.AudioFormat ?? player?.ActiveSourceAudio?.InputFormat;
        if (fidelity is { Label.Length: > 0 })
        {
            QualityChip.Visibility = Visibility.Visible;   // the Narrow visual state overrides this when the window is small
            QualityText.Text       = fidelity.Label;
            QualityDot.Fill        = new SolidColorBrush(ParseColor(fidelity.Color));
            ToolTipService.SetToolTip(QualityChip, format?.Text is { Length: > 0 } text ? text : fidelity.Quality);
        }
        else
        {
            QualityChip.Visibility = Visibility.Collapsed;
        }

        ShuffleButton.Foreground = queue?.ShuffleEnabled == true ? AccentBrush : DefaultBrush;
        RepeatButton.Foreground  = queue is { RepeatMode: not "off" } ? AccentBrush : DefaultBrush;
        RepeatIcon.Glyph         = queue?.RepeatMode == "one" ? "\uE8ED" : "\uE8EE";

        suppressVolume = true;
        // Server volume wins, except while the user's own change is still on its way (debounce running, or the send
        // went out under a second ago and its echo has not come back): resetting then would snap the slider back
        // under the pointer and lose wheel notches.
        if (!volumeTimer.IsRunning && DateTime.UtcNow - volumeSentAt > TimeSpan.FromSeconds(1))
        {
            VolumeSlider.Value       = player?.VolumeLevel ?? 0;
            VolumeFlyoutSlider.Value = VolumeSlider.Value;
            VolumePercentText.Text   = $"{(int)VolumeSlider.Value}%";
        }
        VolumeSlider.IsEnabled  = VolumeFlyoutSlider.IsEnabled = player?.Supports("volume_set") == true;
        MuteButton.IsEnabled    = player?.Supports("volume_mute") == true;
        VolumeFlyoutButton.IsEnabled = player?.Supports("volume_set") == true || player?.Supports("volume_mute") == true;
        PowerButton.Visibility  = player?.Supports("power") == true ? Visibility.Visible : Visibility.Collapsed;   // only players with a power control
        ShuffleButton.IsEnabled = queue is not null;
        RepeatButton.IsEnabled  = queue is not null;
        PowerButton.Opacity     = player?.Powered == false ? 0.5 : 1;
        VolumeIcon.Glyph        = player?.VolumeMuted == true ? "\uE74F" : "\uE767";
        VolumeFlyoutIcon.Glyph  = VolumeFlyoutMuteIcon.Glyph = VolumeIcon.Glyph;
        suppressVolume = false;

        UpdateProgress();
    }

    /// <summary>Full text as a tooltip, only while the ellipsis is actually cutting it off. Also wired to IsTextTrimmedChanged for resizes.</summary>
    private static void TrimTip(TextBlock text)
        => ToolTipService.SetToolTip(text, text.IsTextTrimmed ? text.Text : null);

    private void OnTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => TrimTip(sender);

    private void UpdateProgress()
    {
        if (isSeeking) return;

        var queue = Queue;
        double elapsed, duration;

        if (queue?.CurrentItem is { } item)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            elapsed  = queue.ElapsedTime + (queue.State == "playing" ? Math.Max(0, now - queue.ElapsedTimeLastUpdated) : 0);
            duration = item.Duration ?? 0;
        }
        else
        {
            elapsed  = 0;
            duration = Player?.CurrentMedia?.Duration ?? 0;
        }

        ProgressSlider.Maximum   = Math.Max(1, duration);
        ProgressSlider.Value     = Math.Min(elapsed, ProgressSlider.Maximum);
        // Queue playback seeks on the server by restarting the stream, so it works even for players without a native seek (the PC speaker)
        ProgressSlider.IsEnabled = duration > 0 && (queue?.CurrentItem is not null || Player?.Supports("seek") == true);
        ElapsedText.Text         = Format.Duration(elapsed);
        DurationText.Text        = duration > 0 ? Format.Duration(duration) : "--:--";
    }

    private static Brush AccentBrush  => (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
    private static Brush DefaultBrush => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    private static string JoinNonEmpty(params string?[] parts)
        => string.Join(" • ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static Windows.UI.Color ParseColor(string hex)
        => Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));

    // =========================================================================
    // COMMANDS
    // =========================================================================

    private static async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { App.Window.ShowMessage(ex.Message); }
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
        => _ = RunAsync(() => App.Client.PlayerCommandAsync(Player!.PlayerId, "play_pause"));

    private void OnNext(object sender, RoutedEventArgs e) => Skip("next");

    private void OnPrevious(object sender, RoutedEventArgs e) => Skip("previous");

    /// <summary>Skip through the MA queue when there is one, else ask the player itself (external source).</summary>
    private void Skip(string command)
        => _ = RunAsync(() => Queue is { } queue
            ? App.Client.QueueCommandAsync(queue.QueueId, command)
            : App.Client.PlayerCommandAsync(Player!.PlayerId, command));

    private void OnShuffle(object sender, RoutedEventArgs e)
    {
        if (Queue is not { } queue) return;
        _ = RunAsync(() => App.Client.QueueCommandAsync(queue.QueueId, "shuffle", new { shuffle_enabled = !queue.ShuffleEnabled }));
    }

    private void OnRepeat(object sender, RoutedEventArgs e)
    {
        if (Queue is not { } queue) return;
        var next = queue.RepeatMode switch { "off" => "all", "all" => "one", _ => "off" };
        _ = RunAsync(() => App.Client.QueueCommandAsync(queue.QueueId, "repeat", new { repeat_mode = next }));
    }

    private void OnQueue(object sender, RoutedEventArgs e) => App.Window.ToggleQueue();

    /// <summary>Mirror the Now Playing overlay state on the queue button.</summary>
    public void SetQueueOpen(bool open)
    {
        QueueButton.IsChecked = open;
        QueueIcon.Foreground  = open ? AccentBrush : DefaultBrush;
    }

    private async void OnLike(object sender, RoutedEventArgs e)
    {
        if (Queue?.CurrentItem?.MediaItem is not { } track) return;

        // Immediate feedback: flip the fill now and pop the icon; the server call confirms or reverts
        var willBe = !track.Favorite;
        LikeIcon.Glyph      = willBe ? "" : "";
        LikeIcon.Foreground = willBe ? AccentBrush : DefaultBrush;
        Pop(LikeScale);

        await App.ToggleFavoriteAsync(track);
        Refresh();
    }

    /// <summary>Quick 1.0 → 1.35 → 1.0 scale bounce.</summary>
    private static void Pop(ScaleTransform scale)
    {
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var frames = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames();
            frames.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(0),   Value = 1.0 });
            frames.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(110), Value = 1.35, EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } });
            frames.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(260), Value = 1.0,  EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } });
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(frames, scale);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(frames, property);
            storyboard.Children.Add(frames);
        }
        storyboard.Begin();
    }

    private void OnMute(object sender, RoutedEventArgs e)
        => _ = RunAsync(() => App.Client.PlayerCommandAsync(Player!.PlayerId, "volume_mute", new { muted = Player!.VolumeMuted != true }));

    private void OnPower(object sender, RoutedEventArgs e)
        => _ = RunAsync(() => App.Client.PlayerCommandAsync(Player!.PlayerId, "power", new { powered = Player!.Powered != true }));

    private void OnSeekCommitted(object sender, PointerRoutedEventArgs e)
    {
        // PointerCaptureLost also fires on layout changes (a window resize re-templates the slider), not only at the
        // end of a drag. Without a real press first this is not a seek: sending one restarted the stream on the WiiM
        // every time the window was resized.
        if (!isSeeking) return;
        isSeeking = false;
        if (Player is not { } player) return;
        var position = (int)ProgressSlider.Value;
        _ = Queue?.CurrentItem is not null
            ? RunAsync(() => App.Client.QueueCommandAsync(App.Client.QueueIdFor(player), "seek", new { position }))
            : RunAsync(() => App.Client.PlayerCommandAsync(player.PlayerId, "seek", new { position }));
    }

    /// <summary>Mouse wheel over the volume slider, mute button or compact volume button nudges the volume in 2% steps.</summary>
    private void OnVolumeWheel(object sender, PointerRoutedEventArgs e)
    {
        if (Player is not { } player || !player.Supports("volume_set")) return;
        var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;

        // Drive the inline slider; its ValueChanged runs the same debounced send as a drag. Works even while it is
        // collapsed on narrow windows (the wheel came from the mute or compact volume button instead).
        var next = Math.Clamp((int)Math.Round(VolumeSlider.Value) + (delta > 0 ? 2 : -2), 0, 100);
        VolumeSlider.Value = next;
        ShowVolumeTip((FrameworkElement)sender, next);
    }

    /// <summary>Pop a small tooltip with the percentage over whatever the wheel is on, and keep it up briefly after the last scroll.</summary>
    private void ShowVolumeTip(FrameworkElement owner, int value)
    {
        volumeTip ??= new ToolTip { Placement = PlacementMode.Top };

        // Move the tooltip to the element being scrolled, restoring the previous owner's own tooltip ("Mute", "Volume") first
        if (!ReferenceEquals(volumeTipOwner, owner))
        {
            HideVolumeTip();
            volumeTipSaved = ToolTipService.GetToolTip(owner);
            volumeTipOwner = owner;
            ToolTipService.SetToolTip(owner, volumeTip);
        }

        volumeTip.Content = $"{value}%";
        volumeTip.IsOpen  = true;
        volumeTipTimer.Stop();
        volumeTipTimer.Start();
    }

    private void HideVolumeTip()
    {
        volumeTipTimer.Stop();
        if (volumeTip is not null) volumeTip.IsOpen = false;
        if (volumeTipOwner is not null)
        {
            ToolTipService.SetToolTip(volumeTipOwner, volumeTipSaved);   // put the element's label tooltip back
            volumeTipOwner = null;
            volumeTipSaved = null;
        }
    }

    /// <summary>Both sliders (inline and flyout) route here; the one the user moved becomes the value to send.</summary>
    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (suppressVolume) return;
        pendingVolume = (int)e.NewValue;
        VolumePercentText.Text = $"{pendingVolume}%";

        // Keep the other slider in step, so the flyout thumb follows the wheel and a flyout drag leaves the inline
        // slider (the wheel's base value) current
        suppressVolume = true;
        if (ReferenceEquals(sender, VolumeSlider)) VolumeFlyoutSlider.Value = e.NewValue; else VolumeSlider.Value = e.NewValue;
        suppressVolume = false;

        volumeTimer.Stop();
        volumeTimer.Start();
    }

    private void OnVolumeFlyoutOpening(object? sender, object e)
    {
        suppressVolume = true;
        VolumeFlyoutSlider.Value = Player?.VolumeLevel ?? VolumeSlider.Value;
        VolumePercentText.Text   = $"{(int)VolumeFlyoutSlider.Value}%";
        suppressVolume = false;
    }

    private Task SendVolumeAsync()
    {
        if (Player is not { } player) return Task.CompletedTask;
        volumeSentAt = DateTime.UtcNow;
        var volume_level = pendingVolume;
        return RunAsync(() => App.Client.PlayerCommandAsync(player.PlayerId, "volume_set", new { volume_level }));
    }

    // Player Picker

    /// <summary>Fresh list each time the picker opens: check on the selected player, live bars on playing ones.</summary>
    private void OnPlayerMenuOpening(object? sender, object e)
    {
        var players = App.Client.Players.Values.Where(p => p.IsVisible).OrderBy(p => p.Name).ToList();
        PlayerList.ItemsSource       = players;
        PlayerList.Visibility        = players.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoPlayersText.Visibility     = players.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPlayerPicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Player player) App.SetActivePlayer(player.PlayerId);
        PlayerFlyout.Hide();
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => Templates.FadeIn((UIElement)sender);
}
