using System.Text.Json;
using MusicAssistant.Api;
using MusicAssistant.Remote;

namespace MusicAssistant.Sendspin;

/// <summary>
/// This PC as a Music Assistant speaker, from the app's point of view.
///
/// Owns the Sendspin identity and one <see cref="SendspinPlayer"/> at a time.
/// Locally the player talks to the server's authenticated /sendspin proxy;
/// remotely it rides a "sendspin" data channel on the WebRTC connection.
/// Keeps the player connected while the feature is on and the app is
/// connected, asks the server to pair it (the Web Player flow), and mirrors
/// its state for Settings. All events fire on the UI thread.
/// </summary>
public sealed class Speaker
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly Identity identity = new();
    private SendspinPlayer? player;
    private CancellationTokenSource? loop;
    private int attempt;

    public bool   Enabled   => App.Settings.SpeakerEnabled;
    public bool   Running   => loop is not null;
    public bool   Connected => player?.Connected == true;
    public bool   Playing   => player?.Playing == true;
    public string ClientId  => identity.ClientId;
    public string Status    { get; private set; } = "Off";

    /// <summary>Raised on the UI thread whenever the speaker state changes.</summary>
    public event Action? Changed;

    public Speaker()
    {
        // The identity is the player id the server lists this PC under
        if (App.Settings.SpeakerClientId != identity.ClientId)
        {
            App.Settings.SpeakerClientId = identity.ClientId;
            App.Settings.Save();
        }
        Player.OwnPlayerId = identity.ClientId;
    }

    /// <summary>(Re)start the speaker for the current server connection, or stop it when the feature is off.</summary>
    public Task SyncAsync()
    {
        Stop(notify: false);
        if (!Enabled || !App.Client.IsConnected || App.Settings.GetToken() is null)
        {
            SetStatus("Off");
            return Task.CompletedTask;
        }
        loop = new CancellationTokenSource();
        _ = RunAsync(loop.Token);
        return Task.CompletedTask;
    }

    public void Stop() => Stop(notify: true);

    private void Stop(bool notify)
    {
        loop?.Cancel();
        loop = null;
        // Exchange so only one caller ever disposes: Stop and the reconnect loop can both run at a session drop
        SendspinPlayer? current = Interlocked.Exchange(ref player, null);
        if (current is not null)
        {
            try { current.Disconnect("user_request"); current.Dispose(); } catch (Exception ex) { App.Debug("Speaker stop: " + ex.Message); }
        }
        attempt = 0;
        if (notify) SetStatus("Off");
    }

    /// <summary>Connect, pair, and stay connected; reconnect with backoff while the feature and the app connection are up.</summary>
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool connectedOnce = false;
            try
            {
                SetStatus("Connecting…");
                ISendspinSocket socket = CreateSocket();
                var session = new SendspinPlayer(identity, socket, Environment.MachineName, App.Client.IsRemote);
                // Only a drop after the session was up counts: StateChanged also fires mid-handshake, before Connected is set
                session.StateChanged += () =>
                {
                    if (connectedOnce && !session.Connected) { App.Debug("Speaker: session dropped: " + (session.LastError ?? "closed")); closed.TrySetResult(); }
                    SetStatus(Describe(session));
                };
                player = session;

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await session.ConnectAsync(timeout.Token);
                }
                connectedOnce = true;
                attempt = 0;
                await PairAsync(session);

                // ConnectAsync may have completed just as the session dropped; if so, react now
                if (!session.Connected) closed.TrySetResult();
                await closed.Task.WaitAsync(ct);   // runs until the session drops
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                App.Debug("Speaker: " + ex.Message);
                SetStatus(ex.Message);
            }

            try { Interlocked.Exchange(ref player, null)?.Dispose(); } catch (Exception ex) { App.Debug("Speaker cleanup: " + ex.Message); }
            if (ct.IsCancellationRequested || !App.Client.IsConnected) { SetStatus("Off"); return; }

            TimeSpan delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            SetStatus($"Reconnecting in {delay.TotalSeconds:0}s…");
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private ISendspinSocket CreateSocket()
    {
        if (App.Client.Transport is WebRtcTransport remote) return remote.Peer.OpenChannelSocket("sendspin");
        return new ProxyWebSocket(App.Client.BaseUrl, App.Settings.GetToken()!, identity.ClientId);
    }

    /// <summary>Hand the pairing token to the server; it pairs this player without an operator step (a no-op when already paired).</summary>
    private static async Task PairAsync(SendspinPlayer session)
    {
        try
        {
            await App.Client.SendAsync<JsonElement>("sendspin/pair_web_player", new { pairing_token = session.PairingToken });
        }
        catch (ApiException ex)
        {
            App.Log("Speaker pairing: " + ex.Message);
        }
    }

    private static string Describe(SendspinPlayer session)
    {
        if (!session.Connected) return session.LastError ?? "Disconnected";
        if (session.Playing) return "Playing";
        if (!session.TimeSynced) return "Syncing clock…";
        return session.Paired ? "Ready" : "Ready (pairing…)";
    }

    private void SetStatus(string status)
    {
        Status = status;
        App.Dispatcher.TryEnqueue(() => Changed?.Invoke());
    }
}

