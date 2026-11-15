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

    // One agent session: its project first, then a request a line, each answered in turn.
    private async Task ServeOneAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await using (pipe)
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var first = await reader.ReadLineAsync();
                var projectId = first is not null && first.StartsWith("project:", StringComparison.Ordinal) ? first["project:".Length..] : "";

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
                        await writer.WriteLineAsync(Error(null, -32700, "That wasn't JSON.").ToJsonString());
                        continue;
                    }
                    if (message is not JsonObject request || request["method"]?.GetValue<string>() is not { } method) continue;
                    var id = request["id"]?.DeepClone();
                    if (id is null) continue; // A notification, which has no answer.

                    JsonObject answer;
                    try
                    {
                        var result = await handle(projectId, method, request["params"]);
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
                    await writer.WriteLineAsync(answer.ToJsonString());
                }
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

internal sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// What an agent session starts: passes its messages to the running app and the app's answers back,
/// over standard input and output, which is how MCP runs a program. Nothing else happens in it.
/// </summary>
internal static class McpRelay
{
    public static void Run(string projectId)
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var pipe = new NamedPipeClientStream(".", McpServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(5000);
        }
        catch (Exception error) when (error is TimeoutException or IOException)
        {
            AnswerNotRunning(stdin, stdout);
            return;
        }

        var toApp = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var fromApp = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        toApp.WriteLine($"project:{projectId}");
        var up = Task.Run(async () =>
        {
            while (await stdin.ReadLineAsync() is { } line) await toApp.WriteLineAsync(line);
        });
        var down = Task.Run(async () =>
        {
            while (await fromApp.ReadLineAsync() is { } line) await stdout.WriteLineAsync(line);
        });
        Task.WaitAny(up, down);
    }

    // Agent Orange isn't open, so every request is told so, rather than left unanswered.
    private static void AnswerNotRunning(StreamReader stdin, StreamWriter stdout)
    {
        while (stdin.ReadLine() is { } line)
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject { } request && request["id"] is { } id)
                {
                    stdout.WriteLine(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Agent Orange isn't open, so its project can't be read. Ask the person to open it." },
                    }.ToJsonString());
                }
            }
            catch (JsonException)
            {
            }
        }
    }
}
