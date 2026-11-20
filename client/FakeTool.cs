#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// Dev builds only, for headless checks of the tool relay: a made-up creative tool, as an MCP server
/// over HTTP on this PC inside the app, with a program of its own (a renamed copy of Windows' ping,
/// which runs with no window until it's ended) to stand for the tool being open. It can turn calls
/// away as a tool still starting does, say it's busy, take its time, and quit partway through a call.
/// Requests from the editor monitor's own check are answered as usual and not counted.
/// </summary>
internal sealed class FakeTool
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private int _turnAway;
    private int _busy;

    public static FakeTool? Running { get; private set; }

    public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/mcp";

    public string ProgramPath { get; }

    public Process? Program { get; private set; }

    /// <summary>The notifications/cancelled it was sent, by the request each was about.</summary>
    public HashSet<int> Cancelled { get; } = [];

    private FakeTool(string programPath)
    {
        ProgramPath = programPath;
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public static FakeTool Start(string programPath) => Running ??= new FakeTool(programPath);

    /// <summary>The next so many requests are answered 404, as a tool still starting does.</summary>
    public void TurnAway(int count) => Interlocked.Exchange(ref _turnAway, count);

    /// <summary>The next so many tool calls are answered busy, with a retry-after of a second.</summary>
    public void Busy(int count) => Interlocked.Exchange(ref _busy, count);

    /// <summary>Starts its program, with no window, if it isn't running.</summary>
    public void StartProgram()
    {
        if (Program is { HasExited: false }) return;
        if (!File.Exists(ProgramPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProgramPath)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ProgramPath, overwrite: true);
        }
        Program = Process.Start(new ProcessStartInfo(ProgramPath, "-n 900 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("The made-up tool's program didn't start.");
        ChildProcesses.Track(Program);
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync();
            _ = Task.Run(() => ConnectionAsync(client));
        }
    }

    // HTTP/1.1, a request at a time on each connection, as HttpClient keeps them open.
    private async Task ConnectionAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                while (await ReadRequestAsync(stream) is { } request)
                {
                    var (status, body, session) = await AnswerAsync(request.Method, request.Body);
                    var head = new StringBuilder($"HTTP/1.1 {(int)status} {status}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
                    if (session) head.Append("Mcp-Session-Id: fake-session\r\n");
                    head.Append("\r\n");
                    var bytes = Encoding.UTF8.GetBytes(head + body);
                    await stream.WriteAsync(bytes);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
            {
                // The other end went.
            }
        }
    }

    private static async Task<(string Method, string Body)?> ReadRequestAsync(NetworkStream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0) return null;
            head.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n");
        var length = lines.Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.Parse(parts[1].Trim())).FirstOrDefault();
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return (lines[0].Split(' ')[0], Encoding.UTF8.GetString(body));
    }

    private async Task<(HttpStatusCode Status, string Body, bool Session)> AnswerAsync(string method, string body)
    {
        if (method != "POST") return (method == "DELETE" ? HttpStatusCode.OK : HttpStatusCode.MethodNotAllowed, "", false);
        var request = JsonNode.Parse(body) as JsonObject ?? [];
        var asked = request["method"]?.GetValue<string>() ?? "";
        var fromMonitor = body.Contains("agent-orange-status", StringComparison.Ordinal);
        if (!fromMonitor && Interlocked.Decrement(ref _turnAway) >= 0) return (HttpStatusCode.NotFound, "", false);
        Interlocked.Exchange(ref _turnAway, Math.Max(0, _turnAway));

        if (request["id"] is not JsonValue idValue)
        {
            if (asked == "notifications/cancelled" && request["params"]?["requestId"]?.GetValue<int>() is { } cancelled) lock (Cancelled) Cancelled.Add(cancelled);
            return (HttpStatusCode.Accepted, "", false);
        }
        var id = idValue.GetValue<int>();
        JsonNode result = asked switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "made-up-tool", ["version"] = "1" },
            },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(
                    Tool("echo", "Says back what it's given.", new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } }),
                    Tool("slow", "Takes seconds to answer, unless cancelled.", new JsonObject { ["seconds"] = new JsonObject { ["type"] = "integer" } }),
                    Tool("quit", "Quits the tool, answering Canceled as it goes.", [])),
            },
            "tools/call" => await CallAsync(id, request["params"] as JsonObject ?? []),
            _ => new JsonObject(),
        };
        return (HttpStatusCode.OK, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString(), asked == "initialize");
    }

    private static JsonObject Tool(string name, string description, JsonObject properties) =>
        new() { ["name"] = name, ["description"] = description, ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties } };

    private static JsonObject Said(string text, bool error = false)
    {
        var said = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (error) said["isError"] = true;
        return said;
    }

    private async Task<JsonObject> CallAsync(int id, JsonObject parameters)
    {
        if (Interlocked.Decrement(ref _busy) >= 0) return Said("Editor is busy (slow task). Retry after 1 s.", error: true);
        Interlocked.Exchange(ref _busy, Math.Max(0, _busy));
        var arguments = parameters["arguments"] as JsonObject ?? [];
        switch (parameters["name"]?.GetValue<string>())
        {
            case "echo":
                return Said($"echo: {arguments["text"]}");
            case "slow":
                var seconds = arguments["seconds"]?.GetValue<int>() ?? 1;
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < seconds)
                {
                    lock (Cancelled)
                    {
                        if (Cancelled.Contains(id)) return Said("Cancelled, as asked.", error: true);
                    }
                    await Task.Delay(100);
                }
                return Said($"slow done after {seconds} s");
            case "quit":
                _ = Task.Delay(300).ContinueWith(_ => Program?.Kill(), TaskScheduler.Default);
                return Said("Canceled", error: true);
            default:
                return Said($"There's no tool called {parameters["name"]}.", error: true);
        }
    }
}
#endif
