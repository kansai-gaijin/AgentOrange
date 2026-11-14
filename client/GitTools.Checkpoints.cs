using System.IO;

namespace AgentOrange;

/// <summary>A file a turn changed, with its lines added and taken out.</summary>
public sealed record TurnChange(string Path, int Insertions, int Deletions);

/// <summary>
/// T3 Code's checkpoints: the folder as it stands, written as a Git tree through an index of its
/// own, so the folder's real index, branch and history are never touched. One before a turn and
/// one after say what the turn changed.
/// </summary>
public static partial class GitTools
{
    /// <summary>The folder's files as a tree, new ones included and ignored ones left out, or null if Git couldn't say.</summary>
    public static async Task<string?> CheckpointAsync(string folder)
    {
        var index = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agent-orange-index-{Guid.NewGuid():N}");
        var own = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            // From the last commit, so only what's changed has to be read again.
            await RunAsync("git", folder, null, ["read-tree", "HEAD"], default, own);
            if (await RunAsync("git", folder, null, ["add", "-A"], default, own) is not { ExitCode: 0 }) return null;
            return await RunAsync("git", folder, null, ["write-tree"], default, own) is { ExitCode: 0 } tree ? tree.Output.Trim() : null;
        }
        finally
        {
            try
            {
                File.Delete(index);
                File.Delete(index + ".lock");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Temp, so it goes in time anyway.
            }
        }
    }

    /// <summary>What changed between two checkpoints, file by file.</summary>
    public static async Task<List<TurnChange>> ChangesBetweenAsync(string folder, string before, string after)
    {
        var changes = new List<TurnChange>();
        if (before == after) return changes;
        if (await RunAsync("git", folder, "-c", "core.quotepath=false", "diff", "--numstat", "-M", before, after) is not { ExitCode: 0 } numbers) return changes;
        foreach (var line in numbers.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            var path = parts[^1].Trim();

            // A rename reads "old => new", or "dir/{old => new}/file".
            if (path.Contains("=>", StringComparison.Ordinal))
            {
                var open = path.IndexOf('{');
                var close = path.IndexOf('}');
                path = open >= 0 && close > open
                    ? path[..open] + path[(path.IndexOf("=>", open, StringComparison.Ordinal) + 2)..close].Trim() + path[(close + 1)..]
                    : path[(path.IndexOf("=>", StringComparison.Ordinal) + 2)..].Trim();
                path = path.Replace("//", "/");
            }
            changes.Add(new TurnChange(path, int.TryParse(parts[0], out var added) ? added : 0, int.TryParse(parts[1], out var removed) ? removed : 0));
        }
        return changes;
    }

    /// <summary>One file's changes between two checkpoints, as a unified diff.</summary>
    public static async Task<string> DiffBetweenAsync(string folder, string before, string after, string? path = null)
    {
        string[] scope = path is null ? [] : ["--", path];
        return await RunAsync("git", folder, ["-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "-M", before, after, .. scope]) is { ExitCode: 0 } diff ? diff.Output : "";
    }
}
