using System.IO;

namespace AgentOrange;

/// <summary>Git for test machines: the commit a run is for, and a checkout of it on the PC that runs it.</summary>
public static partial class GitTools
{
    /// <summary>
    /// The commit checked out in the folder, and whether origin has it, which a test machine needs
    /// to fetch it. Origin's branches as last fetched or pushed from here count.
    /// </summary>
    public static async Task<(string Sha, string Subject, bool Pushed, string? Problem)> PushedHeadAsync(string folder)
    {
        if (await RunAsync("git", folder, "log", "-1", "--format=%H%n%s") is not { ExitCode: 0 } head || head.Output.Split('\n') is not [var sha, var subject, ..])
        {
            return ("", "", false, "This folder has no commits yet.");
        }
        sha = sha.Trim();
        var pushed = await RunAsync("git", folder, "branch", "--remotes", "--contains", sha) is { ExitCode: 0 } branches && branches.Output.Trim().Length > 0;
        return (sha, subject.Trim(), pushed, null);
    }

    /// <summary>
    /// Puts the commit in the folder, cloning the repo there first if it isn't yet: detached at the
    /// commit, with anything a run left behind removed, except ignored files such as build output,
    /// which can take long to make again. Then its submodules and large files, as a clone gets.
    /// </summary>
    public static async Task<GitResult> TestCheckoutAsync(string repoUrl, string folder, string commit, CancellationToken cancel, Action<string> progress)
    {
        if (RepoUrlProblem(repoUrl) is { } problem) return new GitResult(false, problem);
        if (!IsCommitId(commit)) return new GitResult(false, "That isn't a commit id.");

        var hasRepo = await Task.Run(() => Directory.Exists(Path.Combine(folder, ".git")));
        if (!hasRepo)
        {
            if (await Task.Run(() => Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))
            {
                return new GitResult(false, $"{folder} isn't empty and isn't a clone of this project's repo, so nothing was put there.");
            }
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder))!;
            await Task.Run(() => Directory.CreateDirectory(parent));
            progress($"Cloning {HideLogins(repoUrl)} into {folder}");
            var cloned = await CloneAsync(repoUrl, parent, cancel, _ => { }, exactFolder: folder, progress: progress);
            if (!cloned.Worked) return cloned;
        }
        else if (await RunAsync("git", folder, "remote", "get-url", "origin") is not { ExitCode: 0 } origin || !SameRepo(origin.Output.Trim(), repoUrl))
        {
            return new GitResult(false, $"{folder} is a clone of another repo, so nothing was changed there. Remove it, or choose another work folder.");
        }

        if (cancel.IsCancellationRequested) return new GitResult(false, "Cancelled.");
        progress($"Fetching {commit[..Math.Min(12, commit.Length)]}");
        if (await RunAsync("git", folder, null, ["fetch", "--quiet", "origin", commit], cancel) is not { ExitCode: 0 }
            && await RunAsync("git", folder, null, ["fetch", "--quiet", "origin"], cancel) is { ExitCode: not 0 } fetched)
        {
            return new GitResult(false, HideLogins(GitProblem("Couldn't fetch from origin", fetched.Errors)));
        }
        if (await RunAsync("git", folder, "cat-file", "-e", $"{commit}^{{commit}}") is not { ExitCode: 0 })
        {
            return new GitResult(false, $"Origin doesn't have the commit {commit}. Push it, then run the tests again.");
        }

        var checkedOut = await GitStepAsync(folder, $"Couldn't check out {commit}", "-c", "advice.detachedHead=false", "checkout", "--quiet", "--force", "--detach", commit);
        if (!checkedOut.Worked) return checkedOut;
        var cleaned = await GitStepAsync(folder, "Couldn't remove what an earlier run left", "clean", "-d", "--force", "--quiet");
        if (!cleaned.Worked) return cleaned;

        progress("Fetching submodules and large files, if it has any");
        if (await FinishDownloadAsync(folder, cancel, progress) is { } missing) return new GitResult(false, missing);
        return new GitResult(true, folder);
    }

    private static bool IsCommitId(string text) => text.Length is >= 7 and <= 64 && text.All(Uri.IsHexDigit);
}
