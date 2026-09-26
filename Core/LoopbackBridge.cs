using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace StageManager.Core;

// Small, bounded HTTP/1.1 endpoint bound only to IPv4 loopback. No HTTP.sys URL reservation.
public sealed class LoopbackBridge : IDisposable
{
    public const int Port = 17845;
    public const int MaxBodyBytes = 4 * 1024 * 1024;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string origin;
    private readonly TcpListener listener = new(IPAddress.Loopback, Port);
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots = new(4);
    private readonly Func<BridgeMessage, Task<BridgeReply>> receive;
    private readonly Func<PlayerSnapshot?> snapshot;
    private readonly Task acceptTask;

    public LoopbackBridge(string origin, Func<BridgeMessage, Task<BridgeReply>> receive, Func<PlayerSnapshot?> snapshot)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.GetLeftPart(UriPartial.Authority) != origin)
            throw new ArgumentException("Enter the web app origin only, e.g. https://example.com (no path or trailing slash).");
        this.origin = origin; this.receive = receive; this.snapshot = snapshot;
        listener.Start(4);
        acceptTask = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                _ = Serve(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task Serve(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = timeout.Token;
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new List<byte>(1024);
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(one, ct) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                if (header.Count >= 8192) { await Reply(stream, 431, "Headers too large", null, false, ct); return; }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var request = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(l => l.Length != 0))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 1 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                    { await Reply(stream, 400, "Invalid headers", null, false, ct); return; }
                }
                var allowed = headers.GetValueOrDefault("Origin") == origin;
                if (request.Length != 3 || request[2] != "HTTP/1.1" || headers.GetValueOrDefault("Host") != $"127.0.0.1:{Port}" || !allowed)
                { await Reply(stream, 403, "Origin or host not allowed", null, false, ct); return; }
                if (request[1] is not ("/snapshot" or "/state")) { await Reply(stream, 404, "Unknown route", null, true, ct); return; }
                if (request[0] == "OPTIONS") { await Reply(stream, 200, "Ready", null, true, ct); return; }
                var auth = Encoding.UTF8.GetBytes(headers.GetValueOrDefault("Authorization", ""));
                if (!CryptographicOperations.FixedTimeEquals(auth, Encoding.UTF8.GetBytes($"Bearer {Token}")))
                { await Reply(stream, 401, "Pair again using the plugin token", null, true, ct); return; }
                if (request[0] != "POST" || headers.ContainsKey("Transfer-Encoding") ||
                    !headers.GetValueOrDefault("Content-Type", "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(headers.GetValueOrDefault("Content-Length"), out var length) || length < 0 || length > MaxBodyBytes)
                { await Reply(stream, 400, "Invalid request body", null, true, ct); return; }
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, ct);
                if (request[1] == "/snapshot") { await Reply(stream, 200, "Connected", snapshot(), true, ct); return; }
                try
                {
                    var result = await receive(WireJson.Parse<BridgeMessage>(Encoding.UTF8.GetString(body))).WaitAsync(ct);
                    await Reply(stream, result.Ok ? 200 : 400, result.Message, result.Snapshot, true, ct);
                }
                catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
                { await Reply(stream, 400, "Invalid choreography or playback state", null, true, ct); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { client.Dispose(); }
        finally { slots.Release(); }
    }

    private async Task Reply(NetworkStream stream, int code, string message, PlayerSnapshot? player, bool cors, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(WireJson.Serialize(new BridgeReply(code == 200, message, player)));
        var corsHeaders = cors ? $"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Authorization, Content-Type\r\nAccess-Control-Allow-Private-Network: true\r\n" : "";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Response\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{corsHeaders}\r\n");
        await stream.WriteAsync(header, ct); await stream.WriteAsync(body, ct);
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
