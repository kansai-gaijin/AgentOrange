using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AgentOrange;

/// <summary>
/// A single answer from a login's agent program, with no conversation and no tools, as T3 Code's
/// text generation writes commit messages, branch names and pull requests. Claude Code runs with
/// -p, Codex with exec, and the prompt goes in on standard input.
/// </summary>
public static class AgentText
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>The answer, or null with the reason it couldn't be had.</summary>
    public static async Task<(string? Text, string? Problem)> AskAsync(AgentProvider login, string prompt, string folder)
    {
        string? answerFile = null;
        string arguments;
        if (login.Kind == ProviderKind.Codex)
        {
            answerFile = Path.Combine(Path.GetTempPath(), $"agent-orange-answer-{Guid.NewGuid():N}.txt");
            arguments = $"exec --skip-git-repo-check --sandbox read-only --output-last-message \"{answerFile}\" -";
        }
        else
        {
            arguments = "-p --output-format text --strict-mcp-config --setting-sources project,local";
        }

        Process? process = null;
        try
        {
            process = Process.Start(AgentProviders.SessionStartInfo(login, arguments, folder));
            if (process is null) return (null, $"Couldn't start {login.Program}.");
            ChildProcesses.Track(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();
            using var waiting = new CancellationTokenSource(Limit);
            await process.WaitForExitAsync(waiting.Token);
            var said = answerFile is not null && File.Exists(answerFile) ? await File.ReadAllTextAsync(answerFile) : await output;
            if (process.ExitCode != 0 || said.Trim().Length == 0) return (null, GitTools.OneLine(await errors) is { Length: > 0 } why ? why : "It gave no answer.");
            return (said.Trim(), null);
        }
        catch (OperationCanceledException)
        {
            return (null, "It took too long to answer.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return (null, error.Message);
        }
        finally
        {
            try
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
            process?.Dispose();
            if (answerFile is not null)
            {
                try
                {
                    File.Delete(answerFile);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Temp, so it goes in time anyway.
                }
            }
        }
    }

    /// <summary>The first JSON object in an answer, which may come wrapped in a code block or words.</summary>
    public static JsonElement? JsonIn(string? text)
    {
        if (text is null) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var json = JsonDocument.Parse(text[start..(end + 1)]);
            return json.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
