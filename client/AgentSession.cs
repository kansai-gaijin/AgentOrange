using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

public enum AgentEventKind { Text, Tool, Problem }

public sealed record AgentEvent(AgentEventKind Kind, string Text);

/// <summary>
/// One conversation with an agent program, working in a project's folder. Each message runs the
/// program once and picks the conversation back up by its ID, so nothing is left running between
/// messages. It may edit files in the folder, and anything else it asks to do is refused for now,
/// as there's nowhere yet to ask the person.
/// </summary>
public sealed class AgentSession(AgentProvider provider, string folder)
{
    private string? _conversationId;
    private Process? _running;
    private bool _stopped;

    public AgentProvider Provider => provider;

    public bool IsRunning => _running is not null;

    /// <summary>Sends one message and reports what comes back as it arrives, on the caller's thread.</summary>
    public async Task SendAsync(string prompt, Action<AgentEvent> report)
    {
        if (_running is not null) return;

        var arguments = "-p --output-format stream-json --verbose --permission-mode acceptEdits";
        if (_conversationId is not null) arguments += $" --resume {_conversationId}";

        Process? process;
        try
        {
            process = Process.Start(AgentProviders.SessionStartInfo(provider, arguments, folder));
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't start {provider.Program}: {error.Message}");
            process = null;
        }
        if (process is null)
        {
            report(new AgentEvent(AgentEventKind.Problem, $"Couldn't start \"{provider.Program}\"."));
            return;
        }

        _running = process;
        _stopped = false;
        var saidAnything = false;
        try
        {
            ChildProcesses.Track(process);
            var errors = process.StandardError.ReadToEndAsync();

            // On its input, so nothing in a message can be read as part of the command.
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                foreach (var found in Read(line))
                {
                    saidAnything = true;
                    report(found);
                }
            }
            await process.WaitForExitAsync();

            if (_stopped) report(new AgentEvent(AgentEventKind.Problem, "Stopped."));
            else if (!saidAnything)
            {
                var detail = (await errors).Trim();
                report(new AgentEvent(AgentEventKind.Problem, detail.Length > 0 ? detail : $"\"{provider.Program}\" ended without saying anything."));
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // Its pipes close under it when it's stopped partway.
            report(new AgentEvent(AgentEventKind.Problem, _stopped ? "Stopped." : "Lost contact with the agent."));
        }
        finally
        {
            _running = null;
            process.Dispose();
        }
    }

    public void Stop()
    {
        if (_running is not { } process) return;
        _stopped = true;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    // Claude Code prints one JSON event per line. Anything unknown is skipped, as its events change between versions.
    private List<AgentEvent> Read(string line)
    {
        var found = new List<AgentEvent>();
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return found;
            if (Text(root, "session_id") is { Length: > 0 } id) _conversationId = id;

            switch (Text(root, "type"))
            {
                case "assistant" when root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                    foreach (var part in content.EnumerateArray())
                    {
                        if (Text(part, "type") == "text" && Text(part, "text") is { Length: > 0 } text) found.Add(new AgentEvent(AgentEventKind.Text, text.Trim()));
                        if (Text(part, "type") == "tool_use") found.Add(new AgentEvent(AgentEventKind.Tool, DescribeTool(part)));
                    }
                    break;
                case "result" when root.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True:
                    found.Add(new AgentEvent(AgentEventKind.Problem, Text(root, "result") is { Length: > 0 } reason ? reason : "The agent stopped with an error."));
                    break;
            }
        }
        catch (JsonException)
        {
            // Not an event, such as a warning printed by something it started.
        }
        return found;
    }

    private static string DescribeTool(JsonElement tool)
    {
        var name = Text(tool, "name") ?? "Tool";
        if (!tool.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return name;

        foreach (var key in new[] { "file_path", "path", "command", "pattern", "url", "description" })
        {
            if (Text(input, key) is not { Length: > 0 } value) continue;
            var oneLine = value.ReplaceLineEndings(" ");
            return $"{name}  {(oneLine.Length > 140 ? oneLine[..140] + "…" : oneLine)}";
        }
        return name;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
