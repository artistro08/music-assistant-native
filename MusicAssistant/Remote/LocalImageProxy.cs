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

    private readonly RemoteBridge bridge;
    private readonly HttpListener listener = new();
    private readonly string nonce = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? cts;

    /// <summary>Base URL to substitute for the server's own base URL.</summary>
    public string BaseUrl { get; }

    public LocalImageProxy(RemoteBridge bridge)
    {
        this.bridge = bridge;

        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseUrl = $"http://127.0.0.1:{port}/{nonce}";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/{nonce}/");
    }

    public void Start()
    {
        if (listener.IsListening) return;
        listener.Start();
        cts = new CancellationTokenSource();
        _ = Task.Run(() => ServeAsync(cts.Token));
    }

    public void Stop()
    {
        cts?.Cancel();
        if (listener.IsListening) listener.Stop();
    }

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
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening) { return; }
            catch (Exception) { continue; }

            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        try
        {
            // Strip the nonce prefix; refuse anything that is not an allowed GET
            var path = context.Request.Url?.PathAndQuery ?? "";
            path = path.StartsWith($"/{nonce}", StringComparison.Ordinal) ? path[(nonce.Length + 1)..] : "";

            if (context.Request.HttpMethod != "GET" || !AllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal)))
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            var reply = await bridge.HttpAsync("GET", path, null, ct);
            response.StatusCode = reply.Status == 0 ? 502 : reply.Status;
            if (reply.Headers.TryGetValue("Content-Type", out var type)) response.ContentType = type;
            response.Headers["Cache-Control"] = "private, max-age=3600";
            response.ContentLength64 = reply.Body.Length;
            await response.OutputStream.WriteAsync(reply.Body, ct);
            response.Close();
        }
        catch (Exception)
        {
            try { response.StatusCode = 502; response.Close(); } catch { }
        }
    }
}
