using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace MusicAssistant.Sendspin;

/// <summary>
/// Accepts the WebSocket connections Sendspin servers open to this PC after discovering it over mDNS (the spec's
/// server initiated connections), the way standalone Sendspin speakers and the ha-windows client work.
/// </summary>
/// <remarks>
/// A plain <see cref="TcpListener"/> with the WebSocket upgrade done by hand, because HttpListener needs an
/// administrator-registered URL reservation to accept anything but loopback. Sendspin runs over plain ws://; the Noise
/// layer inside provides the encryption. The package declares an inbound firewall rule for <see cref="FirstPort"/>
/// through <see cref="LastPort"/>.
///
/// @author Devin Green (Artistro08)
/// @link https://github.com/Sendspin/spec/blob/main/connection.md#server-initiated-connections
/// </remarks>
public sealed class SendspinListener : IDisposable
{
    /// <summary>The spec's recommended client port, tried first.</summary>
    public const int FirstPort = 8928;

    /// <summary>The last port tried when the earlier ones are taken (another Sendspin client on this PC holds 8928).</summary>
    public const int LastPort = 8937;

    /// <summary>The WebSocket path advertised in the mDNS TXT record.</summary>
    public const string EndpointPath = "/sendspin";

    /// <summary>The GUID RFC 6455 appends to the client's key to prove the upgrade was understood.</summary>
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Largest HTTP upgrade request accepted, so a stray client can't make the listener buffer without end.</summary>
    private const int MaxRequestBytes = 8 * 1024;

    private readonly CancellationTokenSource lifetime = new();
    private TcpListener? listener;

    /// <summary>Raised on a background thread for each server that completed the WebSocket upgrade.</summary>
    public event Action<ISendspinSocket>? Accepted;

    /// <summary>The port the listener is bound to, valid after <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>Binds the first free port from <see cref="FirstPort"/> to <see cref="LastPort"/> and starts accepting.</summary>
    /// <exception cref="SocketException">Every port in the range is taken or can't be bound.</exception>
    public void Start()
    {
        SocketException? lastError = null;
        for (int port = FirstPort; port <= LastPort; port++)
        {
            var candidate = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
            try
            {
                candidate.Start();
            }
            catch (SocketException ex)
            {
                lastError = ex;
                continue;
            }

            listener = candidate;
            Port     = port;
            _ = Task.Run(() => AcceptLoopAsync(candidate, lifetime.Token));
            return;
        }

        throw lastError ?? new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private async Task AcceptLoopAsync(TcpListener server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped, or the listening socket failed; there is nothing left to accept on.
                return;
            }

            _ = Task.Run(() => UpgradeAsync(client, ct), ct);
        }
    }

    /// <summary>Reads the HTTP request, answers the WebSocket upgrade and hands the socket on; anything else is refused.</summary>
    /// <param name="client">The accepted TCP connection.</param>
    /// <param name="ct">Stops the upgrade when the listener stops.</param>
    /// <returns>A task that completes once the socket was handed on or refused.</returns>
    private async Task UpgradeAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream stream = client.GetStream();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            Dictionary<string, string>? request = await ReadRequestAsync(stream, timeout.Token);
            if ((request is null) || !IsUpgrade(request, out string key))
            {
                await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", timeout.Token);
                client.Dispose();
                return;
            }

            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            await WriteAsync(stream, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n", timeout.Token);

            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
            App.Debug($"Speaker: server connected from {client.Client.RemoteEndPoint}");
            Accepted?.Invoke(new AcceptedWebSocket(socket, client));
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // A task started per connection: a failed upgrade must only drop that connection.
            App.Debug($"Speaker: incoming connection refused: {ex.Message}");
            client.Dispose();
        }
    }

    /// <summary>Reads the request head up to its blank line and returns its headers (lowercase names) plus the path under "".</summary>
    /// <param name="stream">The connection's stream.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The headers, or <see langword="null"/> when the request is malformed, too large or cut off.</returns>
    private static async Task<Dictionary<string, string>?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new List<byte>(1024);
        byte[] one = new byte[1];

        // Byte by byte, so nothing past the blank line (the first WebSocket frame) is consumed here.
        while (head.Count < MaxRequestBytes)
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                return null;
            }

            head.Add(one[0]);
            if ((head.Count >= 4) && (head[^4] == '\r') && (head[^3] == '\n') && (head[^2] == '\r') && (head[^1] == '\n'))
            {
                break;
            }
        }

        // Too large without ever reaching the blank line.
        if ((head.Count < 4) || (head[^1] != '\n') || (head[^2] != '\r') || (head[^3] != '\n'))
        {
            return null;
        }

        string[] lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] requestLine = lines.Length > 0 ? lines[0].Split(' ') : [];
        if ((requestLine.Length < 3) || (requestLine[0] != "GET"))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = requestLine[1].Split('?')[0] };
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
        }
        return headers;
    }

    /// <summary>Whether the request is a version 13 WebSocket upgrade for the advertised path.</summary>
    /// <param name="request">The parsed request.</param>
    /// <param name="key">The client's Sec-WebSocket-Key when it is.</param>
    /// <returns>Whether the upgrade can be accepted.</returns>
    private static bool IsUpgrade(Dictionary<string, string> request, out string key)
    {
        key = request.GetValueOrDefault("sec-websocket-key", "");
        return (request[""] == EndpointPath)
            && request.GetValueOrDefault("upgrade", "").Equals("websocket", StringComparison.OrdinalIgnoreCase)
            && request.GetValueOrDefault("connection", "").Contains("upgrade", StringComparison.OrdinalIgnoreCase)
            && (request.GetValueOrDefault("sec-websocket-version", "") == "13")
            && (key.Length > 0);
    }

    private static Task WriteAsync(NetworkStream stream, string text, CancellationToken ct)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    /// <summary>Stops accepting; connections already handed on stay open.</summary>
    public void Dispose()
    {
        lifetime.Cancel();
        listener?.Stop();
        listener = null;
        lifetime.Dispose();
    }

    /// <summary>A server's connection accepted by the listener: already open, so opening only starts reading.</summary>
    private sealed class AcceptedWebSocket(WebSocket socket, TcpClient client) : WebSocketWire
    {
        /// <inheritdoc/>
        protected override WebSocket Socket => socket;

        /// <inheritdoc/>
        public override Task OpenAsync(CancellationToken ct)
        {
            // The TCP connection belongs to this wire from here on and closes with it.
            Lifetime.Register(client.Dispose);
            StartReading();
            return Task.CompletedTask;
        }
    }
}
