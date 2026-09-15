using System.Net;

namespace MusicAssistant.Remote;

/// <summary>
/// Loopback HTTP server that serves the remote server's artwork.
///
/// Image controls only know how to load URLs, so in remote mode the image
/// base URL points here. Each request is tunneled through the WebRTC HTTP
/// proxy channel. Binds 127.0.0.1 on a random port, only answers GET under a
/// per-session random prefix, and only for the server's image and preview
/// endpoints.
/// </summary>
public sealed class LocalImageProxy : IDisposable
{
    private static readonly string[] AllowedPrefixes = ["/imageproxy", "/preview"];

    private readonly RemotePeer peer;
    private readonly HttpListener listener = new();
    private readonly string nonce = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? cts;

    /// <summary>Base URL to substitute for the server's own base URL.</summary>
    public string BaseUrl { get; }

    /// <summary>Creates the proxy on a free loopback port for the given peer; it serves nothing until <see cref="Start"/>.</summary>
    /// <param name="peer">The remote connection whose HTTP proxy channel carries the image requests.</param>
    public LocalImageProxy(RemotePeer peer)
    {
        this.peer = peer;

        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseUrl = $"http://127.0.0.1:{port}/{nonce}";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/{nonce}/");
    }

    /// <summary>Starts listening and serving image requests in the background; does nothing if it is already listening.</summary>
    public void Start()
    {
        if (listener.IsListening) return;
        listener.Start();
        cts = new CancellationTokenSource();
        _ = Task.Run(() => ServeAsync(cts.Token));
    }

    /// <summary>Stops accepting requests and cancels the ones in progress.</summary>
    public void Stop()
    {
        cts?.Cancel();
        if (listener.IsListening) listener.Stop();
    }

    /// <summary>Stops the proxy and releases the HTTP listener.</summary>
    public void Dispose()
    {
        Stop();
        listener.Close();
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex) && (ct.IsCancellationRequested || !listener.IsListening))
            {
                return;
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerResponse response = context.Response;
        try
        {
            // Strip the nonce prefix; refuse anything that is not an allowed GET.
            string path = context.Request.Url?.PathAndQuery ?? "";
            path = path.StartsWith($"/{nonce}", StringComparison.Ordinal) ? path[(nonce.Length + 1)..] : "";

            if ((context.Request.HttpMethod != "GET") || !AllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal)))
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            RemotePeer.HttpReply reply = await peer.HttpAsync("GET", path, null, ct);
            response.StatusCode = reply.Status == 0 ? 502 : reply.Status;
            if (reply.Headers.TryGetValue("Content-Type", out string? type)) response.ContentType = type;
            response.Headers["Cache-Control"] = "private, max-age=3600";
            response.ContentLength64 = reply.Body.Length;
            await response.OutputStream.WriteAsync(reply.Body, ct);
            response.Close();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            try
            {
                response.StatusCode = 502;
                response.Close();
            }
            catch (Exception cleanupEx) when (ExceptionFilters.IsRecoverable(cleanupEx))
            {
            }
        }
    }
}
