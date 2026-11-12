using System.IO;

namespace AgentOrange;

/// <summary>Looks at what a folder on this PC holds, and how it relates to a project's repo.</summary>
public static partial class GitTools
{
    /// <summary>Returns null if Git can't be run at all.</summary>
    public static async Task<FolderRepo?> InspectFolderAsync(string folder)
    {
        // An empty prefix means this is the top of the repo, even through a junction or symlink.
        var prefix = await RunAsync("git", folder, "rev-parse", "--is-inside-work-tree", "--show-prefix");
        if (prefix is null) return null;
        if (prefix.ExitCode != 0)
        {
            if (!prefix.Errors.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                return new FolderRepo(false, false, null, GitProblem("Git couldn't read that folder", prefix.Errors));
            }

            // Git says the same about a repo it can't make sense of, whose history may still be
            // there. Setting up Git over the top of that could lose it.
            var gitData = Path.Combine(folder, ".git");
            return await Task.Run(() => Directory.Exists(gitData) || File.Exists(gitData))
                ? new FolderRepo(false, false, null, "This folder's Git data is damaged, so nothing was changed. Repair it or use another copy.")
                : new FolderRepo(false, false, null);
        }

        // Git's own data, or a repo with no files checked out, also answers with an empty prefix.
        var answers = prefix.Output.Split('\n');
        if (answers[0].Trim() != "true") return new FolderRepo(false, false, null, "That's Git's own data, not a copy of the project. Choose the project's top folder.");
        if (answers.Length > 1 && answers[1].Trim().Length > 0) return new FolderRepo(true, false, null);

        var origin = await RunAsync("git", folder, "remote", "get-url", "origin");
        if (origin is null) return null;
        if (origin.ExitCode == 0)
        {
            var remote = WithoutCredentials(origin.Output.Trim());
            var (gitHubForm, aliasProblem) = await GitHubFormAsync(remote, folder);
            // The only problem reading an alias has is SSH settings that haven't been allowed to run.
            return new FolderRepo(true, true, remote, aliasProblem, gitHubForm) { OriginWithLogin = origin.Output.Trim(), NeedsSettingsApproval = aliasProblem is not null };
        }

        // Git documents exit code 2 for a remote that doesn't exist.
        return origin.ExitCode == 2
            ? new FolderRepo(true, true, null)
            : new FolderRepo(true, true, null, GitProblem("Git couldn't read that folder", origin.Errors));
    }

    /// <summary>Checks a folder really holds this project's repo. Returns a problem to show, or null if it does.</summary>
    public static async Task<string?> FolderProblemAsync(string folder, string repoUrl) => (await FolderProblemDetailAsync(folder, repoUrl))?.Text;

    /// <summary>The same check, with what kind of problem it is and the remote it saw, for deciding what to offer.</summary>
    public static async Task<FolderProblem?> FolderProblemDetailAsync(string folder, string repoUrl)
    {
        if (!await FolderExistsAsync(folder)) return new FolderProblem("That folder doesn't exist.");
        if (repoUrl.Length == 0) return null;

        var state = await InspectFolderAsync(folder);
        if (state is null) return new FolderProblem("Git wasn't found on this PC.");
        if (state.Problem is not null) return new FolderProblem(state.Problem, state.NeedsSettingsApproval ? FolderProblemKind.NeedsSettingsApproval : FolderProblemKind.Other);
        if (state.IsInsideAnotherRepo) return new FolderProblem("That folder is inside a repo. Choose the repo's top folder.");
        if (state.Origin is null) return new FolderProblem("That folder isn't connected to a repo, so it can't be this project.", FolderProblemKind.NotConnected, "");

        // The same owner and name somewhere that isn't really GitHub, or in a way Git won't use.
        // Connecting it points it at the real repo, after the same check of its history.
        if (RepoUrlProblem(state.OriginToCompare!) is not null) return new FolderProblem($"That folder uses a different repo address, which isn't one GitHub uses: {state.Origin}", FolderProblemKind.OnAnotherRepo, state.OriginToCompare);

        if (!SameRepo(state.OriginToCompare!, repoUrl))
        {
            var (renamed, moveProblem, originAnswered, originNow) = await MoveIfRepoRenamedAsync(folder, state, repoUrl);
            if (moveProblem is not null) return new FolderProblem(moveProblem);
            if (renamed)
            {
                state = await InspectFolderAsync(folder);
                if (state?.Problem is { } unreadable) return new FolderProblem(unreadable, state.NeedsSettingsApproval ? FolderProblemKind.NeedsSettingsApproval : FolderProblemKind.Other);
                if (state?.Origin is null) return new FolderProblem("That folder isn't connected to a repo, so it can't be this project.", FolderProblemKind.NotConnected, "");
            }

            // The project can be on an old name too, and it moves on when it's next opened, and the folder with it.
            else
            {
                var projectRepo = await CurrentRepoUrlAsync(repoUrl);
                if (!SameRepo(projectRepo.Current, state.OriginToCompare!) && !(projectRepo.Answered && originAnswered && SameRepo(projectRepo.Current, originNow)))
                {
                    // Only certain if this repo was really found. GitHub would then have led the folder's name to it, if it were one.
                    if (projectRepo.Found && originAnswered) return new FolderProblem($"That folder uses a different repo: {state.Origin}", FolderProblemKind.OnAnotherRepo, state.OriginToCompare);
                    var why = projectRepo.Answered
                        ? "GitHub can't find this project's repo, so it can't be told whether that folder is it under another name."
                        : NoAnswerAboutOlderName;
                    return new FolderProblem($"That folder may use a different repo: {state.Origin}. {why}", FolderProblemKind.MayBeOnAnotherRepo, state.OriginToCompare);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// An older copy of a repo that's since been renamed is still that repo. GitHub says so without
    /// running anything from the folder, so its remote just moves to the new name, the way it
    /// connects now. Moved is false if it isn't the same repo, and Problem says why one that is couldn't be moved.
    /// </summary>
    public static async Task<(bool Moved, string? Problem, bool OriginAnswered, string OriginNow)> MoveIfRepoRenamedAsync(string folder, FolderRepo state, string repoUrl)
    {
        if (state.Origin is null) return (false, null, true, "");
        var originName = await CurrentRepoUrlAsync(state.OriginToCompare!);
        var (current, answered) = (originName.Current, originName.Answered);
        if (!answered || SameRepo(current, state.OriginToCompare!) || !SameRepo(current, repoUrl)) return (false, null, answered, current);
        var problem = await MoveOriginAsync(folder, SameWayTo(state, repoUrl), state.OriginToCompare!);
        return (problem is null, problem, true, current);
    }

    /// <summary>Said when a folder may be on an old name of the repo, and GitHub didn't say.</summary>
    public const string NoAnswerAboutOlderName = "GitHub didn't say, so it can't be checked whether that's this repo under an older name.";

    /// <summary>A disconnected network drive can take a long time to answer, so this stays off the UI thread.</summary>
    public static Task<bool> FolderExistsAsync(string folder) => Task.Run(() => Directory.Exists(folder));

    /// <summary>Whether a folder can move to a new repo: readable, a repo's top or no repo at all, and not using an unrelated one.</summary>
    public static async Task<bool> FolderCanReplaceRepoAsync(string folder, string oldRepo)
    {
        if (!await FolderExistsAsync(folder)) return false;
        var state = await InspectFolderAsync(folder);
        return state is { Problem: null, IsInsideAnotherRepo: false }
            && (state.Origin is null || SameRepo(state.OriginToCompare!, oldRepo));
    }

    // Git there would make every folder underneath count as inside this repo, and refuse them.
    private static Task<string?> TooBroadForAProjectAsync(string folder) => Task.Run(() =>
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetDirectoryName(full) is null) return (string?)"That's a whole drive, and setting up Git there would take in everything on it. Choose a folder on it.";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && FolderIdentity.Same(full, home)
            ? "That's your whole user folder, and setting up Git there would take in everything in it. Choose a folder inside it."
            : null;
    });

    /// <summary>
    /// A repo with commits, none of whose branches this folder has, may not be the same project.
    /// It can also just be a copy that's behind, so this is only ever a reason to ask. Nothing
    /// is fetched.
    /// </summary>
    public static async Task<FolderHistory> CompareFolderHistoryAsync(string folder, string url)
    {
        if (await FolderHasNoCommitsAsync(folder) is not false) return FolderHistory.CantTell;

        // Looking for a commit can fetch it in a folder set up as a partial clone, which could use
        // that folder's own settings, and those only run once they've been allowed.
        var partialClone = await RunAsync("git", folder, "config", "--get-regexp", @"^(extensions\.partialclone|remote\..*\.promisor)$");
        if (partialClone is not { ExitCode: 1 } && (await FolderRunnableSettingsAsync(folder) is not { } settings || !AreAllowed(folder, settings))) return FolderHistory.CantTell;

        var form = await ReachableFormAsync(url) ?? url;
        var heads = await RunAsync("git", Path.GetTempPath(), null, ["ls-remote", "--", form, "HEAD", "refs/heads/*"], default, await SshSettingsAsync(Path.GetTempPath()));
        if (heads is not { ExitCode: 0 }) return FolderHistory.CantTell;

        // The main branch comes first, ahead of any number of branches made by tools.
        var commits = heads.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')[0].Trim()).Take(50).ToList();
        if (commits.Count == 0) return FolderHistory.RepoIsEmpty;

        Dictionary<string, string> noFetching = new() { ["GIT_NO_LAZY_FETCH"] = "1" };
        foreach (var commit in commits)
        {
            var found = await RunAsync("git", folder, null, ["cat-file", "-e", commit], default, noFetching);
            if (found is { ExitCode: 0 }) return FolderHistory.SharesCommits;
            if (found is not { ExitCode: 1 }) return FolderHistory.CantTell;
        }
        return FolderHistory.SharesNothing;
    }

    /// <summary>Whether a repo has anything in it, or null if that couldn't be found out.</summary>
    public static async Task<bool?> RepoHasContentAsync(string url)
    {
        var form = await ReachableFormAsync(url) ?? url;
        var settings = await SshSettingsAsync(Path.GetTempPath());

        // Just its branches first, which GitHub picks out itself, as a repo can have so many refs
        // that listing them all runs out of time. Only one with no branches needs the rest looked at.
        var branches = await RunAsync("git", Path.GetTempPath(), null, ["ls-remote", "--heads", "--", form], default, settings);
        if (branches is not { ExitCode: 0 }) return null;
        if (branches.Output.Trim().Length > 0) return true;
        var refs = await RunAsync("git", Path.GetTempPath(), null, ["ls-remote", "--", form], default, settings);
        return refs is { ExitCode: 0 } ? refs.Output.Trim().Length > 0 : null;
    }

    /// <summary>Null if that couldn't be found out, such as a slow drive not answering in time.</summary>
    public static async Task<bool?> FolderHasNoCommitsAsync(string folder) =>
        await RunAsync("git", folder, "rev-parse", "--verify", "-q", "HEAD") switch
        {
            { ExitCode: 0 } => false,
            var answer when SaysNoCommits(answer) => true,
            _ => null,
        };

    public static Task<bool> FolderIsEmptyAsync(string folder) => Task.Run(() => Directory.Exists(folder) && !HasAnything(folder));

    /// <summary>Set up for Git, with no files or commits, as git init leaves a folder.</summary>
    public static async Task<bool> FolderHoldsOnlyGitAsync(string folder) =>
        await Task.Run(() =>
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(folder).All(entry => Path.GetFileName(entry) == ".git");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false; // A drive can drop while this is being asked.
            }
        })
        && await FolderHasNoCommitsAsync(folder) == true;

    public static Task<bool> FolderIsLinkAsync(string folder) => Task.Run(() =>
    {
        try
        {
            return new DirectoryInfo(folder).LinkTarget is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true; // Can't be vouched for, so it isn't treated as an ordinary folder.
        }
    });

    /// <summary>
    /// A folder can send its pushes somewhere other than this project's repo, for this branch or
    /// for all of them, including through a push address on origin itself. That's a normal way to
    /// work with a fork, so this is something to know, not a reason to refuse. Gives where they'd
    /// go, if that isn't this project's repo.
    /// </summary>
    public static async Task<string?> PushesElsewhereAsync(string folder, string repoUrl)
    {
        var branch = await RunAsync("git", folder, "symbolic-ref", "--short", "-q", "HEAD");
        var forBranch = branch is { ExitCode: 0 }
            ? await RunAsync("git", folder, "config", "--get", $"branch.{branch.Output.Trim()}.pushRemote")
            : null;
        var chosen = forBranch is { ExitCode: 0 } ? forBranch : await RunAsync("git", folder, "config", "--get", "remote.pushDefault");
        if (chosen is not { ExitCode: 0 } && branch is { ExitCode: 0 })
        {
            chosen = await RunAsync("git", folder, "config", "--get", $"branch.{branch.Output.Trim()}.remote"); // Git's last resort.
        }
        var remote = chosen is { ExitCode: 0 } && chosen.Output.Trim() is { Length: > 0 } named ? named : "origin";
        if (remote == ".") return null;

        return await PushAddressElsewhereAsync(folder, remote, repoUrl) is { } elsewhere
            ? remote == elsewhere ? elsewhere : $"{remote} ({elsewhere})"
            : null;
    }

    // Git pushes to every address a remote lists, so all of them count.
    private static async Task<string?> PushAddressElsewhereAsync(string folder, string remote, string repoUrl)
    {
        var addresses = await RunAsync("git", folder, "remote", "get-url", "--push", "--all", remote);
        if (addresses is not { ExitCode: 0 })
        {
            // It can be an address itself, which can be this project's own.
            var address = WithoutCredentials(remote);
            return remote == "origin" || SameRepo(address, repoUrl) ? null : address;
        }
        (string Current, bool Answered)? projectNow = null;
        foreach (var line in addresses.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var address = WithoutCredentials(line);
            var (gitHubForm, _) = await GitHubFormAsync(address, folder);
            var form = gitHubForm ?? address;
            if (RepoUrlProblem(form) is not null) return address;
            if (SameRepo(form, repoUrl)) continue;

            var (pushesNow, pushAnswered) = await RecentRepoUrlAsync(form);
            if (projectNow is null)
            {
                var projectName = await CurrentRepoUrlAsync(repoUrl);
                projectNow = (projectName.Current, projectName.Answered);
            }
            if (!(pushAnswered && projectNow.Value.Answered && SameRepo(pushesNow, projectNow.Value.Current))) return address;
        }
        return null;
    }

    /// <summary>A clone is prepared beside the folder it's going into, so it only has the real name once it's complete.</summary>
    public static async Task<bool> CanWorkBesideAsync(string folder)
    {
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is not { } parent) return false;

        var trial = Path.Combine(parent, $".ao-{Guid.NewGuid():N}"[..12]);
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(trial);
                Directory.Delete(trial);
            });
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Can't work beside a folder: {error.Message}");
            return false;
        }
    }
}
