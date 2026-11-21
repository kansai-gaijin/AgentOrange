using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// Where a project's folder stands with its repo, as T3 Code's git panel shows it: the branch, how
/// far it is ahead of or behind its upstream, what's changed since it left the default branch, and
/// any pull request open for it.
/// </summary>
public sealed record RepoState
{
    public string? Branch { get; init; } // Null when no branch is checked out.
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int ChangedFiles { get; init; } // Not yet committed, untracked ones included.
    public string? DefaultBranch { get; init; } // Such as origin/main.
    public int AheadOfDefault { get; init; }
    public int Insertions { get; init; }
    public int Deletions { get; init; }
    public bool HasOrigin { get; init; }
    public string? PullRequestUrl { get; init; }
    public bool PullRequestOpen { get; init; }
    public bool Fetched { get; init; } // Ahead and behind are as GitHub has it now, not as last fetched.
    public string? Problem { get; init; }

    public bool HasChanges => ChangedFiles > 0;
    public bool OnDefaultBranch => Branch is not null && DefaultBranch is not null && DefaultBranch.EndsWith("/" + Branch, StringComparison.Ordinal);
}

public static partial class GitTools
{
    /// <summary>How the folder stands, after asking GitHub for what's new when fetch is set.</summary>
    public static async Task<RepoState> RepoStateAsync(string folder, bool fetch)
    {
        var hasOrigin = await RunAsync("git", folder, "remote", "get-url", "origin") is { ExitCode: 0 };
        string? problem = null;
        var fetched = false;
        if (fetch && hasOrigin)
        {
            var got = await RunAsync("git", folder, "fetch", "--prune", "--quiet", "origin");
            fetched = got is { ExitCode: 0 };
            if (!fetched) problem = got is null ? "Git wasn't found on this PC." : HideLogins(GitProblem("Couldn't check GitHub for changes", got.Errors));
        }

        if (await RunAsync("git", folder, "status", "--porcelain=v2", "--branch", "--untracked-files=all") is not { ExitCode: 0 } status)
        {
            return new RepoState { Problem = "This folder isn't a Git repo, or Git couldn't read it." };
        }
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0, changed = 0;
        foreach (var line in status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = line[14..] is "(detached)" ? null : line[14..].Trim();
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal)) upstream = line[18..].Trim();
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal) && AheadBehind().Match(line) is { Success: true } counts)
            {
                ahead = int.Parse(counts.Groups[1].Value);
                behind = int.Parse(counts.Groups[2].Value);
            }
            else if (!line.StartsWith('#')) changed++;
        }

        // The default branch, as origin says it, or main or master when it hasn't said.
        var defaultBranch = hasOrigin ? await DefaultBranchAsync(folder) : null;

        // What's changed since it left the default branch, committed or not.
        int insertions = 0, deletions = 0, aheadOfDefault = 0;
        if ((defaultBranch ?? upstream) is { } baseRef && await RunAsync("git", folder, "merge-base", "HEAD", baseRef) is { ExitCode: 0 } mergeBase)
        {
            var since = mergeBase.Output.Trim();
            if (await RunAsync("git", folder, "diff", "--shortstat", since) is { ExitCode: 0 } diff)
            {
                insertions = ShortStat(diff.Output, "insertion");
                deletions = ShortStat(diff.Output, "deletion");
            }
            if (await RunAsync("git", folder, "rev-list", "--count", $"{since}..HEAD") is { ExitCode: 0 } count) int.TryParse(count.Output.Trim(), out aheadOfDefault);
        }

        // A pull request for this branch, if GitHub has one.
        string? pullRequest = null;
        var pullRequestOpen = false;
        if (hasOrigin && branch is not null && defaultBranch?.EndsWith("/" + branch, StringComparison.Ordinal) != true
            && await RunAsync("gh", folder, "pr", "view", branch, "--json", "url,state") is { ExitCode: 0 } view)
        {
            try
            {
                using var json = JsonDocument.Parse(view.Output);
                pullRequest = json.RootElement.GetProperty("url").GetString();
                pullRequestOpen = json.RootElement.GetProperty("state").GetString() == "OPEN";
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Not one it could read, so as if there were none.
            }
        }

        return new RepoState
        {
            Branch = branch,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            ChangedFiles = changed,
            DefaultBranch = defaultBranch,
            AheadOfDefault = aheadOfDefault,
            Insertions = insertions,
            Deletions = deletions,
            HasOrigin = hasOrigin,
            PullRequestUrl = pullRequest,
            PullRequestOpen = pullRequestOpen,
            Fetched = fetched,
            Problem = problem,
        };
    }

    private static int ShortStat(string text, string kind) =>
        Regex.Match(text, $@"(\d+) {kind}") is { Success: true } found ? int.Parse(found.Groups[1].Value) : 0;

    /// <summary>The folder's branches, the latest worked on first, with origin's that have no local one.</summary>
    public static async Task<List<string>> BranchesAsync(string folder)
    {
        if (await RunAsync("git", folder, "for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", "refs/heads", "refs/remotes/origin") is not { ExitCode: 0 } refs) return [];
        var all = refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(name => name is not "origin" and not "origin/HEAD").ToList();
        var local = all.Where(name => !name.StartsWith("origin/", StringComparison.Ordinal)).ToHashSet();
        return [.. all.Where(name => !name.StartsWith("origin/", StringComparison.Ordinal) || !local.Contains(name[7..]))];
    }

    /// <summary>Checks out a branch, or origin's one as a local branch of the same name.</summary>
    public static Task<GitResult> SwitchBranchAsync(string folder, string name)
    {
        string[] arguments = name.StartsWith("origin/", StringComparison.Ordinal) ? ["switch", "--track", name] : ["switch", name];
        return GitStepAsync(folder, $"Couldn't switch to {name}", arguments);
    }

    public static Task<GitResult> CreateBranchAsync(string folder, string name) =>
        GitStepAsync(folder, $"Couldn't make the branch {name}", "switch", "-c", name);

    /// <summary>Makes the folder a Git repo, as T3 Code's "Initialize Git" does.</summary>
    public static Task<GitResult> InitializeAsync(string folder) => GitStepAsync(folder, "Couldn't start a Git repo here", "init");

    /// <summary>The checked-out commit's short id and subject.</summary>
    public static async Task<(string Sha, string Subject)> HeadAsync(string folder) =>
        await RunAsync("git", folder, "log", "-1", "--format=%h%n%s") is { ExitCode: 0 } head && head.Output.Split('\n') is [var sha, var subject, ..]
            ? (sha.Trim(), subject.Trim())
            : ("", "");

    public static Task<GitResult> PushAsync(string folder) =>
        GitStepAsync(folder, "Couldn't push", "push", "--quiet", "--set-upstream", "origin", "HEAD");

    public static Task<GitResult> PullAsync(string folder) =>
        GitStepAsync(folder, "Couldn't pull", "pull", "--ff-only", "--quiet");

    /// <summary>
    /// When this branch and its upstream each have commits the other hasn't: puts this branch's own commits
    /// after the upstream's, as if they'd been made after it. When both change the same files, it's put
    /// back as it was, and says which files, as only a person can say which change wins.
    /// </summary>
    public static async Task<GitResult> RebaseOnUpstreamAsync(string folder)
    {
        var rebased = await GitStepAsync(folder, "Couldn't bring in origin's commits", "pull", "--rebase", "--quiet");
        if (rebased.Worked) return rebased;
        var clashing = await RunAsync("git", folder, "-c", "core.quotepath=false", "diff", "--name-only", "--diff-filter=U") is { ExitCode: 0 } listed
            ? listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        await RunAsync("git", folder, "rebase", "--abort");
        if (clashing.Length == 0) return rebased;
        var named = string.Join(", ", clashing.Take(5)) + (clashing.Length > 5 ? $" and {clashing.Length - 5} more" : "");
        return new GitResult(false, $"This PC's commits and origin's both change {named}. Nothing was changed: decide which to keep, or ask an agent to merge them.");
    }

    /// <summary>A file changed in the folder and not yet committed, with its lines added and taken out.</summary>
    public sealed record ChangedFile(string Path, int Insertions, int Deletions);

    /// <summary>Every file changed since the last commit, new ones included, as T3 Code's commit dialog lists them.</summary>
    public static async Task<List<ChangedFile>> ChangedFilesAsync(string folder)
    {
        var files = new List<ChangedFile>();
        var counted = new Dictionary<string, (int Added, int Removed)>();
        if (await RunAsync("git", folder, "-c", "core.quotepath=false", "diff", "HEAD", "--numstat", "-M") is { ExitCode: 0 } numbers)
        {
            foreach (var line in numbers.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                counted[parts[^1].Trim()] = (int.TryParse(parts[0], out var added) ? added : 0, int.TryParse(parts[1], out var removed) ? removed : 0);
            }
        }
        if (await RunAsync("git", folder, "-c", "core.quotepath=false", "status", "--porcelain", "--untracked-files=all") is not { ExitCode: 0 } status) return files;
        foreach (var line in status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var path = line[3..].Trim();
            if (path.Contains(" -> ", StringComparison.Ordinal)) path = path[(path.IndexOf(" -> ", StringComparison.Ordinal) + 4)..];
            path = path.Trim('"');
            if (counted.TryGetValue(path, out var known))
            {
                files.Add(new ChangedFile(path, known.Added, known.Removed));
                continue;
            }

            // New, so every line is added.
            var lines = 0;
            try
            {
                var full = System.IO.Path.Combine(folder, path);
                if (File.Exists(full) && new FileInfo(full).Length < 2_000_000) lines = (await File.ReadAllLinesAsync(full)).Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Counted as none.
            }
            files.Add(new ChangedFile(path, lines, 0));
        }
        return files;
    }

    /// <summary>What's about to be committed, for writing its message: the files and the patch, cut short as T3 Code does.</summary>
    // Staged first, as T3 Code does, as the commit is about to be made: without it, new files aren't
    // in the patch at all, and the message is written about nothing.
    public static async Task<(string Files, string Patch)> CommitContextAsync(string folder, IReadOnlyList<string>? only)
    {
        string[] scope = only is { Count: > 0 } ? ["--", .. only] : [];
        await RunAsync("git", folder, ["add", "-A", .. scope]);
        var names = await RunAsync("git", folder, ["-c", "core.quotepath=false", "diff", "--cached", "--name-status", .. scope]);
        var patch = await RunAsync("git", folder, ["diff", "--cached", "--no-color", "--no-ext-diff", "--no-textconv", "--patch", "--minimal", .. scope]);
        return (Cut(names?.Output, 8000), Cut(patch?.Output, 50000));
    }

    private static string Cut(string? text, int most) => text is null ? "" : text.Length > most ? text[..most] : text;

    private static string? _noReplyEmail;

    /// <summary>
    /// The GitHub account's no-reply address, when the folder would otherwise commit under another
    /// one, such as a personal email set for all of Git on this PC. A commit's email goes wherever
    /// the repo does, and a private repo can be made public later. Null to commit as Git is set up.
    /// </summary>
    private static async Task<string?> NoReplyEmailAsync(string folder)
    {
        var configured = (await RunAsync("git", folder, "config", "user.email"))?.Output.Trim() ?? "";
        if (configured.EndsWith("@users.noreply.github.com", StringComparison.OrdinalIgnoreCase)) return null;
        if (_noReplyEmail is not null) return _noReplyEmail;
        if (await RunAsync("gh", folder, "api", "user", "--jq", ".id, .login") is not { ExitCode: 0 } user) return null;
        var parts = user.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !long.TryParse(parts[0], out var id)) return null;
        return _noReplyEmail = $"{id}+{parts[1]}@users.noreply.github.com";
    }

    /// <summary>Commits the changes, only the files given when some are left out. The value is the new commit's short id.</summary>
    public static async Task<GitResult> CommitAsync(string folder, string subject, string body, IReadOnlyList<string>? only)
    {
        string[] scope = only is { Count: > 0 } ? ["--", .. only] : [];
        var added = await GitStepAsync(folder, "Couldn't add the changes", ["add", "-A", .. scope]);
        if (!added.Worked) return added;
        string[] message = body.Trim().Length > 0 ? ["-m", subject, "-m", body.Trim()] : ["-m", subject];
        string[] identity = await NoReplyEmailAsync(folder) is { } noReply ? ["-c", $"user.email={noReply}"] : [];
        var made = await GitStepAsync(folder, "Couldn't commit", [.. identity, "commit", "--quiet", .. message, .. scope]);
        if (!made.Worked) return made;
        return await RunAsync("git", folder, "rev-parse", "--short", "HEAD") is { ExitCode: 0 } head ? new GitResult(true, head.Output.Trim()) : new GitResult(true, "");
    }

    /// <summary>
    /// A new branch's name, as T3 Code makes one: lower case, accents gone, anything else a dash,
    /// at most 64 long, under feature/ unless it has a folder of its own, numbered when it's taken.
    /// </summary>
    public static async Task<string> FeatureBranchNameAsync(string folder, string? suggested, string subject)
    {
        var text = (suggested is { Length: > 0 } ? suggested : subject).Normalize(System.Text.NormalizationForm.FormD);
        var plain = new string([.. text.Where(letter => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(letter) != System.Globalization.UnicodeCategory.NonSpacingMark)]).ToLowerInvariant();
        var name = Regex.Replace(plain, "[^a-z0-9/_-]", "-");
        name = Regex.Replace(name, "-{2,}", "-").Trim('-', '/');
        if (name.Length > 64) name = name[..64].Trim('-', '/');
        if (name.Length == 0) name = "update";
        if (!name.Contains('/')) name = $"feature/{name}";
        var taken = (await BranchesAsync(folder)).Select(each => each.StartsWith("origin/", StringComparison.Ordinal) ? each[7..] : each).ToHashSet();
        var chosen = name;
        for (var number = 2; taken.Contains(chosen); number++) chosen = $"{name}-{number}";
        return chosen;
    }

    /// <summary>What a pull request would cover: the commits, the files and the patch against its base.</summary>
    public static async Task<(string Commits, string Stat, string Patch)> PullRequestContextAsync(string folder, string baseRef)
    {
        var commits = await RunAsync("git", folder, "log", "--format=%s%n%b", $"{baseRef}..HEAD");
        var stat = await RunAsync("git", folder, "diff", "--stat", $"{baseRef}...HEAD");
        var patch = await RunAsync("git", folder, "diff", "--no-color", "--no-ext-diff", "--minimal", $"{baseRef}...HEAD");
        return (Cut(commits?.Output, 8000), Cut(stat?.Output, 8000), Cut(patch?.Output, 50000));
    }

    /// <summary>Opens a pull request with this title and description. The value is its address.</summary>
    public static async Task<GitResult> CreatePullRequestAsync(string folder, string branch, string baseRef, string title, string body)
    {
        var bodyFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agent-orange-pr-body-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(bodyFile, body);
            var target = baseRef.StartsWith("origin/", StringComparison.Ordinal) ? baseRef[7..] : baseRef;
            if (await RunAsync("gh", folder, "pr", "create", "--head", branch, "--base", target, "--title", title, "--body-file", bodyFile) is not { } made) return new GitResult(false, "The GitHub CLI wasn't found on this PC.");
            if (made.ExitCode != 0) return new GitResult(false, HideLogins(GitProblem("Couldn't open a pull request", made.Errors)));
            return new GitResult(true, made.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(line => line.StartsWith("https://", StringComparison.Ordinal))?.Trim() ?? "");
        }
        finally
        {
            try
            {
                File.Delete(bodyFile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Temp, so it goes in time anyway.
            }
        }
    }

    private static async Task<GitResult> GitStepAsync(string folder, string failure, params string[] arguments)
    {
        if (await RunAsync("git", folder, arguments) is not { } done) return new GitResult(false, "Git wasn't found on this PC.");
        return done.ExitCode == 0 ? new GitResult(true, done.Output.Trim()) : new GitResult(false, HideLogins(GitProblem(failure, done.Errors.Length > 0 ? done.Errors : done.Output)));
    }

    [GeneratedRegex(@"\+(\d+) -(\d+)")]
    private static partial Regex AheadBehind();
}
