using MusicAssistant.Api;

namespace MusicAssistant.Remote;

/// <summary>
/// Makes this PC a Music Assistant player ("speaker").
///
/// Audio arrives over the Sendspin protocol and plays through the default
/// Windows output. The protocol itself (Noise encryption, pairing, clock
/// sync, decoding, scheduled playback) runs in the vendored sendspin-js
/// library inside the bridge page; this class only starts and stops it,
/// completes pairing with the server and mirrors its state.
/// </summary>
public sealed class Speaker
{
    private readonly RemoteBridge bridge;

    public bool   Enabled   => App.Settings.SpeakerEnabled;
    public bool   Running   { get; private set; }
    public bool   Connected { get; private set; }
    public bool   Playing   { get; private set; }
    public string Status    { get; private set; } = "Off";

    /// <summary>Raised on the UI thread whenever the speaker state changes.</summary>
    public event Action? Changed;

    public Speaker(RemoteBridge bridge)
    {
        this.bridge = bridge;
        bridge.PlayerState   += OnPlayerState;
        bridge.PlayerPairing += token => _ = PairAsync(token);
        bridge.PlayerError   += message => { Status = message; Notify(); };
    }

    /// <summary>Start (or restart) the speaker for the current server connection, if enabled.</summary>
    public async Task SyncAsync()
    {
        if (!Enabled || !App.Client.IsConnected || App.Settings.GetToken() is not { } token)
        {
            Stop();
            return;
        }

        try
        {
            await bridge.InitializeAsync();
            Status = "Connecting…";
            Notify();
            bridge.StartPlayer(new
            {
                baseUrl  = App.Client.IsRemote ? "" : App.Client.BaseUrl,
                remote   = App.Client.IsRemote,
                token,
                name     = Environment.MachineName,
                clientId = App.Settings.SpeakerClientId ?? "",
            });
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Notify();
        }
    }

    public void Stop()
    {
        if (Running) bridge.StopPlayer();
        Running = Connected = Playing = false;
        Status  = "Off";
        Notify();
    }

    /// <summary>Hand the client's pairing token to the server so it pairs this PC without an operator step.</summary>
    private async Task PairAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || !App.Client.IsConnected) return;
        try { await App.Client.SendAsync<System.Text.Json.JsonElement>("sendspin/pair_web_player", new { pairing_token = token }); }
        catch (ApiException ex) { App.Log("Speaker pairing: " + ex.Message); }
    }

    private void OnPlayerState(RemoteBridge.PlayerStatus state)
    {
        Running   = state.Running;
        Connected = state.Connected;
        Playing   = state.Playing;
        Status    = !state.Running ? "Off" : !state.Connected ? "Connecting…" : state.Playing ? "Playing" : "Ready";

        // The library derives a stable client id; keep it so the server keeps recognizing this PC
        if (!string.IsNullOrEmpty(state.ClientId) && state.ClientId != App.Settings.SpeakerClientId)
        {
            App.Settings.SpeakerClientId = state.ClientId;
            App.Settings.Save();
        }
        Notify();
    }

    private void Notify() => App.Dispatcher.TryEnqueue(() => Changed?.Invoke());
}
