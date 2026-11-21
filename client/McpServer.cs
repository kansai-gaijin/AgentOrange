using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// Agent Orange's own MCP server, so agents can read and change the design and the tasks: the cards'
/// documents, the overview, the tasks and milestones, and the game settings. Each agent session
/// starts "AgentOrange.exe --mcp project-id" (<see cref="McpRelay"/>), which passes its messages, a
/// line of JSON-RPC each, to the running app over a pipe only this Windows account can open. The app
/// answers from its own database, on its window's thread, so what it shows and what agents see agree.
/// </summary>
internal sealed class McpServer(Func<string, string, JsonNode?, Task<JsonNode?>> handle)
{
#if DEBUG
    public const string PipeName = "AgentOrange-Dev-Mcp";
#else
    public const string PipeName = "AgentOrange-Mcp";
#endif

    public void Start() => _ = Task.Run(ServeAsync);

    private async Task ServeAsync()
    {
        while (true)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
            }
            catch (IOException error)
            {
                // Such as another copy of the app serving it already.
                AppLog.Write($"The MCP server couldn't take connections: {error.Message}");
                return;
            }
            _ = Task.Run(() => ServeOneAsync(pipe));
        }
    }

    // One agent session: its project first, then a request a line. Each is answered when it's done,
    // not in turn, so a long one, such as wait_for or open_tool, doesn't hold up the session's others.
    private async Task ServeOneAsync(NamedPipeServerStream pipe)
    {
        var answering = new List<Task>();
        try
        {
            await using (pipe)
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var writing = new SemaphoreSlim(1, 1);
                var first = await reader.ReadLineAsync();
                var projectId = first is not null && first.StartsWith("project:", StringComparison.Ordinal) ? first["project:".Length..] : "";

                async Task SendAsync(JsonObject answer)
                {
                    await writing.WaitAsync();
                    try
                    {
                        await writer.WriteLineAsync(answer.ToJsonString());
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException)
                    {
                        // The session ended before its answer was ready.
                    }
                    finally
                    {
                        writing.Release();
                    }
                }

                async Task AnswerAsync(JsonNode id, string method, JsonNode? parameters)
                {
                    JsonObject answer;
                    try
                    {
                        var result = await handle(projectId, method, parameters);
                        answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
                    }
                    catch (McpException error)
                    {
                        answer = Error(id, error.Code, error.Message);
                    }
                    catch (Exception error)
                    {
                        AppLog.Write($"An MCP request failed: {method}: {error}");
                        answer = Error(id, -32603, $"Agent Orange couldn't do that: {error.Message}");
                    }
                    await SendAsync(answer);
                }

                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Trim().Length == 0) continue;
                    JsonNode? message;
                    try
                    {
                        message = JsonNode.Parse(line);
                    }
                    catch (JsonException)
                    {
                        await SendAsync(Error(null, -32700, "That wasn't JSON."));
                        continue;
                    }
                    if (message is not JsonObject request || request["method"]?.GetValue<string>() is not { } method) continue;
                    var id = request["id"]?.DeepClone();
                    if (id is null) continue; // A notification, which has no answer.
                    answering.RemoveAll(task => task.IsCompleted);
                    answering.Add(AnswerAsync(id, method, request["params"]?.DeepClone()));
                }
                await Task.WhenAll(answering);
            }
        }
        catch (IOException)
        {
            // The session ended.
        }
    }

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

internal class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>An argument a tool needs that wasn't given, said in full by the tool's call, which knows what the argument is for.</summary>
internal sealed class McpMissingArgument(string name) : McpException(-32602, $"{name} is needed.")
{
    public string Name { get; } = name;
}

/// <summary>
/// What an agent session starts: passes its messages to the running app and the app's answers back,
/// over standard input and output, which is how MCP runs a program. Nothing else happens in it.
///
/// Its session outlives the app (#101), so the relay does too: when the app goes, such as for an
/// update or a dev build's rebuild, it connects again once the app is back. A request the app had
/// when it went is told so, as it may or may not have been done, and one sent while it's away waits
/// a few minutes for it to come back before being told it isn't open.
/// </summary>
internal static class McpRelay
{
    private static readonly TimeSpan WaitsForApp = TimeSpan.FromMinutes(3);

    public static void Run(string projectId)
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var lockObject = new object();
        var asked = new Dictionary<string, JsonNode>(); // Requests the app has, by id, not yet answered.
        StreamWriter? toApp = null;
        NamedPipeClientStream? pipe = null;
        var gaveUp = false; // It stayed away past the wait, so the next request only tries once.

        void Answer(JsonNode id, string message)
        {
            lock (lockObject)
            {
                stdout.WriteLine(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message },
                }.ToJsonString());
            }
        }

        // The app went: what it had is told so, and the next request connects again.
        void Lost(NamedPipeClientStream gone)
        {
            List<JsonNode> unanswered;
            lock (lockObject)
            {
                if (pipe != gone) return;
                pipe = null;
                toApp = null;
                unanswered = [.. asked.Values];
                asked.Clear();
            }
            gone.Dispose();
            foreach (var id in unanswered) Answer(id, "Agent Orange restarted before answering, so this may or may not have been done. Check before trying it again.");
        }

        // Connected, or connecting again, for up to a few minutes while the app restarts.
        StreamWriter? Connected()
        {
            lock (lockObject)
            {
                if (toApp is not null) return toApp;
            }
            var until = DateTimeOffset.UtcNow + (gaveUp ? TimeSpan.Zero : WaitsForApp);
            while (true)
            {
                var connecting = new NamedPipeClientStream(".", McpServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    connecting.Connect(2000);
                    var writer = new StreamWriter(connecting, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    writer.WriteLine($"project:{projectId}");
                    lock (lockObject)
                    {
                        pipe = connecting;
                        toApp = writer;
                        gaveUp = false;
                    }
                    var fromApp = new StreamReader(connecting, new UTF8Encoding(false), leaveOpen: true);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (await fromApp.ReadLineAsync() is { } line)
                            {
                                lock (lockObject)
                                {
                                    if (IdOf(line) is { } id) asked.Remove(id.ToJsonString());
                                    stdout.WriteLine(line);
                                }
                            }
                        }
                        catch (Exception error) when (error is IOException or ObjectDisposedException)
                        {
                        }
                        Lost(connecting);
                    });
                    return writer;
                }
                catch (Exception error) when (error is TimeoutException or IOException)
                {
                    connecting.Dispose();
                    if (DateTimeOffset.UtcNow > until)
                    {
                        gaveUp = true;
                        return null;
                    }
                    Thread.Sleep(1000);
                }
            }
        }

        while (stdin.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            var id = IdOf(line);
            for (var tries = 0; tries < 2; tries++)
            {
                if (Connected() is not { } writer)
                {
                    if (id is not null) Answer(id, "Agent Orange isn't open, so its project can't be read. Ask the person to open it.");
                    break;
                }
                try
                {
                    lock (lockObject)
                    {
                        if (id is not null) asked[id.ToJsonString()] = id.DeepClone();
                        writer.WriteLine(line);
                    }
                    break;
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // It went just now. Sent again once it's back, as it never got it.
                    lock (lockObject)
                    {
                        if (id is not null) asked.Remove(id.ToJsonString());
                    }
                    if (pipe is { } gone) Lost(gone);
                }
            }
        }
    }

    // A request's id, or an answer's, or null for a notification or anything that isn't JSON.
    private static JsonNode? IdOf(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject message && message["id"] is { } id ? id.DeepClone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
