using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// Agent Orange talking to a tool's MCP server itself, to pass an agent's calls on to it: over HTTP,
/// whose answers come as JSON or as a stream of events, or to a program over its standard input and
/// output. An agent's session only connects to the servers running when it starts, and a tool it
/// opens partway through would otherwise be out of its reach until the next one.
/// </summary>
internal abstract class McpClient : IDisposable
{
    private int _nextId;

    public static async Task<McpClient> ConnectAsync(EditorSettings settings, string? folder)
    {
        McpClient client = settings.Kind switch
        {
            McpKind.Http => new HttpMcpClient(settings.Address),
            McpKind.Relay => new ProgramMcpClient(settings.ProgramPath, settings.Address, folder),
            _ => throw new InvalidOperationException("This tool's MCP is only a port, which takes no MCP."),
        };
        try
        {
            await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "agent-orange", ["version"] = "1" },
            });
            await client.NotifyAsync("notifications/initialized");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancel = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, id, cancel);
        if (answer?["error"] is JsonObject error) throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? "The tool said no.");
        return answer?["result"];
    }

    public Task NotifyAsync(string method) => SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }, null, default);

    protected abstract Task<JsonNode?> SendAsync(JsonObject message, int? id, CancellationToken cancel);

    public virtual void Dispose()
    {
    }
}

// Streamable HTTP: a POST each, with the session the server gives on the first.
internal sealed class HttpMcpClient(string address) : McpClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private string? _session;

    protected override async Task<JsonNode?> SendAsync(JsonObject message, int? id, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_session is not null) request.Headers.Add("Mcp-Session-Id", _session);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _session = sessions.FirstOrDefault() ?? _session;
        if (id is null) return null;
        response.EnsureSuccessStatusCode();

        // A stream of events, one of which is the answer, or the answer itself.
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel));
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(cancel) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line[5..].TrimStart());
                    continue;
                }
                if (line.Length > 0 || data.Length == 0) continue;
                var event_ = JsonNode.Parse(data.ToString());
                data.Clear();
                if (event_?["id"]?.GetValue<int>() == id) return event_;
            }
            throw new IOException("The tool stopped before answering.");
        }
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel));
    }
}

// A program started for it, a line of JSON each way, such as ArmorPaint's relay.
internal sealed class ProgramMcpClient : McpClient
{
    private readonly Process _program;
    private readonly SemaphoreSlim _turn = new(1, 1);

    public ProgramMcpClient(string program, string arguments, string? folder)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (folder is not null && Directory.Exists(folder)) start.WorkingDirectory = folder;
        _program = Process.Start(start) ?? throw new IOException($"{Path.GetFileName(program)} didn't start.");
        _program.ErrorDataReceived += (_, _) => { };
        _program.BeginErrorReadLine();
    }

    protected override async Task<JsonNode?> SendAsync(JsonObject message, int? id, CancellationToken cancel)
    {
        await _turn.WaitAsync(cancel);
        try
        {
            await _program.StandardInput.WriteLineAsync(message.ToJsonString());
            await _program.StandardInput.FlushAsync(cancel);
            if (id is null) return null;
            while (await _program.StandardOutput.ReadLineAsync(cancel) is { } line)
            {
                JsonNode? answer;
                try
                {
                    answer = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // Anything else it prints.
                }
                if (answer?["id"] is JsonValue answered && answered.TryGetValue<int>(out var number) && number == id) return answer;
            }
            throw new IOException("The tool's relay stopped before answering.");
        }
        finally
        {
            _turn.Release();
        }
    }

    public override void Dispose()
    {
        try
        {
            if (!_program.HasExited) _program.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _program.Dispose();
    }
}

/// <summary>
/// Blender, through the bridge Agent Orange starts it with (<see cref="BlenderBridge"/>): a JSON
/// command each way over a socket, as the community Blender MCP add-on takes them.
/// </summary>
internal static class BlenderSocket
{
    public static async Task<JsonNode?> SendAsync(string address, string type, JsonObject parameters, CancellationToken cancel = default)
    {
        var (host, port) = address.Split(':') is [var h, var p] && int.TryParse(p, out var number) ? (h, number) : ("127.0.0.1", 9876);
        using var socket = new TcpClient();
        await socket.ConnectAsync(host, port, cancel);
        var stream = socket.GetStream();
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["type"] = type, ["params"] = parameters }.ToJsonString());
        await stream.WriteAsync(bytes, cancel);

        // One whole JSON answer, however many reads it takes.
        var received = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancel);
            if (read == 0) break;
            received.Write(buffer, 0, read);
            try
            {
                return JsonNode.Parse(received.ToArray());
            }
            catch (JsonException)
            {
                // Not all of it yet.
            }
        }
        throw new IOException("Blender closed the connection before answering.");
    }
}
