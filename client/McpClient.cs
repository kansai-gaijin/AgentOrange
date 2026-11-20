using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// Agent Orange talking to a tool's MCP server itself, to pass an agent's calls on to it: over HTTP,
/// whose answers come as JSON or as a stream of events, or to a program over its standard input and
/// output. An agent's session only connects to the servers running when it starts, and a tool it
/// opens partway through would otherwise be out of its reach until the next one. Several requests
/// can be waiting at once, so a long call doesn't hold up the others.
/// </summary>
internal abstract class McpClient : IDisposable
{
    private int _nextId;

    // A tool whose MCP is only a port, such as Blender's, is reached through the server made for it (server).
    public static async Task<McpClient> ConnectAsync(EditorSettings settings, string? folder, string? server = null, CancellationToken cancel = default)
    {
        McpClient client = settings.Kind switch
        {
            McpKind.Http => new HttpMcpClient(settings.Address),
            McpKind.Relay => new ProgramMcpClient(settings.ProgramPath, settings.Address, folder),
            _ when server is not null && File.Exists(server) => new ProgramMcpClient(server, "", folder),
            _ => throw new InvalidOperationException("This tool's MCP is only a port, and nothing that speaks MCP to it is set up."),
        };
        try
        {
            await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "agent-orange", ["version"] = "1" },
            }, cancel);
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
        JsonNode? answer;
        try
        {
            answer = await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, id, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // MCP's own way to say a request is no longer wanted, so the tool can stop its work.
            _ = TellCancelledAsync(id);
            throw;
        }
        if (answer?["error"] is JsonObject error)
        {
            var message = error["message"]?.GetValue<string>() ?? "The tool said no.";
            if (McpToolBusy.From(message, error["data"]) is { } busy) throw busy;
            throw new InvalidOperationException(message);
        }
        return answer?["result"];
    }

    public Task NotifyAsync(string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return SendAsync(message, null, default);
    }

    private async Task TellCancelledAsync(int id)
    {
        try
        {
            await NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "Agent Orange stopped waiting for it." });
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            // It has gone, and its work with it.
        }
    }

    protected abstract Task<JsonNode?> SendAsync(JsonObject message, int? id, CancellationToken cancel);

    public virtual void Dispose()
    {
    }
}

/// <summary>
/// A tool that said it's busy, such as with a slow task, so nothing was done: and when to ask again,
/// if it said. Taken from an HTTP 429 or 503 with its Retry-After, or an error that says busy or
/// gives a retry-after of its own.
/// </summary>
internal sealed partial class McpToolBusy(string message, TimeSpan? retryAfter) : InvalidOperationException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The busy answer in this error, or null when it's another kind of error.</summary>
    public static McpToolBusy? From(string message, JsonNode? data)
    {
        var given = SecondsIn(data);
        if (given is null && !BusyWords().IsMatch(message)) return null;
        return new McpToolBusy(message, given ?? SecondsIn(message));
    }

    // A retry-after a tool gives with its error, by any of the usual names, in seconds.
    private static TimeSpan? SecondsIn(JsonNode? data)
    {
        if (data is not JsonObject fields) return null;
        foreach (var (name, value) in fields)
        {
            if (Bare(name) is not ("retryafter" or "retryafterseconds" or "retryafterms")) continue;
            if (value is JsonValue plain && (plain.TryGetValue<double>(out var number) || (plain.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))) && number >= 0)
            {
                return Bare(name).EndsWith("ms", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(number) : TimeSpan.FromSeconds(number);
            }
        }
        // As MCP tools put extra data: in _meta, or nested a level down.
        return fields.Select(each => each.Value).OfType<JsonObject>().Select(SecondsIn).FirstOrDefault(found => found is not null);
    }

    private static string Bare(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>A retry-after said in words, such as "retry after 5 s" or "try again in 30 seconds".</summary>
    public static TimeSpan? SecondsIn(string text)
    {
        if (SaidWait().Match(text) is not { Success: true } found || !double.TryParse(found.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        var unit = found.Groups[2].Value.ToLowerInvariant();
        return unit.StartsWith("ms", StringComparison.Ordinal) || unit.StartsWith("milli", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(number)
            : unit.StartsWith('m') ? TimeSpan.FromMinutes(number)
            : TimeSpan.FromSeconds(number);
    }

    /// <summary>From an HTTP answer's Retry-After, in seconds or as a time.</summary>
    public static TimeSpan? SecondsIn(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero,
            _ => null,
        };

    [GeneratedRegex(@"\bbusy\b", RegexOptions.IgnoreCase)]
    private static partial Regex BusyWords();

    [GeneratedRegex(@"(?:retry[\s_-]*after|try\s+again\s+in|retry\s+in|wait)\D{0,4}?(\d+(?:\.\d+)?)\s*(ms|milliseconds?|s|secs?|seconds?|m|mins?|minutes?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SaidWait();
}

// Streamable HTTP: a POST each, with the session the server gives on the first. No time limit of its
// own: each request is given one by whoever asks.
internal sealed class HttpMcpClient(string address) : McpClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string? _session;
    private bool _renewing; // Once only, should the new session be refused too.

    protected override async Task<JsonNode?> SendAsync(JsonObject message, int? id, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_session is not null) request.Headers.Add("Mcp-Session-Id", _session);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);

        // A tool that restarted, such as Unreal opened again, has forgotten the session, and says so with
        // 404, as MCP has it: start a new session, as the first time, and send it again.
        if (response.StatusCode == HttpStatusCode.NotFound && _session is not null && !_renewing && message["method"]?.GetValue<string>() != "initialize")
        {
            _session = null;
            _renewing = true;
            try
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = -1, ["method"] = "initialize",
                    ["params"] = new JsonObject
                    {
                        ["protocolVersion"] = "2025-06-18",
                        ["capabilities"] = new JsonObject(),
                        ["clientInfo"] = new JsonObject { ["name"] = "agent-orange", ["version"] = "1" },
                    },
                }, -1, cancel);
                await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, null, cancel);
                return await SendAsync(message, id, cancel);
            }
            finally
            {
                _renewing = false;
            }
        }
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _session = sessions.FirstOrDefault() ?? _session;
        if (id is null) return null;

        // Too busy to take it, which nothing was done for: said with when to ask again, if it said.
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            throw new McpToolBusy($"The tool is busy: it answered {(int)response.StatusCode} {response.ReasonPhrase}.", McpToolBusy.SecondsIn(response));
        }
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

// A program started for it, a line of JSON each way, such as ArmorPaint's relay. Its answers are read
// as they come and given to whichever request they answer, so several can wait at once.
internal sealed class ProgramMcpClient : McpClient
{
    private readonly Process _program;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _waiting = [];
    private bool _ended;

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
        _ = Task.Run(ReadAsync);
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _program.StandardOutput.ReadLineAsync() is { } line)
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
                if (answer?["id"] is not JsonValue answered || !answered.TryGetValue<int>(out var number)) continue;
                TaskCompletionSource<JsonNode?>? waiting;
                lock (_waiting) _waiting.Remove(number, out waiting);
                waiting?.TrySetResult(answer);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // It ended.
        }
        lock (_waiting)
        {
            _ended = true;
            foreach (var waiting in _waiting.Values) waiting.TrySetException(new IOException("The tool's relay stopped before answering."));
            _waiting.Clear();
        }
    }

    protected override async Task<JsonNode?> SendAsync(JsonObject message, int? id, CancellationToken cancel)
    {
        TaskCompletionSource<JsonNode?>? answer = null;
        if (id is { } number)
        {
            answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting)
            {
                if (_ended) throw new IOException("The tool's relay has stopped.");
                _waiting[number] = answer;
            }
        }
        try
        {
            await _writing.WaitAsync(cancel);
            try
            {
                await _program.StandardInput.WriteLineAsync(message.ToJsonString());
                await _program.StandardInput.FlushAsync(cancel);
            }
            finally
            {
                _writing.Release();
            }
            return answer is null ? null : await answer.Task.WaitAsync(cancel);
        }
        finally
        {
            if (id is { } asked) lock (_waiting) _waiting.Remove(asked);
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
