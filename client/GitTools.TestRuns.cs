using System.IO;

namespace AgentOrange;

/// <summary>Git for test machines: the commit a run is for, and a checkout of it on the PC that runs it.</summary>
public static partial class GitTools
{
    /// <summary>
    /// The commit checked out in the folder, or the one named (a commit id, its start, or a branch
    /// or tag), and whether origin has it, which a test machine needs to fetch it. Origin's
    /// branches as last fetched or pushed from here count.
    /// </summary>
    public static async Task<(string Sha, string Subject, bool Pushed, string? Problem)> PushedHeadAsync(string folder, string? commit = null)
    {
        if (commit is not null && !CommitName().IsMatch(commit)) return ("", "", false, $"{commit} isn't a commit: give a commit id, the start of one, or a branch or tag name.");
        if (await RunAsync("git", folder, "log", "-1", "--format=%H%n%s", commit is null ? "HEAD" : $"{commit}^{{commit}}", "--") is not { ExitCode: 0 } head || head.Output.Split('\n') is not [var sha, var subject, ..])
        {
            return ("", "", false, commit is null ? "This folder has no commits yet." : $"The project's repo on this PC has no commit {commit}. Fetch or pull first if it was made elsewhere.");
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

    /// <summary>
    /// Origin's default branch as this PC last heard it, such as origin/main, or origin/main or
    /// origin/master when origin hasn't said. Null with none of those.
    /// </summary>
    public static async Task<string?> DefaultBranchAsync(string folder)
    {
        if (await RunAsync("git", folder, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD") is { ExitCode: 0 } head && head.Output.Trim() is { Length: > 0 } said) return said;
        foreach (var guess in new[] { "origin/main", "origin/master" })
        {
            if (await RunAsync("git", folder, "rev-parse", "--verify", "--quiet", guess) is { ExitCode: 0 }) return guess;
        }
        return null;
    }

    /// <summary>
    /// The commits on a branch as this PC has it, newest first, each after every commit built on
    /// it, as far back as a few thousand. Null when Git couldn't say.
    /// </summary>
    public static async Task<List<string>?> BranchHistoryAsync(string folder, string branch) =>
        await RunAsync("git", folder, "rev-list", "--topo-order", "--max-count=5000", branch, "--") is { ExitCode: 0 } listed
            ? [.. listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;

    /// <summary>A file's text as committed, or null if the commit doesn't have it.</summary>
    public static async Task<string?> FileAtCommitAsync(string folder, string commit, string path) =>
        IsCommitId(commit) && await RunAsync("git", folder, "show", $"{commit}:{path}") is { ExitCode: 0 } shown ? shown.Output : null;

#if DEBUG
    // Headless checks only: a bare repo in a scratch folder stands in for GitHub, as the checks
    // mustn't push anywhere real. Git's file protocol is otherwise never allowed.
    private static bool IsLocalRepoForChecks(string url) => Headless.Active && Path.IsPathFullyQualified(url) && Directory.Exists(url);

    /// <summary>Headless checks only: makes a folder in the data folder a repo with everything in it committed, and gives the commit.</summary>
    internal static async Task<string?> CommitAllForChecksAsync(string folder, string? origin = null)
    {
        if (!Headless.Active || !folder.StartsWith(Headless.DataFolder, StringComparison.OrdinalIgnoreCase)) return null;
        Directory.CreateDirectory(folder);
        if (!Directory.Exists(Path.Combine(folder, ".git"))) await RunAsync("git", folder, "init", "--quiet");
        await RunAsync("git", folder, "add", "--all");
        await RunAsync("git", folder, "-c", "user.name=Agent Orange check", "-c", "user.email=agent-orange-check", "commit", "--quiet", "--allow-empty", "--message", "Check");
        if (origin is not null && await RunAsync("git", folder, "remote", "set-url", "origin", origin) is not { ExitCode: 0 }) await RunAsync("git", folder, "remote", "add", "origin", origin);
        return await RunAsync("git", folder, "rev-parse", "HEAD") is { ExitCode: 0 } head ? head.Output.Trim() : null;
    }

    /// <summary>Headless checks only: the branch checked out and the subjects of its latest commits, newest first.</summary>
    internal static async Task<string> LogForChecksAsync(string folder)
    {
        var branch = await RunAsync("git", folder, "symbolic-ref", "--quiet", "--short", "HEAD") is { ExitCode: 0 } named ? named.Output.Trim() : "no branch";
        var log = await RunAsync("git", folder, "log", "-8", "--format=%s") is { ExitCode: 0 } subjects ? string.Join(" | ", subjects.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) : "unknown";
        return $"on {branch}: {log}";
    }

    /// <summary>Headless checks only: a clone of one folder in the data folder into another, as a person's own clone would be.</summary>
    internal static async Task<bool> CloneForChecksAsync(string from, string to)
    {
        if (!Headless.Active || !from.StartsWith(Headless.DataFolder, StringComparison.OrdinalIgnoreCase) || !to.StartsWith(Headless.DataFolder, StringComparison.OrdinalIgnoreCase)) return false;
        return await RunAsync("git", Headless.DataFolder, null, ["clone", "--quiet", "--", from, to], default, new() { ["GIT_ALLOW_PROTOCOL"] = "file" }) is { ExitCode: 0 };
    }
#endif

    /// <summary>
    /// Puts the commit in the folder, cloning the repo there first if it isn't yet: detached at the
    /// commit, with anything a run left behind removed, except ignored files such as build output,
    /// which can take long to make again. Then its submodules and large files, as a clone gets.
    /// </summary>
    public static async Task<GitResult> TestCheckoutAsync(string repoUrl, string folder, string commit, CancellationToken cancel, Action<string> progress, string? borrowFrom = null)
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

        // Another clone of the same repo on this PC that the person chose to borrow from: only read.
        var borrowing = borrowFrom is null ? null : await BorrowableAsync(borrowFrom, repoUrl, folder, local, progress);

        var hasRepo = await Task.Run(() => Directory.Exists(Path.Combine(folder, ".git")));
        if (!hasRepo)
        {
            if (await Task.Run(() => Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))
            {
                return new GitResult(false, $"{folder} isn't empty and isn't a clone of this project's repo, so nothing was put there.");
            }
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder))!;
            await Task.Run(() => Directory.CreateDirectory(parent));
            progress($"Cloning {HideLogins(repoUrl)} into {folder}{(borrowing is null ? "" : $", copying what it can from {borrowFrom}")}");
            string[] borrowed = borrowing is null ? [] : ["--reference-if-able", borrowing, "--dissociate"];
            var cloned = local
                ? await RunAsync("git", parent, null, ["clone", "--quiet", .. borrowed, "--", repoUrl, folder], cancel, new(allowLocal!) { ["GIT_LFS_SKIP_SMUDGE"] = "1" }) is { ExitCode: 0 } ? new GitResult(true, folder) : new GitResult(false, "Couldn't clone the local repo.")
                : await CloneAsync(repoUrl, parent, cancel, _ => { }, exactFolder: folder, progress: progress, borrowFrom: borrowing);
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

        if (borrowing is not null) await BorrowLargeFilesAsync(borrowing, folder, progress);
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

    /// <summary>
    /// Whether a folder can be borrowed from for a test checkout: a clone whose origin is the same
    /// repo, apart from the runner's own. Its Git folder (shared by its worktrees), or null, having
    /// said why not. Only reads it; nothing there is ever changed.
    /// </summary>
    private static async Task<string?> BorrowableAsync(string from, string repoUrl, string folder, bool local, Action<string> progress)
    {
        string? Skip(string why)
        {
            progress($"Not copying from {from}: {why} Downloading instead.");
            return null;
        }
        if (!Path.IsPathFullyQualified(from) || !await FolderExistsAsync(from)) return Skip("that folder isn't there.");
        var mine = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var theirs = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        if (theirs.StartsWith(mine, StringComparison.OrdinalIgnoreCase) || mine.StartsWith(theirs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Skip("it's the test runs' own folder, or holds it.");
        }
        if (await RunAsync("git", from, "remote", "get-url", "origin") is not { ExitCode: 0 } origin) return Skip("it isn't a clone with an origin.");
        var same = local
            ? string.Equals(Path.GetFullPath(origin.Output.Trim()), Path.GetFullPath(repoUrl), StringComparison.OrdinalIgnoreCase)
            : SameRepo(origin.Output.Trim(), repoUrl);
        if (!same) return Skip("its origin is another repo.");
        if (await RunAsync("git", from, "rev-parse", "--path-format=absolute", "--git-common-dir") is not { ExitCode: 0 } common || common.Output.Trim().Length == 0)
        {
            return Skip("Git couldn't say where its data is.");
        }
        return Path.GetFullPath(common.Output.Trim());
    }

    /// <summary>
    /// The large files this commit needs that the borrowed clone already has, put in the test
    /// checkout's own Git LFS store before it fetches, so they aren't downloaded again. Hard links
    /// where both are on one drive (Git LFS never changes a file it has), copies otherwise.
    /// </summary>
    private static async Task BorrowLargeFilesAsync(string fromGitFolder, string folder, Action<string> progress)
    {
        if (await LargeFileIdsAsync(folder) is not { Count: > 0 } ids) return; // No Git LFS, or no large files.
        if (await RunAsync("git", folder, "rev-parse", "--path-format=absolute", "--git-common-dir") is not { ExitCode: 0 } own) return;
        var (linked, copied) = await Task.Run(() =>
        {
            int links = 0, copies = 0;
            foreach (var id in ids)
            {
                string Place(string gitFolder) => Path.Combine(gitFolder, "lfs", "objects", id[..2], id[2..4], id);
                var source = Place(fromGitFolder);
                var target = Place(Path.GetFullPath(own.Output.Trim()));
                if (File.Exists(target) || !File.Exists(source)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (CreateHardLinkW(target, source, 0)) links++;
                    else
                    {
                        File.Copy(source, target);
                        copies++;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    AppLog.Write($"Couldn't borrow a large file for a test checkout: {error.Message}");
                }
            }
            return (links, copies);
        });
        if (linked + copied > 0) progress($"Took {linked + copied} of {ids.Count} large files from the borrowed clone{(copied > 0 ? $" ({copied} copied, as it's on another drive)" : "")}.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);

    // Set for the steps of a test checkout, and for what they call, only.
    private static readonly AsyncLocal<bool> NoStepLimit = new();

    private static bool IsCommitId(string text) => text.Length is >= 7 and <= 64 && text.All(Uri.IsHexDigit);

    // A commit as an agent names one: an id or its start, or a branch or tag. Never an option or a range.
    [System.Text.RegularExpressions.GeneratedRegex(@"\A(?!.*\.\.)[A-Za-z0-9][A-Za-z0-9._/~^-]{0,199}\z")]
    private static partial System.Text.RegularExpressions.Regex CommitName();
}
