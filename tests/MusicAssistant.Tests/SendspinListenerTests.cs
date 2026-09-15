using System.Net.Sockets;
using System.Text;
using MusicAssistant.Sendspin;
using Xunit;

namespace MusicAssistant.Tests;

/// <summary>
/// The listener reads HTTP from any machine on the network, so these drive it over a real loopback connection: a
/// correct upgrade is accepted, and everything malformed is refused without taking the listener down.
/// </summary>
public sealed class SendspinListenerTests : IDisposable
{
    private readonly SendspinListener listener = new();
    private readonly List<ISendspinSocket> accepted = [];

    /// <summary>Released once per accepted connection, so a test waits for the event instead of polling.</summary>
    private readonly SemaphoreSlim accepts = new(0);

    public SendspinListenerTests()
    {
        listener.Accepted += socket =>
        {
            lock (accepted) accepted.Add(socket);
            accepts.Release();
        };
        listener.Start();
    }

    [Fact]
    public async Task Accepts_a_correct_upgrade()
    {
        string response = await SendAsync(Request(SendspinListener.EndpointPath));

        Assert.StartsWith("HTTP/1.1 101", response, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Accept: ", response, StringComparison.Ordinal);
        Assert.True(await WaitForAcceptedAsync(1));
    }

    [Theory]
    [InlineData("/nope")]
    [InlineData("/sendspin/../etc")]
    public async Task Refuses_another_path(string path)
    {
        string response = await SendAsync(Request(path));

        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        Assert.False(await WaitForAcceptedAsync(1));
    }

    [Fact]
    public async Task Refuses_a_request_that_is_not_a_websocket_upgrade()
    {
        string plain = $"GET {SendspinListener.EndpointPath} HTTP/1.1\r\nHost: localhost\r\n\r\n";

        Assert.StartsWith("HTTP/1.1 400", await SendAsync(plain), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_a_post()
    {
        string post = Request(SendspinListener.EndpointPath).Replace("GET ", "POST ", StringComparison.Ordinal);

        Assert.StartsWith("HTTP/1.1 400", await SendAsync(post), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_a_head_that_never_ends()
    {
        // 8 KB of headers with no blank line: the listener must give up instead of buffering without end.
        var flood = new StringBuilder($"GET {SendspinListener.EndpointPath} HTTP/1.1\r\n");
        for (int i = 0; i < 400; i++)
        {
            flood.Append($"X-Filler-{i}: ....................................\r\n");
        }

        string response = await SendAsync(flood.ToString());

        Assert.True((response.Length == 0) || response.StartsWith("HTTP/1.1 400", StringComparison.Ordinal));
        Assert.False(await WaitForAcceptedAsync(1));
    }

    [Fact]
    public async Task Keeps_listening_after_a_refused_connection()
    {
        await SendAsync(Request("/nope"));

        Assert.StartsWith("HTTP/1.1 101", await SendAsync(Request(SendspinListener.EndpointPath)), StringComparison.Ordinal);
    }

    private static string Request(string path)
        => $"GET {path} HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";

    /// <summary>Sends one request to the listener and reads what comes back.</summary>
    /// <param name="request">The raw request text.</param>
    /// <returns>The response text, empty when the listener closed without answering.</returns>
    private async Task<string> SendAsync(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", listener.Port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        // The read returns when the listener answers or hangs up, so no waiting around is needed.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[4096];
        try
        {
            int read = await stream.ReadAsync(buffer, timeout.Token);
            return Encoding.ASCII.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return "";
        }
    }

    /// <summary>Waits for the listener to hand on that many connections.</summary>
    /// <param name="count">How many Accepted events to wait for.</param>
    /// <returns>Whether they all arrived within a second.</returns>
    private async Task<bool> WaitForAcceptedAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!await accepts.WaitAsync(TimeSpan.FromSeconds(1))) return false;
        }
        return true;
    }

    public void Dispose()
    {
        listener.Dispose();
        accepts.Dispose();
        lock (accepted)
        {
            foreach (ISendspinSocket socket in accepted)
            {
                socket.Dispose();
            }
        }
    }
}
