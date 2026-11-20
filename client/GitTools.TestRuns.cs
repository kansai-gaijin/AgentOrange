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
        var pushed = await RunAsync("git", folder, "branch", "--remotes", "--list", "origin/*", "--contains", sha) is { ExitCode: 0 } branches && branches.Output.Trim().Length > 0;
        return (sha, subject.Trim(), pushed, null);
    }

    /// <summary>
    /// The folder's own origin, as this PC's Git has it, which is where a test machine fetches from.
    /// Not the project's synced URL, which the server or another PC could change.
    /// </summary>
    public static async Task<(string? Url, string? Problem)> OriginForTestsAsync(string folder)
    {
        if (await RunAsync("git", folder, "remote", "get-url", "origin") is not { ExitCode: 0 } origin || origin.Output.Trim().Length == 0)
        {
            return (null, "This folder's repo has no origin remote, so a test machine would have nowhere to fetch it from.");
        }
        var url = origin.Output.Trim();
#if DEBUG
        if (IsLocalRepoForChecks(url)) return (url, null);
#endif
        return RepoUrlProblem(url) is { } problem ? (null, $"This folder's origin can't be sent to a test machine. {problem}") : (url, null);
    }

    /// <summary>A file's text as committed, or null if the commit doesn't have it.</summary>
    public static async Task<string?> FileAtCommitAsync(string folder, string commit, string path) =>
        IsCommitId(commit) && await RunAsync("git", folder, "show", $"{commit}:{path}") is { ExitCode: 0 } shown ? shown.Output : null;

#if DEBUG
    // Headless checks only: a bare repo in a scratch folder stands in for GitHub, as the checks
    // mustn't push anywhere real. Git's file protocol is otherwise never allowed.
    private static bool IsLocalRepoForChecks(string url) => Headless.Active && Path.IsPathFullyQualified(url) && Directory.Exists(url);
#endif

    /// <summary>
    /// Puts the commit in the folder, cloning the repo there first if it isn't yet: detached at the
    /// commit, with anything a run left behind removed, except ignored files such as build output,
    /// which can take long to make again. Then its submodules and large files, as a clone gets.
    /// </summary>
    public static async Task<GitResult> TestCheckoutAsync(string repoUrl, string folder, string commit, CancellationToken cancel, Action<string> progress)
    {
        var local = false;
#if DEBUG
        local = IsLocalRepoForChecks(repoUrl);
#endif
        if (!local && RepoUrlProblem(repoUrl) is { } problem) return new GitResult(false, problem);
        if (!IsCommitId(commit)) return new GitResult(false, "That isn't a commit id.");
        Dictionary<string, string>? allowLocal = local ? new() { ["GIT_ALLOW_PROTOCOL"] = "https:ssh:file" } : null;

        // A fetch, checkout or clean of a big repo on a cold disk can take minutes, so these steps
        // have no time limit of their own. The run's own limit, through cancel, still stops them.
        NoStepLimit.Value = true;

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
            var cloned = local
                ? await RunAsync("git", parent, null, ["clone", "--quiet", "--", repoUrl, folder], cancel, allowLocal) is { ExitCode: 0 } ? new GitResult(true, folder) : new GitResult(false, "Couldn't clone the local repo.")
                : await CloneAsync(repoUrl, parent, cancel, _ => { }, exactFolder: folder, progress: progress);
            if (!cloned.Worked) return cloned;
        }
        else if (await RunAsync("git", folder, "remote", "get-url", "origin") is not { ExitCode: 0 } origin
            || !(local ? string.Equals(Path.GetFullPath(origin.Output.Trim()), Path.GetFullPath(repoUrl), StringComparison.OrdinalIgnoreCase) : SameRepo(origin.Output.Trim(), repoUrl)))
        {
            return new GitResult(false, $"{folder} is a clone of another repo, so nothing was changed there. Remove it, or choose another work folder.");
        }

        if (cancel.IsCancellationRequested) return new GitResult(false, "Cancelled.");
        progress($"Fetching {commit[..Math.Min(12, commit.Length)]}");
        if (await RunAsync("git", folder, null, ["fetch", "--quiet", "origin", commit], cancel, allowLocal) is not { ExitCode: 0 }
            && await RunAsync("git", folder, null, ["fetch", "--quiet", "origin"], cancel, allowLocal) is { ExitCode: not 0 } fetched)
        {
            return new GitResult(false, HideLogins(GitProblem("Couldn't fetch from origin", fetched.Errors)));
        }
        if (await RunAsync("git", folder, "cat-file", "-e", $"{commit}^{{commit}}") is not { ExitCode: 0 })
        {
            return new GitResult(false, $"Origin doesn't have the commit {commit}. Push it, then run the tests again.");
        }

        // The folder is the runner's own, and it runs one test at a time, so a lock left in it is
        // from a Git that was stopped part way (a cancelled run, a crash, a restart). Without this,
        // every later run would fail on it until someone removed it by hand.
        await Task.Run(() =>
        {
            foreach (var name in new[] { "index.lock", "HEAD.lock", "shallow.lock" })
            {
                try { File.Delete(Path.Combine(folder, ".git", name)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        });

        var checkedOut = await RunStepAsync($"Couldn't check out {commit}", "-c", "advice.detachedHead=false", "checkout", "--quiet", "--force", "--detach", commit);
        if (!checkedOut.Worked) return checkedOut;
        var cleaned = await RunStepAsync("Couldn't remove what an earlier run left", "clean", "-d", "--force", "--quiet");
        if (!cleaned.Worked) return cleaned;

        progress("Fetching submodules and large files, if it has any");
        if (await FinishDownloadAsync(folder, cancel, progress) is { } missing) return new GitResult(false, missing);
        return new GitResult(true, folder);

        async Task<GitResult> RunStepAsync(string failure, params string[] arguments)
        {
            if (cancel.IsCancellationRequested) return new GitResult(false, "Cancelled.");
            if (await RunAsync("git", folder, null, arguments, cancel) is not { } done) return new GitResult(false, "Git wasn't found on this PC.");
            return done.ExitCode == 0 ? new GitResult(true, done.Output.Trim()) : new GitResult(false, HideLogins(GitProblem(failure, done.Errors.Length > 0 ? done.Errors : done.Output)));
        }
    }

    // Set for the steps of a test checkout, and for what they call, only.
    private static readonly AsyncLocal<bool> NoStepLimit = new();

    private static bool IsCommitId(string text) => text.Length is >= 7 and <= 64 && text.All(Uri.IsHexDigit);
}
