using System.IO;

namespace AgentOrange;

/// <summary>What merging a branch did: merged it, stopped on files that conflict (and took the merge back), or couldn't, saying why.</summary>
public sealed record MergeResult(bool Merged, IReadOnlyList<string> Conflicts, string? Problem);

/// <summary>
/// Git worktrees for the parts of a milestone split across agents (#94): each is a checkout of the
/// project of its own, on a branch of its own made from the commit the split started at, sharing the
/// project's Git data, so parts never touch each other's files. Merged back in the project's own folder.
/// </summary>
public static partial class GitTools
{
    /// <summary>The branch checked out in the folder (null when it's on no branch) and its commit, or null when Git couldn't say.</summary>
    public static async Task<(string? Branch, string Commit)?> CheckedOutAsync(string folder)
    {
        if (await RunAsync("git", folder, "rev-parse", "HEAD") is not { ExitCode: 0 } head) return null;
        var branch = await RunAsync("git", folder, "symbolic-ref", "--quiet", "--short", "HEAD") is { ExitCode: 0 } named ? named.Output.Trim() : null;
        return (branch is { Length: > 0 } ? branch : null, head.Output.Trim());
    }

    /// <summary>A new worktree of the folder's repo at the path, on a new branch made from the commit. Null once made, or else why not.</summary>
    public static async Task<string?> AddWorktreeAsync(string folder, string path, string branch, string commit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var made = await RunAsync("git", folder, "worktree", "add", "--quiet", "-b", branch, path, commit);
        return made is { ExitCode: 0 } ? null : $"Git couldn't make a worktree for {branch}: {OneLine(made?.Errors ?? "Git isn't on this PC.")}";
    }

    /// <summary>Takes a worktree away, and its branch once it's merged. Null once done, or else why not.</summary>
    public static async Task<string?> RemoveWorktreeAsync(string folder, string path, string branch)
    {
        if (Directory.Exists(path) && await RunAsync("git", folder, "worktree", "remove", path) is { ExitCode: not 0 } removed) return $"Git couldn't take the worktree at {path} away: {OneLine(removed.Errors)}";
        await RunAsync("git", folder, "worktree", "prune");
        return await RunAsync("git", folder, "branch", "--delete", branch) is { ExitCode: not 0 } deleted ? $"Git couldn't delete the branch {branch}: {OneLine(deleted.Errors)}" : null;
    }

    /// <summary>Files changed in the folder and not committed, new ones too unless only tracked files count, or null when Git couldn't say.</summary>
    public static async Task<List<string>?> NotCommittedAsync(string folder, bool trackedOnly = false)
    {
        string[] untracked = trackedOnly ? ["--untracked-files=no"] : ["--untracked-files=all"];
        if (await RunAsync("git", folder, ["-c", "core.quotepath=false", "status", "--porcelain", .. untracked]) is not { ExitCode: 0 } status) return null;
        return [.. status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim()).Where(path => path.Length > 0)];
    }

    /// <summary>How many commits the branch has past the commit, or null when Git couldn't say.</summary>
    public static async Task<int?> CommitsPastAsync(string folder, string commit, string branch) =>
        await RunAsync("git", folder, "rev-list", "--count", $"{commit}..{branch}") is { ExitCode: 0 } counted && int.TryParse(counted.Output.Trim(), out var count) ? count : null;

    /// <summary>
    /// Merges the branch into the one checked out in the folder: a fast-forward when nothing else has
    /// happened since, or else a merge commit with the message. On a conflict, the merge is taken back,
    /// so the folder is as it was, and the files are said.
    /// </summary>
    public static async Task<MergeResult> MergeBranchAsync(string folder, string branch, string message)
    {
        var merged = await RunAsync("git", folder, "merge", "--no-edit", "-m", message, branch);
        if (merged is { ExitCode: 0 }) return new MergeResult(true, [], null);
        var conflicts = await RunAsync("git", folder, "-c", "core.quotepath=false", "diff", "--name-only", "--diff-filter=U") is { ExitCode: 0 } listed
            ? listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Trim()).Where(path => path.Length > 0).ToList()
            : [];
        // Whatever it got to, it's taken back, so the person's folder is never left half merged.
        await RunAsync("git", folder, "merge", "--abort");
        if (conflicts.Count > 0) return new MergeResult(false, conflicts, null);
        return new MergeResult(false, [], OneLine(merged is null ? "Git isn't on this PC." : merged.Errors.Length > 0 ? merged.Errors : merged.Output));
    }
}
