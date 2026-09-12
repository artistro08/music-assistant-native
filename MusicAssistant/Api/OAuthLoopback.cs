using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace MusicAssistant.Api;

/// <summary>
/// One-shot loopback HTTP listener for browser-based (OAuth) sign-in.
///
/// The Music Assistant server appends the issued token as ?code= to the
/// return URL after Home Assistant authorizes. This listens on 127.0.0.1
/// with a random port and a random path nonce, accepts exactly one matching
/// request, shows a "you can close this tab" page and hands back the code.
/// 127.0.0.1 is a trusted redirect target for the server, so no consent
/// banner is shown.
/// </summary>
public sealed class OAuthLoopback : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public string ReturnUrl { get; }

    public OAuthLoopback()
    {
        // Pick a free port by binding, then hand it to HttpListener
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        ReturnUrl = $"http://127.0.0.1:{port}/callback/{nonce}";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/callback/");
        listener.Start();
    }

    /// <summary>Wait for the browser to land on ReturnUrl and return the code query parameter.</summary>
    public async Task<string> WaitForCodeAsync(CancellationToken ct)
    {
        using var registration = ct.Register(listener.Stop);

        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }

            var request = context.Request;
            var code    = HttpUtility.ParseQueryString(request.Url?.Query ?? "").Get("code");
            var matches = request.Url?.AbsolutePath.EndsWith($"/callback/{nonce}", StringComparison.Ordinal) == true;

            if (!matches || string.IsNullOrEmpty(code))
            {
                await RespondAsync(context.Response, 404, "Not found");
                continue;
            }

            await RespondAsync(context.Response, 200, "Signed in. You can close this tab and return to Music Assistant.");
            return code;
        }
    }

    private static async Task RespondAsync(HttpListenerResponse response, int status, string text)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Music Assistant</title></head>" +
            $"<body style=\"font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh;margin:0\"><p>{text}</p></body></html>");
        response.StatusCode      = status;
        response.ContentType     = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        response.Headers["Cache-Control"] = "no-store";
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    public void Dispose()
    {
        if (listener.IsListening) listener.Stop();
        listener.Close();
    }
}
