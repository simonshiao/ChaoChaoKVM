using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChaoChaoKvm.Services;

namespace ChaoChaoKvm.Api;

public sealed class AiControlApiServer : IAsyncDisposable
{
    public const int Port = 45889;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly ComputerControlService _computer;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public AiControlApiServer(ComputerControlService computer)
    {
        _computer = computer;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
            return Task.CompletedTask;

        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A malformed request must not stop the local API loop.
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            HttpRequest request = await ReadRequestAsync(stream, cancellationToken);

            if (request.Method == "GET" && request.Path == "/api/v1/status")
            {
                await WriteJsonAsync(stream, 200, new
                {
                    ok = true,
                    app = "ChaoChaoKvm",
                    apiVersion = "v1",
                    endpoint = $"http://127.0.0.1:{Port}",
                    actions = new[] { "system.ping", "computer.open_text_file", "computer.type" }
                }, cancellationToken);
                return;
            }

            if (request.Method == "POST" && request.Path == "/api/v1/action")
            {
                AiActionRequest? action = JsonSerializer.Deserialize<AiActionRequest>(request.Body, JsonOptions);
                if (action is null)
                {
                    await WriteJsonAsync(stream, 400, new { ok = false, error = "INVALID_JSON" }, cancellationToken);
                    return;
                }

                (int status, object payload) = HandleAction(action);
                await WriteJsonAsync(stream, status, payload, cancellationToken);
                return;
            }

            await WriteJsonAsync(stream, 404, new { ok = false, error = "NOT_FOUND" }, cancellationToken);
        }
    }

    private (int Status, object Payload) HandleAction(AiActionRequest request)
    {
        try
        {
            return request.Action switch
            {
                "system.ping" => (200, new { ok = true, action = request.Action, result = "pong" }),
                "computer.open_text_file" => (200, new
                {
                    ok = true,
                    action = request.Action,
                    path = _computer.OpenTextFile(request.Path ?? string.Empty)
                }),
                "computer.type" => (200, new
                {
                    ok = true,
                    action = request.Action,
                    characters = _computer.TypeText(request.Text ?? string.Empty)
                }),
                _ => (400, new { ok = false, error = "UNKNOWN_ACTION", action = request.Action })
            };
        }
        catch (Exception ex)
        {
            return (400, new { ok = false, error = ex.GetType().Name, message = ex.Message });
        }
    }

    private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var received = new List<byte>(4096);
        byte[] buffer = new byte[4096];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                throw new IOException("HTTP request ended before headers were complete.");

            for (int i = 0; i < read; i++)
                received.Add(buffer[i]);

            if (received.Count > MaxHeaderBytes + MaxBodyBytes)
                throw new InvalidDataException("HTTP request is too large.");

            headerEnd = FindHeaderEnd(received);
            if (headerEnd < 0 && received.Count > MaxHeaderBytes)
                throw new InvalidDataException("HTTP headers are too large.");
        }

        string headerText = Encoding.ASCII.GetString(received.GetRange(0, headerEnd).ToArray());
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
            throw new InvalidDataException("Invalid HTTP request line.");

        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(value);
        }

        if (contentLength is < 0 or > MaxBodyBytes)
            throw new InvalidDataException("HTTP body is too large.");

        int bodyStart = headerEnd + 4;
        while (received.Count - bodyStart < contentLength)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                throw new IOException("HTTP request ended before body was complete.");
            for (int i = 0; i < read; i++)
                received.Add(buffer[i]);
        }

        byte[] bodyBytes = received.Skip(bodyStart).Take(contentLength).ToArray();
        return new HttpRequest(
            requestLine[0].ToUpperInvariant(),
            requestLine[1].Split('?', 2)[0],
            Encoding.UTF8.GetString(bodyBytes));
    }

    private static int FindHeaderEnd(List<byte> bytes)
    {
        for (int i = 0; i <= bytes.Count - 4; i++)
        {
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                return i;
        }

        return -1;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int statusCode, object payload, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        string reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Error"
        };
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is null)
            return;

        _cts?.Cancel();
        _listener.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
        _acceptLoop = null;
        _listener = null;
    }

    private sealed record HttpRequest(string Method, string Path, string Body);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed class AiActionRequest
    {
        [JsonPropertyName("action")]
        public string Action { get; init; } = string.Empty;

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}
