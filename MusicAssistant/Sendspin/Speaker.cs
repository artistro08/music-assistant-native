using System.Net;
using MusicAssistant.Remote;

namespace MusicAssistant.Sendspin;

/// <summary>
/// This PC as a Music Assistant speaker, from the app's point of view.
///
/// On the local network the speaker works like a standalone Sendspin speaker: it listens for connections, announces
/// itself over mDNS, and Music Assistant discovers it and connects. The server then lists it as a regular device (a
/// universal player) in every client, the web app and the Android app included. Away from home the server can't reach
/// this PC, so the speaker rides a "sendspin" data channel on the WebRTC connection instead. When the listener can't
/// start, or no server connects within a minute, it falls back to the server's authenticated /sendspin proxy.
///
/// Owns the Sendspin identity and one admitted <see cref="SendspinPlayer"/> at a time, and mirrors its state for
/// Settings. Events fire on the UI thread.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://github.com/Sendspin/spec/blob/main/connection.md#multiple-servers-server-initiated
/// </remarks>
public sealed class Speaker
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>How long a listening speaker waits for a server before connecting through the server's proxy instead.</summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a server's connection may stay unadmitted (no server/activate yet) before it is dropped, per the spec.</summary>
    private static readonly TimeSpan ProvisionalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Most unadmitted connections held at once; further ones are closed right away, which the spec allows. Anything on
    /// the network can connect to the listener, so without a cap it could make the app hold any number of sessions.
    /// </summary>
    private const int MaxProvisional = 2;

    private readonly Identity identity = new();

    /// <summary>Guards the admitted session and the provisional ones.</summary>
    private readonly object sessionsGate = new();

    /// <summary>Server connections that finished the upgrade but have not been admitted by their first server/activate.</summary>
    private readonly List<SendspinPlayer> provisional = [];

    /// <summary>The admitted session: the one server this speaker plays for.</summary>
    private SendspinPlayer? player;

    private CancellationTokenSource? loop;
    private SendspinListener? listener;
    private MdnsAdvertiser? advertiser;
    private int attempt;

    /// <summary>Whether the user has turned the speaker feature on in Settings.</summary>
    public bool   Enabled   => App.Settings.SpeakerEnabled;

    /// <summary>Whether the speaker is listening for servers or running its connect-and-reconnect loop.</summary>
    public bool   Running   => loop is not null;

    /// <summary>Whether the current player has an encrypted session with the server.</summary>
    public bool   Connected => player?.Connected == true;

    /// <summary>Whether the current player is receiving a stream.</summary>
    public bool   Playing   => player?.Playing == true;

    /// <summary>This PC's Sendspin client_id, the id of the speaker's protocol player on the server.</summary>
    public string ClientId  => identity.ClientId;

    /// <summary>Short status text for Settings, such as "Off", "Waiting for Music Assistant…" or "Playing".</summary>
    public string Status    { get; private set; } = "Off";

    /// <summary>Raised on the UI thread whenever the speaker state changes.</summary>
    public event Action? Changed;

    /// <summary>The speaker's name in Music Assistant: the PC's host name, as Windows spells it.</summary>
    private static string Name => Dns.GetHostName();

    /// <summary>Loads the Sendspin identity and records its client_id as this app's own player id.</summary>
    public Speaker()
    {
        // The identity is the id the server knows this PC's speaker by.
        if (App.Settings.SpeakerClientId != identity.ClientId)
        {
            App.Settings.SpeakerClientId = identity.ClientId;
            App.Settings.Save();
        }
        Api.Player.OwnPlayerId = identity.ClientId;
    }

    /// <summary>(Re)start the speaker for the current server connection, or stop it when the feature is off.</summary>
    /// <returns>A completed task; listening and reconnecting continue in the background.</returns>
    public Task SyncAsync()
    {
        Stop(notify: false);
        if (!Enabled || !App.Client.IsConnected || (App.Settings.GetToken() is null))
        {
            SetStatus("Off");
            return Task.CompletedTask;
        }

        loop = new CancellationTokenSource();
        if (App.Client.Transport is WebRtcTransport || !TryListen(loop.Token))
        {
            _ = RunDialAsync(loop.Token);
        }
        return Task.CompletedTask;
    }

    /// <summary>Stops listening and reconnecting, disconnects every session and reports the speaker as off.</summary>
    public void Stop() => Stop(notify: true);

    private void Stop(bool notify)
    {
        loop?.Cancel();
        loop = null;
        StopListening();

        List<SendspinPlayer> sessions;
        lock (sessionsGate)
        {
            sessions = [.. provisional];
            provisional.Clear();
            if (player is not null)
            {
                sessions.Add(player);
            }
            player = null;
        }

        // Synchronous: Stop runs on the UI thread, and on exit the goodbye has to go out before the process ends.
        foreach (SendspinPlayer session in sessions)
        {
            End(session, "user_request");
        }

        attempt = 0;
        if (notify)
        {
            SetStatus("Off");
        }
    }

    // =========================================================================
    // LISTENING (SERVER INITIATED CONNECTIONS)
    // =========================================================================

    /// <summary>Starts the listener and the mDNS announcement; returns false when either can't start, so the caller dials instead.</summary>
    /// <param name="ct">Cancelled when the speaker stops.</param>
    /// <returns>Whether the speaker is now listening.</returns>
    private bool TryListen(CancellationToken ct)
    {
        string host = new Uri(App.Client.BaseUrl).Host;
        IPAddress? local = MdnsAdvertiser.LocalAddressFor(host);
        if (local is null)
        {
            App.Log($"Speaker: no IPv4 route to {host}; connecting through the server instead of listening");
            return false;
        }

        try
        {
            listener = new SendspinListener();
            listener.Accepted += OnAccepted;
            listener.Start();

            advertiser = new MdnsAdvertiser();
            advertiser.Register(identity.ClientId, Name, listener.Port, local);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            App.Log($"Speaker: can't listen for Music Assistant ({ex.Message}); connecting through the server instead");
            StopListening();
            return false;
        }

        App.Debug($"Speaker: listening on {local}:{listener.Port}");
        SetStatus("Waiting for Music Assistant…");
        _ = FallBackUnlessDiscoveredAsync(ct);
        return true;
    }

    /// <summary>Connects through the server's proxy when no server has connected to the listener within <see cref="DiscoveryTimeout"/>.</summary>
    /// <param name="ct">Cancelled when the speaker stops.</param>
    /// <returns>A task that completes once discovery succeeded or the fallback started.</returns>
    private async Task FallBackUnlessDiscoveredAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DiscoveryTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (sessionsGate)
        {
            if ((player is not null) || (provisional.Count > 0))
            {
                return;
            }
        }

        // Not both at once: the spec forbids connecting to servers while advertising.
        App.Log("Speaker: Music Assistant didn't connect to this PC; connecting through the server instead");
        StopListening();
        _ = RunDialAsync(ct);
    }

    private void StopListening()
    {
        advertiser?.Dispose();
        advertiser = null;
        if (listener is not null)
        {
            listener.Accepted -= OnAccepted;
            listener.Dispose();
            listener = null;
        }
    }

    /// <summary>A server connected: start its session as provisional until its first server/activate decides admission.</summary>
    /// <param name="socket">The upgraded connection.</param>
    private void OnAccepted(ISendspinSocket socket)
    {
        var session = new SendspinPlayer(identity, socket, Name, remote: false);
        lock (sessionsGate)
        {
            if (provisional.Count >= MaxProvisional)
            {
                App.Debug("Speaker: too many unadmitted server connections; closing the newest");
                session.Dispose();
                return;
            }
            provisional.Add(session);
        }

        session.Activated    += OnActivated;
        session.StateChanged += () => OnSessionStateChanged(session);
        _ = RunAcceptedAsync(session);
    }

    /// <summary>Runs the handshake of an accepted connection and drops it when it isn't admitted in time.</summary>
    /// <param name="session">The provisional session.</param>
    /// <returns>A task that completes once the session is admitted, dropped or closed.</returns>
    private async Task RunAcceptedAsync(SendspinPlayer session)
    {
        // The 30 seconds run from the connection, not from the end of the handshake.
        var deadline = Task.Delay(ProvisionalTimeout);
        try
        {
            using var timeout = new CancellationTokenSource(ProvisionalTimeout);
            await session.ConnectAsync(timeout.Token);
            await deadline;
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // A task per server connection: a failed handshake only drops that connection.
            App.Debug($"Speaker: server connection failed: {ex.Message}");
        }

        bool stillProvisional;
        lock (sessionsGate)
        {
            stillProvisional = provisional.Remove(session);
        }

        if (stillProvisional)
        {
            Release(session, "user_request");
        }
    }

    /// <summary>
    /// Applies the spec's admission rules on a connection's first server/activate: the higher-ranked activity wins
    /// (playback over pairing over none), ties go to the newcomer, a pairing attempt is never displaced, and between two
    /// idle connections only the last server that played here is let back in.
    /// </summary>
    /// <param name="incoming">The session that was activated.</param>
    /// <param name="first">Whether this is its first activation; later ones never trigger admission.</param>
    private void OnActivated(SendspinPlayer incoming, bool first)
    {
        SendspinPlayer? displaced = null;
        bool admitted;
        lock (sessionsGate)
        {
            if (first && provisional.Remove(incoming))
            {
                SendspinPlayer? current = player;
                admitted = (current is null) || !current.Connected || Wins(incoming, current);
                if (admitted)
                {
                    displaced = current;
                    player    = incoming;
                }
            }
            else
            {
                admitted = player == incoming;
            }
        }

        if (!admitted)
        {
            App.Debug($"Speaker: turned away server {incoming.ServerId[..8]}…, another server holds this speaker");
            incoming.Reject();
            Release(incoming, goodbye: null);
            return;
        }

        if (displaced is not null)
        {
            App.Debug($"Speaker: server {incoming.ServerId[..8]}… took over from {displaced.ServerId[..8]}…");
            Release(displaced, "another_server");
        }

        // Persisted so an idle reconnect from this server is let back in over another idle server.
        if (incoming.Activities.Contains("playback") && (App.Settings.SpeakerLastPlaybackServerId != incoming.ServerId))
        {
            App.Settings.SpeakerLastPlaybackServerId = incoming.ServerId;
            App.Settings.Save();
        }
        SetStatus(Describe(incoming));
    }

    /// <summary>Whether an incoming connection displaces the admitted one.</summary>
    /// <param name="incoming">The newly activated connection.</param>
    /// <param name="current">The admitted connection.</param>
    /// <returns>Whether the incoming connection wins.</returns>
    private static bool Wins(SendspinPlayer incoming, SendspinPlayer current)
    {
        if (current.Activities.Contains("pairing"))
        {
            return false;
        }

        int incomingRank = Rank(incoming.Activities);
        int currentRank  = Rank(current.Activities);
        if ((incomingRank == 0) && (currentRank == 0))
        {
            string? last = App.Settings.SpeakerLastPlaybackServerId;
            return (incoming.ServerId == last) && (current.ServerId != last);
        }

        return incomingRank >= currentRank;
    }

    /// <summary>Ranks a connection by its highest activity: playback, then pairing, then none.</summary>
    /// <param name="activities">The activities from server/activate.</param>
    /// <returns>2, 1 or 0.</returns>
    private static int Rank(IReadOnlyList<string> activities)
    {
        if (activities.Contains("playback"))
        {
            return 2;
        }

        return activities.Contains("pairing") ? 1 : 0;
    }

    private void OnSessionStateChanged(SendspinPlayer session)
    {
        bool wasAdmitted;
        lock (sessionsGate)
        {
            wasAdmitted = player == session;
            if (wasAdmitted && !session.Connected && (session.LastError is not null))
            {
                player = null;
            }
        }

        if (!wasAdmitted)
        {
            return;
        }

        if (player == session)
        {
            SetStatus(Describe(session));
            return;
        }

        // The admitted server went away; it reconnects on its own (a server restart, a network blip).
        App.Debug($"Speaker: session dropped: {session.LastError}");
        Release(session, goodbye: null);
        SetStatus(listener is null ? "Disconnected" : "Waiting for Music Assistant…");
    }

    // =========================================================================
    // DIALING (REMOTE, OR NO SERVER FOUND THIS PC)
    // =========================================================================

    /// <summary>Connect and stay connected; reconnect with backoff while the feature and the app connection are up.</summary>
    /// <param name="ct">Cancelled when the speaker stops.</param>
    /// <returns>A task that runs until the speaker stops.</returns>
    private async Task RunDialAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool connectedOnce = false;
            try
            {
                SetStatus("Connecting…");
                ISendspinSocket socket = CreateDialSocket();
                var session = new SendspinPlayer(identity, socket, Name, App.Client.IsRemote);

                // Only a drop after the session was up counts: StateChanged also fires mid-handshake, before Connected is set.
                session.StateChanged += () =>
                {
                    if (connectedOnce && !session.Connected)
                    {
                        App.Debug($"Speaker: session dropped: {session.LastError ?? "closed"}");
                        closed.TrySetResult();
                    }
                    SetStatus(Describe(session));
                };
                lock (sessionsGate)
                {
                    player = session;
                }

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await session.ConnectAsync(timeout.Token);
                }
                connectedOnce = true;
                attempt       = 0;

                // ConnectAsync may have completed just as the session dropped; if so, react now.
                if (!session.Connected)
                {
                    closed.TrySetResult();
                }

                // Runs until the session drops.
                await closed.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                App.Debug($"Speaker: {ex.Message}");
                SetStatus(ex.Message);
            }

            SendspinPlayer? ended;
            lock (sessionsGate)
            {
                ended  = player;
                player = null;
            }
            if (ended is not null)
            {
                Release(ended, goodbye: null);
            }

            if (ct.IsCancellationRequested || !App.Client.IsConnected)
            {
                SetStatus("Off");
                return;
            }

            TimeSpan delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            SetStatus($"Reconnecting in {delay.TotalSeconds:0}s…");
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private ISendspinSocket CreateDialSocket()
    {
        if (App.Client.Transport is WebRtcTransport remote)
        {
            return remote.Peer.OpenChannelSocket("sendspin");
        }

        return new ProxyWebSocket(App.Client.BaseUrl, App.Settings.GetToken()!, identity.ClientId);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>Ends a session off the calling thread, for callers running inside a session's own socket callbacks.</summary>
    /// <param name="session">The session to end.</param>
    /// <param name="goodbye">The client/goodbye reason, or null when the session already closed or said goodbye.</param>
    private static void Release(SendspinPlayer session, string? goodbye)
    {
        // Disposing inside a socket callback would tear down the thread that is running it.
        ThreadPool.QueueUserWorkItem(_ => End(session, goodbye));
    }

    /// <summary>Closes a session, sending client/goodbye with the given reason if any, and frees it.</summary>
    /// <param name="session">The session to end.</param>
    /// <param name="goodbye">The client/goodbye reason, or null when the session already closed or said goodbye.</param>
    private static void End(SendspinPlayer session, string? goodbye)
    {
        try
        {
            if (goodbye is not null)
            {
                session.Disconnect(goodbye);
            }
            session.Dispose();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Debug($"Speaker cleanup: {ex.Message}");
        }
    }

    private static string Describe(SendspinPlayer session)
    {
        if (!session.Connected)
        {
            return session.LastError ?? "Disconnected";
        }

        if (session.Playing)
        {
            return "Playing";
        }

        return session.TimeSynced ? "Ready" : "Syncing clock…";
    }

    private void SetStatus(string status)
    {
        Status = status;
        App.Dispatcher.TryEnqueue(() => Changed?.Invoke());
    }
}
