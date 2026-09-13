using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace MusicAssistant.Sendspin;

/// <summary>
/// A Sendspin player@v1 client: this PC as a Music Assistant speaker.
///
/// Drives one <see cref="SendspinConnection"/>: answers server/hello with the
/// player's capabilities, reports state, keeps the clock synchronized with
/// NTP-style bursts, decodes audio chunks into the scheduler and completes the
/// Pairing PSK flow when the server (told by the app through the API) offers
/// it. Presents itself as Music Assistant's built-in "Web Player" so the
/// server pairs and lists it the way it does the browser player.
/// </summary>
/// <remarks>
/// @link https://github.com/Sendspin/spec/blob/main/roles/player/v1.md
/// @link https://github.com/Sendspin/spec/blob/main/pairing.md#pairing-psk-flow
/// </remarks>
public sealed class SendspinPlayer : IDisposable
{
    private const int    TimeSyncBurstSize     = 8;
    private const int    TimeSyncBurstInterval = 10_000;
    private const int    TimeSyncProbeTimeout  = 2_000;
    private const int    StateInterval         = 5_000;
    private const int    BufferCapacityBytes   = 4 * 1024 * 1024;

    private readonly Identity           identity;
    private readonly SendspinConnection connection;
    private readonly string             name;
    private readonly bool               remote;
    private readonly TimeFilter         timeFilter;
    private readonly object             gate = new();

    private WasapiOutput?   output;
    private AudioScheduler? scheduler;
    private ChunkDecoder?   decoder;
    private Timer?          stateTimer;
    private Timer?          timeSyncTimer;
    private Timer?          probeTimer;

    // Protocol state
    private bool     activated;
    private bool     playerRoleActive;
    private bool     availableReported;
    private bool     pairingInFlight;
    private byte[]?  pendingLongTermPsk;
    private int      volume = 100;
    private bool     muted;
    private int      staticDelayMs;

    // Time sync burst
    private readonly List<(double Measurement, double MaxError, long T4, double Rtt)> burstSamples = [];
    private long     probeInFlight;   // client_transmitted of the outstanding probe, 0 when none
    private int      burstSent;
    private bool     burstActive;

    public event Action? StateChanged;

    public string ClientId     => identity.ClientId;
    public string PairingToken => identity.PairingToken;
    public bool   Connected    { get; private set; }
    public bool   Playing      { get; private set; }
    public bool   Paired       => connection.Matched?.Category == Identity.PskCategory.LongTerm;
    public bool   TimeSynced   => timeFilter.IsSynchronized;
    public int    Volume       => volume;
    public bool   Muted        => muted;
    public long   SyncErrorUs  => scheduler?.SyncErrorUs ?? 0;
    public double ClockErrorUs => timeFilter.Error;
    public string? LastError   { get; private set; }

    public SendspinPlayer(Identity identity, ISendspinSocket socket, string name, bool remote)
    {
        this.identity = identity;
        this.name     = name;
        this.remote   = remote;
        timeFilter = new TimeFilter(0, 1.1, 2.0);
        connection = new SendspinConnection(socket, identity);
        connection.HandshakeCompleted += OnHandshake;
        connection.ControlReceived    += OnControl;
        connection.BinaryReceived     += OnBinary;
        connection.Closed             += OnClosed;
    }

    /// <summary>Open the audio device, connect and finish the handshake. Returns when the encrypted session is up.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        output = new WasapiOutput((buffer, frames, time) => scheduler?.Render(buffer, frames, time));
        output.Start();
        scheduler = new AudioScheduler(timeFilter, output.SampleRate, output.Channels, remote);
        decoder   = new ChunkDecoder(output.SampleRate, output.Channels);
        ApplyGain();

        await connection.StartAsync(ct);
        Connected = true;
        Notify();
    }

    public void Disconnect(string goodbye = "user_request")
    {
        connection.Close("Disconnected", goodbye);
    }

    // =========================================================================
    // HANDSHAKE AND HELLO
    // =========================================================================

    private void OnHandshake(SendspinConnection.HandshakeInfo info)
    {
        App.Debug($"Speaker: {(info.IsRehandshake ? "re-handshake" : "handshake")} done with {info.Matched.Category} key, server {info.ServerId[..8]}…");
        lock (gate)
        {
            // A (re)handshake restarts the activation sequence; pairing state that belonged to the old keys is void
            activated = false;
            availableReported = false;
            pairingInFlight = false;
            pendingLongTermPsk = null;
            StopTimers();
        }
        Notify();
    }

    private void SendClientHello()
    {
        // A dictionary because one key ("player@v1_support") is not a valid identifier
        var hello = new Dictionary<string, object?>
        {
            ["name"]            = name,
            ["supported_roles"] = new[] { "player@v1" },
            ["device_info"]     = new
            {
                product_name     = "Web Player",   // what the server recognizes as its built-in player and pairs through the API
                manufacturer     = "Music Assistant",
                software_version = "Music Assistant for Windows " + (typeof(SendspinPlayer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"),
            },
            ["player@v1_support"] = new
            {
                supported_formats  = SupportedFormats(),
                buffer_capacity    = BufferCapacityBytes,
                supported_commands = new[] { "volume", "mute" },
            },
            ["supported_pair_methods"] = new[] { new { method = "pairing_psk", locations = new[] { "operator" } } },
            ["unpaired_access"]        = new { enabled = true },
        };
        connection.SendControl("client/hello", hello);
    }

    /// <summary>Formats in preference order: the device's own rate as PCM on the LAN, Opus first over the internet.</summary>
    private object[] SupportedFormats()
    {
        var rate = output?.SampleRate ?? 48000;
        var pcm  = new { codec = "pcm",  channels = 2, sample_rate = rate,  bit_depth = 16 };
        var opus = new { codec = "opus", channels = 2, sample_rate = 48000, bit_depth = 16 };
        var pcm48 = new { codec = "pcm", channels = 2, sample_rate = 48000, bit_depth = 16 };
        return remote ? [pcm, opus, pcm48] : rate == 48000 ? [pcm, opus] : [pcm, opus, pcm48];
    }

    // =========================================================================
    // SERVER MESSAGES
    // =========================================================================

    private void OnControl(string type, JsonElement payload)
    {
        try
        {
            switch (type)
            {
                case "server/hello":     SendClientHello(); break;
                case "server/activate":  OnActivate(payload); break;
                case "server/time":      OnServerTime(payload); break;
                case "stream/start":     OnStreamStart(payload); break;
                case "stream/clear":     if (RolesInclude(payload, "player")) scheduler?.Clear(); break;
                case "stream/end":       OnStreamEnd(payload); break;
                case "server/command":   OnCommand(payload); break;
                case "server/pair-finalize": OnPairFinalize(); break;
                case "pair/abort":       OnPairAbort(payload); break;
                case "group/update":
                case "server/state":     break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            App.Debug($"Speaker: bad {type} message: {ex.Message}");
        }
    }

    private static bool RolesInclude(JsonElement payload, string role)
        => payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("roles", out var roles) || roles.EnumerateArray().Any(r => r.GetString() == role);

    private void OnActivate(JsonElement payload)
    {
        var activities = payload.TryGetProperty("activities", out var a) ? a.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        if (payload.TryGetProperty("active_roles", out var roles)) playerRoleActive = roles.EnumerateArray().Any(r => r.GetString() == "player@v1");

        if (activities.Contains("pairing"))
        {
            StartPairing(payload);
            return;
        }

        lock (gate)
        {
            if (activated) { SendState(); return; }
            activated = true;
        }
        App.Debug($"Speaker: activated ({string.Join(",", activities)}) roles player={playerRoleActive} key={connection.Matched?.Category}");
        SendState();
        StartTimeSync();
        stateTimer = new Timer(_ => { SendState(); LogStatus(); }, null, StateInterval, StateInterval);
        Notify();
    }

    private int statusTicks;

    /// <summary>Every 10 seconds while playing: how far the output sits from the target and how good the clock estimate is.</summary>
    private void LogStatus()
    {
        if (!Playing || ++statusTicks % 2 != 0) return;
        App.Debug($"Speaker: sync error {SyncErrorUs}us, clock ±{ClockErrorUs:0}us (drift {timeFilter.Drift * 1e6:0.0} ppm, n={timeFilter.Count}), resyncs {scheduler?.Resyncs}, late chunks {scheduler?.DroppedLateChunks}, buffered {(scheduler?.HasAudio == true ? "yes" : "no")}, peak {scheduler?.TakePeak():0.000}");
    }

    private void OnStreamStart(JsonElement payload)
    {
        if (!payload.TryGetProperty("player", out var player)) return;
        var format = new ChunkDecoder.Format(
            player.GetProperty("codec").GetString() ?? "pcm",
            player.GetProperty("sample_rate").GetInt32(),
            player.GetProperty("channels").GetInt32(),
            player.TryGetProperty("bit_depth", out var bd) ? bd.GetInt32() : 16);

        var update = decoder?.IsConfigured == true;
        decoder?.Configure(format);
        if (!update) scheduler?.Clear();   // a new stream starts from an empty buffer; a format update keeps the timeline
        App.Debug($"Speaker: stream {(update ? "format update" : "start")} {format.Codec} {format.SampleRate} Hz {format.Channels} ch {format.BitDepth} bit");
        Playing = true;
        Notify();
    }

    private void OnStreamEnd(JsonElement payload)
    {
        if (!RolesInclude(payload, "player")) return;
        scheduler?.Clear();
        decoder?.Reset();
        Playing = false;
        SendState();
        Notify();
    }

    private void OnCommand(JsonElement payload)
    {
        if (!payload.TryGetProperty("player", out var player)) return;
        switch (player.GetProperty("command").GetString())
        {
            case "volume":           volume = Math.Clamp(player.GetProperty("volume").GetInt32(), 0, 100); break;
            case "mute":             muted  = player.GetProperty("mute").GetBoolean(); break;
            case "set_static_delay": SetStaticDelay(player.GetProperty("static_delay_ms").GetInt32()); break;
            default: return;
        }
        ApplyGain();
        stateTimer?.Change(StateInterval, StateInterval);
        SendState();
        Notify();
    }

    private void SetStaticDelay(int ms)
    {
        staticDelayMs = Math.Clamp(ms, 0, 5000);
        if (scheduler is not null) scheduler.OutputDelayUs = staticDelayMs * 1000L;
    }

    /// <summary>Perceived loudness to amplitude: (volume / 100) ^ 1.5, per the player role specification.</summary>
    private void ApplyGain() => scheduler?.SetGain(muted ? 0f : MathF.Pow(volume / 100f, 1.5f));

    private void OnBinary(byte[] frame)
    {
        if (frame[0] != 4 || frame.Length < 13 || decoder is null || scheduler is null) return;   // 4 = player audio chunk
        if (!playerRoleActive) return;

        var serverTime = BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(1, 8));
        var decoded = decoder.Decode(frame.AsSpan(13));
        if (decoded is not { Frames: > 0 } chunk) return;
        scheduler.Enqueue(new AudioScheduler.Chunk(chunk.Samples, chunk.Frames, serverTime, scheduler.CurrentGeneration));
    }

    // =========================================================================
    // STATE
    // =========================================================================

    /// <summary>client/state: available once the clock is synchronized, plus the player object the server keys playback on.</summary>
    private void SendState()
    {
        if (!connection.Ready) return;
        var synced = timeFilter.IsSynchronized;
        connection.SendControl("client/state", new
        {
            available = synced,
            player = new
            {
                volume,
                muted,
                static_delay_ms       = staticDelayMs,
                // Buffer over the relay so jittery, head-of-line-blocked chunks arrive before their play time
                required_lead_time_ms = remote ? 1500 : 250,
                min_buffer_ms         = remote ? 1500 : 500,
                supported_commands    = new[] { "set_static_delay" },
            },
        });
        if (synced) availableReported = true;
    }

    /// <summary>The app asks the server to pair this player; the server then re-handshakes with the pairing PSK and activates pairing.</summary>
    private void StartPairing(JsonElement payload)
    {
        var method = payload.TryGetProperty("pairing", out var p) && p.TryGetProperty("method", out var m) ? m.GetString() : null;
        if (method != "pairing_psk" || connection.Matched?.Category != Identity.PskCategory.Pairing)
        {
            connection.SendControl("pair/abort", new { reason = "method_not_supported" });
            return;
        }
        lock (gate)
        {
            StopTimers();
            activated = false;
            pairingInFlight = true;
            pendingLongTermPsk = RandomNumberGenerator.GetBytes(NoiseCrypto.KeySize);
        }
        connection.SendControl("client/pair-finalize", new { long_term_psk = Base64Url.Encode(pendingLongTermPsk!) });
    }

    private void OnPairFinalize()
    {
        byte[]? psk;
        lock (gate)
        {
            psk = pendingLongTermPsk;
            pendingLongTermPsk = null;
            pairingInFlight = false;
        }
        if (psk is null) return;
        identity.AddPairingRecord(connection.ServerId, psk);   // the server now re-handshakes to this key
        Notify();
    }

    private void OnPairAbort(JsonElement payload)
    {
        lock (gate) { pairingInFlight = false; pendingLongTermPsk = null; }
        LastError = "Pairing aborted: " + (payload.TryGetProperty("reason", out var r) ? r.GetString() : "unknown");
        App.Debug("Speaker: " + LastError);
    }

    // =========================================================================
    // TIME SYNC
    // =========================================================================

    private void StartTimeSync()
    {
        StartBurst();
        timeSyncTimer = new Timer(_ => StartBurst(), null, TimeSyncBurstInterval, TimeSyncBurstInterval);
    }

    private void StartBurst()
    {
        lock (gate)
        {
            if (burstActive) return;
            burstActive = true;
            burstSent = 0;
            burstSamples.Clear();
            probeInFlight = 0;
        }
        SendProbe();
    }

    private void SendProbe()
    {
        long t1;
        lock (gate)
        {
            if (!burstActive || probeInFlight != 0) return;
            if (burstSent >= TimeSyncBurstSize) { FinishBurst(); return; }
            t1 = Clock.NowUs();
            probeInFlight = t1;
            burstSent++;
        }
        connection.SendControl("client/time", new { client_transmitted = t1 });
        probeTimer?.Dispose();
        // A single slow probe (over the relay, control messages queue behind audio on the ordered channel) is
        // skipped, not fatal: move on to the next one and still use the samples the burst did collect.
        probeTimer = new Timer(_ => OnProbeTimeout(t1), null, TimeSyncProbeTimeout, Timeout.Infinite);
    }

    private void OnProbeTimeout(long t1)
    {
        lock (gate)
        {
            if (!burstActive || probeInFlight != t1) return;
            probeInFlight = 0;
            if (burstSent >= TimeSyncBurstSize) { FinishBurst(); return; }
        }
        SendProbe();
    }

    private void OnServerTime(JsonElement payload)
    {
        var t4 = Clock.NowUs();
        var t1 = payload.GetProperty("client_transmitted").GetInt64();
        var t2 = payload.GetProperty("server_received").GetInt64();
        var t3 = payload.GetProperty("server_transmitted").GetInt64();

        lock (gate)
        {
            if (!burstActive || probeInFlight != t1) return;
            probeInFlight = 0;
            var measurement = ((t2 - t1) + (t3 - t4)) / 2.0;
            var rtt         = Math.Max(0, (t4 - t1) - (t3 - t2));
            burstSamples.Add((measurement, Math.Max(1000, rtt / 2.0), t4, rtt));
            if (burstSent >= TimeSyncBurstSize) { FinishBurst(); return; }
        }
        SendProbe();
    }

    /// <summary>Feed the filter the median offset of the three lowest-latency probes, as the reference clients do. Caller holds the lock.</summary>
    private void FinishBurst()
    {
        burstActive = false;
        probeInFlight = 0;
        if (burstSamples.Count > 0)
        {
            // Keep only samples whose round trip is near the burst minimum: a higher RTT means the probe or its
            // reply queued behind audio on the ordered channel, which also skews its one-way offset estimate.
            var minRtt = burstSamples.Min(s => s.Rtt);
            var clean  = burstSamples.Where(s => s.Rtt <= minRtt * 1.5 + 2000).OrderBy(s => s.Measurement).ToList();
            var pick   = clean[clean.Count / 2];
            var wasSynced = timeFilter.IsSynchronized;
            timeFilter.Update(pick.Measurement, pick.MaxError, pick.T4);
            if (!wasSynced && timeFilter.IsSynchronized) ThreadPool.QueueUserWorkItem(_ => { SendState(); Notify(); });   // now available
        }
        burstSamples.Clear();
    }

    // =========================================================================
    // LIFECYCLE
    // =========================================================================

    private void OnClosed(string reason)
    {
        lock (gate) StopTimers();
        Connected = false;
        Playing   = false;
        LastError = reason;
        scheduler?.Clear();
        output?.Stop();
        Notify();
    }

    private void StopTimers()
    {
        stateTimer?.Dispose();    stateTimer = null;
        timeSyncTimer?.Dispose(); timeSyncTimer = null;
        probeTimer?.Dispose();    probeTimer = null;
        burstActive = false;
        probeInFlight = 0;
    }

    private void Notify() => StateChanged?.Invoke();

    public void Dispose()
    {
        lock (gate) StopTimers();
        connection.Dispose();
        output?.Dispose();
        decoder?.Dispose();
    }
}


