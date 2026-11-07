using Microsoft.Data.Sqlite;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>Note is something that didn't go as well as the rest, worth saying even though it worked.</summary>
public sealed record GitResult(bool Worked, string Value, string? Note = null);

/// <summary>How a folder's commits compare with its repo's. A repo with none can't say.</summary>
public enum FolderHistory { CantTell, RepoIsEmpty, SharesCommits, SharesNothing }

public enum RepoAccess
{
    Allowed,
    NoGitHubCli,
    GitHubCliTooOld,
    NoGit,
    SignedOut,
    OtherAccount,
    ReadOnly,
    Archived,
    Restricted,
    NotFound,
    NotFoundOrNoAccess,
    GitCantReach,
    GitReadOnly,
    BadUrl,
    Offline,
    CheckFailed,
    GitPushUnchecked,
}

/// <summary>Account is the signed-in GitHub account this applies to, when there is one.</summary>
public sealed record RepoCheck(RepoAccess Access, string? Account = null)
{
    /// <summary>The check got a clear answer that this PC can't use the repo, as opposed to not getting one.</summary>
    public bool IsDenied => Access is RepoAccess.ReadOnly or RepoAccess.Archived or RepoAccess.Restricted or RepoAccess.NotFound or RepoAccess.NotFoundOrNoAccess or RepoAccess.GitReadOnly or RepoAccess.BadUrl;

    /// <summary>GitHub said this account can see the repo, so what it calls the repo now can be trusted.</summary>
    public bool GitHubSeesRepo => Access is RepoAccess.Allowed or RepoAccess.ReadOnly or RepoAccess.GitCantReach or RepoAccess.GitReadOnly or RepoAccess.GitPushUnchecked;

    /// <summary>Whether the page shows the folder at all, which is when it's worth looking through.</summary>
    public bool ShowsFolder => Access is RepoAccess.Allowed or RepoAccess.Offline or RepoAccess.CheckFailed or RepoAccess.GitPushUnchecked or RepoAccess.GitCantReach or RepoAccess.GitReadOnly;

    /// <summary>No clear answer, so a PC that has had access, or a project made here, can keep working.</summary>
    public bool IsUnanswered => Access is RepoAccess.Offline or RepoAccess.CheckFailed or RepoAccess.GitPushUnchecked;

    public (string Heading, string Description) Describe() => Access switch
    {
        RepoAccess.NoGitHubCli => ("The GitHub CLI is needed", "Agent Orange uses it to check your access. Install it from cli.github.com, then try again."),
        RepoAccess.GitHubCliTooOld => ("This PC's GitHub CLI is too old", "It can't say which accounts can see this repo. Update it from cli.github.com, then try again."),
        RepoAccess.NoGit => ("Git is needed", "Git wasn't found on this PC. Install it from git-scm.com, then try again."),
        RepoAccess.SignedOut => ("Sign in to GitHub", "This PC isn't signed in, so your access to this repo can't be checked."),
        RepoAccess.OtherAccount => ("Wrong GitHub account", $"Your active GitHub account can't work on this repo, but {Account} can."),
        RepoAccess.ReadOnly => ("You don't have access to this repo", $"{Account ?? "Your GitHub account"} can only read it, so you can't work on it. Get write access, or use a different repo."),
        RepoAccess.Archived => ("This repo is archived", "GitHub won't accept changes to it. Unarchive it on GitHub, or change the URL in project settings."),
        RepoAccess.Restricted => ("GitHub is blocking this login from the repo", $"An organization or token setting doesn't let {Account ?? "this login"} see it, so trying again won't help. Authorize that login for the organization on GitHub, or sign in again with gh auth refresh."),
        RepoAccess.NotFound => ("This repo doesn't exist", "It may have been deleted or renamed, or your GitHub login may not be allowed to see it."),
        RepoAccess.NotFoundOrNoAccess => ("Can't find this repo", "It was deleted, or your GitHub account doesn't have access. GitHub doesn't say which. If you should have access, ask the owner."),
        RepoAccess.GitCantReach => ("Git on this PC can't reach this repo", "Your GitHub account has access, but Git doesn't get through from here. Check this PC's Git setup, such as its SSH keys or a firewall, or run gh auth setup-git."),
        RepoAccess.GitReadOnly => ("Git on this PC can only read this repo", "Your GitHub account can work on it, but the key or login Git uses here can't push to it. Check this PC's Git setup, such as a read-only deploy key, or run gh auth setup-git."),
        RepoAccess.BadUrl => ("This repo URL can't be used", "It has to be a GitHub repo, with no password or token in the URL. Change it in project settings."),
        RepoAccess.Offline => ("Couldn't reach GitHub", "This PC seems to be offline or blocked from GitHub, and it hasn't had access to this repo before."),
        RepoAccess.GitPushUnchecked => ("Couldn't check pushing", "Your GitHub account has access, but Git on this PC couldn't finish checking whether it can push to this repo. Try again in a moment, and if this keeps happening, check this PC's Git setup, such as a proxy or firewall."),
        RepoAccess.Allowed => ("You have access", ""),
        _ => ("Couldn't check this repo", "GitHub gave an unexpected answer. Try again in a moment."),
    };
}

/// <summary>A folder's own Git settings that matter. The fingerprint says whether they've changed, without keeping them.</summary>
public sealed record FolderGitSettings(string Shown, string Fingerprint, bool CanBeApproved = true)
{
    public static readonly FolderGitSettings None = new("", "");

    public bool IsEmpty => Fingerprint.Length == 0;
}

/// <summary>What finishing a download in a folder would use, and why it can't be allowed if it can't.</summary>
public sealed record FinishLook(FolderGitSettings Settings, string? Refused);

/// <summary>What a chosen folder is, as far as Git is concerned.</summary>
public sealed record FolderRepo(bool InsideRepo, bool IsRepoRoot, string? Origin, string? Problem = null, string? GitHubOrigin = null)
{
    /// <summary>A folder inside someone else's repo would inherit that repo without really being it.</summary>
    public bool IsInsideAnotherRepo => InsideRepo && !IsRepoRoot;

    /// <summary>The remote, in its plain GitHub form if it has one, such as when it's an SSH alias for GitHub.</summary>
    public string? OriginToCompare => GitHubOrigin ?? Origin;

    /// <summary>The remote's SSH settings can't be understood until the person allows them to run.</summary>
    public bool NeedsSettingsApproval => Problem?.StartsWith(GitTools.CantTellSshSettings, StringComparison.Ordinal) == true;
}

public enum LeftoverClone { Gone, TryAgain, KeptFinished }

/// <summary>What projects need from Git and the GitHub CLI.</summary>
public static partial class GitTools
{
    private const string TimedOut = "timeout";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AfterExitLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WholeRepoTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(2);

    private sealed record CommandResult(int ExitCode, string Output, string Errors);

    /// <summary>
    /// Projects only live on GitHub, because the guard needs GitHub to say who has access.
    /// A repo URL also syncs to the server and other PCs, so it must not carry a password or token.
    /// </summary>
    public static string? RepoUrlProblem(string url)
    {
        if (GitHubScpRepo().IsMatch(url)) return null;

        const string notGitHub = "Enter a GitHub repo URL, like https://github.com/you/game.git.";
        if (url.Contains('\\') || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "ssh")
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !GitHubRepoPath().IsMatch(uri.AbsolutePath))
        {
            return notGitHub;
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return "Enter just the repo's URL, like https://github.com/you/game.git.";

        var hasCredentials = uri.Scheme == "ssh" ? uri.UserInfo.Contains(':') : uri.UserInfo.Length > 0;
        if (hasCredentials) return "Take the password or token out of that URL.";
        return uri.Scheme == "ssh" && uri.UserInfo != "git" ? notGitHub : null;
    }

    /// <summary>
    /// GitHub's SSH certificate form, as an organisation that requires it gives its members, says how
    /// to reach a repo on one PC. What's saved and synced is which repo, so it becomes that.
    /// </summary>
    public static string AsProjectUrl(string typed) =>
        CertificateAddress().Match(typed.Trim()) is { Success: true } certificate ? $"https://github.com/{certificate.Groups[1].Value}" : typed.Trim();

    /// <summary>
    /// Compares repos by host and path, so HTTPS and SSH forms of one repo match. A folder's
    /// remote can be anywhere, but a project's can't, so case only ever matters to a remote
    /// that wasn't going to match anyway.
    /// </summary>
    public static bool SameRepo(string a, string b) => RepoIdentity(a) == RepoIdentity(b);

    /// <summary>The same for every form of a repo's URL.</summary>
    public static string RepoKey(string url) => RepoIdentity(url);

    /// <summary>
    /// Whether this PC can work on a repo. GitHub answers "not found" both for a repo
    /// that's gone and for a private one you can't see, so a repo outside your own
    /// accounts can only be reported as one or the other. The URL says which repo this is,
    /// but not how this PC gets to it, so a folder that's connected to it is what gets tried,
    /// if its own Git settings are allowed to run. See FolderRunnableSettingsAsync.
    /// </summary>
    public static async Task<RepoCheck> CheckRepoAccessAsync(string url, string? folder = null, bool useFolderSettings = false)
    {
        // Synced URLs come from other PCs, so they're checked before anything runs them.
        if (RepoUrlProblem(url) is not null) return new RepoCheck(RepoAccess.BadUrl);

        var parts = RepoIdentity(url).Split('/');
        var slug = $"{parts[1]}/{parts[2]}";
        var view = await ViewRepoAsync(slug, null);
        if (view is null) return new RepoCheck(RepoAccess.NoGitHubCli);

        if (view.ExitCode == 0 && ArchivedRepo().IsMatch(view.Output)) return new RepoCheck(RepoAccess.Archived);
        if (view.ExitCode == 0 && WritePermission().IsMatch(view.Output)) return await CheckGitCanReachAsync(url, folder, useFolderSettings);

        var readOnly = view.ExitCode == 0;
        var status = readOnly ? null : await RepoStatusAsync(slug, null);
        var notFound = status == 404 || SaysRepoMissing(view.Errors);
        RepoCheck? activeAccountsAnswer = null;
        if (!readOnly && !notFound)
        {
            if (status is null && LooksLikeNetworkFailure(view.Errors)) return new RepoCheck(RepoAccess.Offline);
            if (MeansBlocked(status) || AccessBlocked().IsMatch(view.Errors)) activeAccountsAnswer = new RepoCheck(RepoAccess.Restricted, await ActiveAccountAsync());
            else if (status == 401 || SignedOutMessage().IsMatch(view.Errors)) activeAccountsAnswer = new RepoCheck(RepoAccess.SignedOut);
            else return new RepoCheck(RepoAccess.CheckFailed);
        }

        // Another signed-in account may be the one that can work on this repo.
        string? inconclusive = null;
        string? otherCanRead = null;
        string? otherIsBlocked = null;
        var accounts = await SignedInAccountsAsync();

        // An account just answered, so finding none means the list couldn't be read. Without that,
        // a repo that's gone can never be told apart from GitHub not giving a clear answer.
        if (accounts.Count == 0)
        {
            var active = await RunAsync("gh", Path.GetTempPath(), "auth", "status", "--active", "--hostname", "github.com");
            if (active?.Errors.Contains("unknown flag", StringComparison.OrdinalIgnoreCase) == true) return new RepoCheck(RepoAccess.GitHubCliTooOld);
            inconclusive = "";
        }
        foreach (var account in accounts)
        {
            var token = await RunAsync("gh", Path.GetTempPath(), "auth", "token", "--hostname", "github.com", "--user", account);
            if (token is not { ExitCode: 0 })
            {
                // Couldn't ask this account, so it can't be ruled out. One with no login left can.
                var why = token?.Errors ?? "";
                if (why.Contains("unknown flag", StringComparison.OrdinalIgnoreCase)) return new RepoCheck(RepoAccess.GitHubCliTooOld);
                if (!SignedOutMessage().IsMatch(why) && !why.Contains("no oauth token", StringComparison.OrdinalIgnoreCase)) inconclusive = why;
                continue;
            }
            var viewed = await ViewRepoAsync(slug, token.Output.Trim());
            if (viewed is { ExitCode: 0 } && WritePermission().IsMatch(viewed.Output)) return new RepoCheck(RepoAccess.OtherAccount, account);

            // If an account couldn't answer, nothing can be said about the repo being gone or
            // who can write to it. An account with an expired login just can't work on it.
            if (viewed is { ExitCode: 0 }) otherCanRead ??= account;
            var errors = viewed?.Errors ?? "";
            var accountStatus = viewed is { ExitCode: 0 } ? null : await RepoStatusAsync(slug, token.Output.Trim());
            var isBlocked = MeansBlocked(accountStatus) || AccessBlocked().IsMatch(errors);
            if (isBlocked) otherIsBlocked ??= account;
            var answered = viewed is { ExitCode: 0 } || isBlocked || accountStatus is 404 or 401 || SaysRepoMissing(errors) || SignedOutMessage().IsMatch(errors);
            if (!answered) inconclusive = errors;
        }

        // No other account can work on it, so this is what there is to say about the active one.
        if (activeAccountsAnswer is not null) return activeAccountsAnswer;

        // GitHub clearly said read-only, and no other account has shown it can do better.
        if (readOnly) return new RepoCheck(RepoAccess.ReadOnly, await ActiveAccountAsync());

        // The active account was told it doesn't exist, but another one can see it. So it does
        // exist, and offering to replace it would move the project off a real repo.
        if (otherCanRead is not null) return new RepoCheck(RepoAccess.ReadOnly, otherCanRead);

        // GitHub only blocks a login from something that's there, so this one exists too.
        if (otherIsBlocked is not null) return new RepoCheck(RepoAccess.Restricted, otherIsBlocked);
        if (inconclusive is not null) return new RepoCheck(LooksLikeNetworkFailure(inconclusive) ? RepoAccess.Offline : RepoAccess.CheckFailed);

        var ownerIsYours = accounts.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
        return new RepoCheck(ownerIsYours ? RepoAccess.NotFound : RepoAccess.NotFoundOrNoAccess, await ActiveAccountAsync());
    }

    public static async Task<bool> SwitchAccountAsync(string account) =>
        await RunAsync("gh", Path.GetTempPath(), "auth", "switch", "--hostname", "github.com", "--user", account) is { ExitCode: 0 };

    /// <summary>Signing in is interactive, so it gets a terminal of its own.</summary>
    public static void OpenGitHubSignIn()
    {
#if DEBUG
        if (Headless.Active) return; // It would open a terminal on someone's screen.
#endif
        var info = new ProcessStartInfo(CommandShell, "/d /k gh auth login")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        info.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        info.Environment["PATH"] = CurrentPath();
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"Couldn't open the GitHub sign-in: {error.Message}");
        }
    }

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
            return new FolderRepo(true, true, remote, aliasProblem, gitHubForm);
        }

        // Git documents exit code 2 for a remote that doesn't exist.
        return origin.ExitCode == 2
            ? new FolderRepo(true, true, null)
            : new FolderRepo(true, true, null, GitProblem("Git couldn't read that folder", origin.Errors));
    }

    /// <summary>Checks a folder really holds this project's repo. Returns a problem to show, or null if it does.</summary>
    public static async Task<string?> FolderProblemAsync(string folder, string repoUrl)
    {
        if (!await FolderExistsAsync(folder)) return "That folder doesn't exist.";
        if (repoUrl.Length == 0) return null;

        var state = await InspectFolderAsync(folder);
        if (state is null) return "Git wasn't found on this PC.";
        if (state.Problem is not null) return state.Problem;
        if (state.IsInsideAnotherRepo) return "That folder is inside a repo. Choose the repo's top folder.";
        if (state.Origin is null) return "That folder isn't connected to a repo, so it can't be this project.";

        // The same owner and name somewhere that isn't really GitHub, or in a way Git won't use.
        if (RepoUrlProblem(state.OriginToCompare!) is not null) return $"That folder uses a different repo address, which isn't one GitHub uses: {state.Origin}";

        if (!SameRepo(state.OriginToCompare!, repoUrl))
        {
            var (renamed, moveProblem, originAnswered, originNow) = await MoveIfRepoRenamedAsync(folder, state, repoUrl);
            if (moveProblem is not null) return moveProblem;
            if (renamed)
            {
                state = await InspectFolderAsync(folder);
                if (state?.Problem is { } unreadable) return unreadable;
                if (state?.Origin is null) return "That folder isn't connected to a repo, so it can't be this project.";
            }

            // The project can be on an old name too, and it moves on when it's next opened, and the folder with it.
            else
            {
                var projectRepo = await CurrentRepoUrlAsync(repoUrl);
                if (!SameRepo(projectRepo.Current, state.OriginToCompare!) && !(projectRepo.Answered && originAnswered && SameRepo(projectRepo.Current, originNow)))
                {
                    // Only certain if this repo was really found. GitHub would then have led the folder's name to it, if it were one.
                    if (projectRepo.Found && originAnswered) return $"That folder uses a different repo: {state.Origin}";
                    return projectRepo.Answered
                        ? $"{MayUseDifferentRepo}{state.Origin}. GitHub can't find this project's repo, so it can't be told whether that folder is it under another name."
                        : $"{MayUseDifferentRepo}{state.Origin}. {NoAnswerAboutOlderName}";
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
        var problem = await MoveOriginAsync(folder, SameWayTo(state.Origin, repoUrl), state.OriginToCompare!);
        return (problem is null, problem, true, current);
    }

    /// <summary>Said when a folder may be on an old name of the repo, and GitHub didn't say.</summary>
    public const string NoAnswerAboutOlderName = "GitHub didn't say, so it can't be checked whether that's this repo under an older name.";

    /// <summary>
    /// Settings in a folder that make Git run a program or send a login somewhere else when it
    /// reaches a remote. A folder can come from anyone, as a zip or on a shared drive, so these
    /// are only used once the person has seen them. Null means that couldn't be found out,
    /// which has to be treated as not being safe to use them.
    /// </summary>
    public static Task<FolderGitSettings?> FolderRunnableSettingsAsync(string folder) =>
        ReadFolderSettingsAsync(folder, FolderSettingsThatMatter, null);

    /// <summary>
    /// Finishing a download checks out files and works inside each submodule, using that one's own
    /// settings and hooks, and there are too many ways for those to run a program to list them all.
    /// So anything that isn't known to be safe needs allowing, including what a settings file
    /// includes. What a clone, Git LFS and submodules set up for themselves doesn't come up. Refused
    /// says it can't be allowed at all, and null means this couldn't be read.
    /// </summary>
    public static async Task<FinishLook?> LookBeforeFinishingAsync(string folder)
    {
        // What's remembered goes by path, and something else can have appeared there since.
        switch (await HoldsNoteFromHereAsync(folder))
        {
            case false:
                TryNoting(() => RememberUnfinishedDownload(folder, false));
                return new FinishLook(FolderGitSettings.None, "This app can't confirm this is the copy it was downloading, so nothing was run in it. If its large files or submodules are missing and you trust it, run git lfs pull and git submodule update --init --recursive in it yourself.");
            case null:
                // It may just be on a drive that's unplugged, and it's still unfinished when that's back.
                return new FinishLook(FolderGitSettings.None, "That folder can't be read right now, so nothing was run in it. Finish the download once it's available again.");
        }

        var own = await ReadSettingLinesAsync(folder, ".", setting => SafeWhileFinishing().IsMatch(setting));
        var inSubmodules = await LookThroughSubmodulesAsync(folder);
        if (own is null || inSubmodules is null) return null;
        return new FinishLook(SummedUp([.. own, .. inSubmodules.Lines]), inSubmodules.Refused);
    }

    private const int MaxSubmoduleDepth = 8;
    private const int MaxHookBytes = 1_000_000;

    private sealed record SubmoduleLook(List<string> Lines, string? Refused);

    private static async Task<SubmoduleLook?> LookThroughSubmodulesAsync(string folder)
    {
        var lines = new List<string>();
        try
        {
            var gitDirectory = Path.Combine(folder, ".git");
            if (await Task.Run(() => File.Exists(gitDirectory))) return new SubmoduleLook(lines, "This folder keeps its Git data somewhere else, which a clone from this app never does, so what it would use can't be checked and nothing was run in it. If you trust it, run git submodule update --init --recursive and git lfs pull in the folder yourself.");
            if (!await Task.Run(() => Directory.Exists(gitDirectory))) return new SubmoduleLook(lines, null);
            if (await Task.Run(() => File.Exists(Path.Combine(gitDirectory, "commondir")))) return new SubmoduleLook(lines, CantBeUsed("This folder's Git data", "it has a commondir file, which keeps its hooks and settings somewhere else"));
            lines.AddRange(await Task.Run(() => HooksIn(gitDirectory, ".git")));
            var places = new HashSet<string>(await Task.Run(() => GitFolderSafety.ModuleDirectories(gitDirectory)), StringComparer.OrdinalIgnoreCase);

            // A submodule's folder can also keep its Git data itself, or say it's somewhere else.
            var waiting = new Stack<(string Folder, int Depth)>();
            waiting.Push((folder, 0));
            while (waiting.TryPop(out var worktree))
            {
                if (await IsLinkInFolderAsync(worktree.Folder, ".gitmodules")) return new SubmoduleLook(lines, CantBeUsed(".gitmodules", "it's a link"));
                if (await DeclaredSubmodulePathsAsync(worktree.Folder) is not { } paths) return null;
                foreach (var path in paths)
                {
                    var (problem, keeps, carriesOn) = await Task.Run(() => WhereSubmoduleKeepsData(folder, gitDirectory, worktree.Folder, path));
                    if (problem is not null) return new SubmoduleLook(lines, CantBeUsed($"The submodule {OneLine(path)}", problem));
                    if (keeps is not null) places.Add(keeps);
                    if (carriesOn is null) continue;
                    if (worktree.Depth >= MaxSubmoduleDepth) return new SubmoduleLook(lines, CantBeUsed($"The submodule {OneLine(path)}", "submodules go too many levels deep to check"));
                    waiting.Push((carriesOn, worktree.Depth + 1));
                }
            }

            foreach (var place in places)
            {
                if (await GitFolderSafety.SubmoduleDataProblemAsync(folder, place, ReadSettingsFileAsync) is { } refused) return new SubmoduleLook(lines, refused);

                var name = Path.GetRelativePath(folder, place);
                if (await Task.Run(() => File.Exists(Path.Combine(place, "commondir")))) return new SubmoduleLook(lines, CantBeUsed($"The Git data at {name}", "it has a commondir file, which keeps its hooks and settings somewhere else"));
                foreach (var file in new[] { "config", "config.worktree" })
                {
                    var settingsFile = Path.Combine(place, file);
                    if (!await Task.Run(() => File.Exists(settingsFile))) continue;
                    if (await ReadSettingsFileAsync(settingsFile) is not { } settings) return null;
                    if (settings.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(setting => SubmoduleInclude().IsMatch(setting.Split('\n', 2)[0])))
                    {
                        return new SubmoduleLook(lines, CantBeUsed($"The Git data at {name}", "its settings include another file, which could change after being allowed"));
                    }
                    lines.AddRange(settings.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(setting => !SafeWhileFinishing().IsMatch(setting) && !IsSubmodulesOwnWorktree(setting, place, folder)).Select(setting => $"{name}: {setting}"));
                }
                lines.AddRange(await Task.Run(() => HooksIn(place, name)));
            }
            return new SubmoduleLook(lines, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write($"Couldn't read a submodule's settings: {error.Message}");
            return null;
        }
    }

    private static string CantBeUsed(string what, string why) => $"{what} can't be used, so nothing was run in this folder: {why}. Git would follow it, so this can't be allowed. Fix that, or use another copy.";

    // What Git sets up for a submodule, which is safe as long as it stays inside the project.
    private static bool IsSubmodulesOwnWorktree(string setting, string place, string folder)
    {
        var parts = setting.Split('\n', 2);
        return parts.Length == 2 && parts[0].Equals("core.worktree", StringComparison.OrdinalIgnoreCase)
            && Path.GetFullPath(parts[1], place).StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Both copies, as Git falls back on the committed one if the one in the folder is gone.
    private static async Task<List<string>?> DeclaredSubmodulePathsAsync(string worktree)
    {
        var paths = new List<string>();
        var declared = Path.Combine(worktree, ".gitmodules");
        if (await Task.Run(() => File.Exists(declared) && !SshConfigFile.IsLink(declared)))
        {
            if (await ReadSettingsFileAsync(declared) is not { } inFolder) return null;
            paths.AddRange(SubmodulePathsIn(inFolder));
        }

        var committed = await RunAsync("git", worktree, "config", "--blob", "HEAD:.gitmodules", "-z", "--list");
        if (committed is null or { ExitCode: < 0 }) return null;
        if (committed.ExitCode == 0) paths.AddRange(SubmodulePathsIn(committed.Output));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Task<bool> IsLinkInFolderAsync(string folder, string name) => Task.Run(() =>
    {
        try
        {
            return SshConfigFile.IsLink(Path.Combine(folder, name));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    });

    private static IEnumerable<string> SubmodulePathsIn(string settings) =>
        settings.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('\n', 2)).Where(parts => parts.Length == 2 && SubmodulePathSetting().IsMatch(parts[0])).Select(parts => parts[1]);

    // Nothing here gets followed. A link on the way to one could lead onto a network share.
    private static (string? Problem, string? Keeps, string? CarriesOn) WhereSubmoduleKeepsData(string folder, string gitDirectory, string worktree, string path)
    {
        var submodule = Path.GetFullPath(Path.Combine(worktree, path));
        if (!submodule.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return ("is outside this folder", null, null);
        if (SshConfigFile.HasLinkBelowChosenPath(folder, submodule)) return ("is reached through a link", null, null);

        var entry = Path.Combine(submodule, ".git");
        if (!File.Exists(entry) && !Directory.Exists(entry)) return (null, null, null);
        if (SshConfigFile.IsLink(entry)) return ("its .git is a link", null, null);
        if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) return (null, entry, submodule);

        if (new FileInfo(entry).Length > MaxHookBytes) return ("its .git file is too big to check", null, null);
        var line = File.ReadLines(entry).FirstOrDefault(text => text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return (null, null, submodule);
        var target = line["gitdir:".Length..].Trim();
        var resolved = Path.GetFullPath(target, submodule);
        return resolved.StartsWith(Path.Combine(gitDirectory, "modules") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? (null, null, submodule) // One of the ones already being looked at.
            : ($"keeps its Git data at {target}", null, submodule);
    }

    // Anything that isn't one of Git's samples, or a hook Git LFS added for itself.
    private static List<string> HooksIn(string place, string name)
    {
        var found = new List<string>();
        var hooks = new DirectoryInfo(Path.Combine(place, "hooks"));
        if (!hooks.Exists) return found;
        foreach (var hook in hooks.EnumerateFiles().Where(file => !file.Name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)))
        {
            if (hook.Length <= MaxHookBytes && StandardLfsHook().IsMatch(File.ReadAllText(hook.FullName))) continue;

            // Read as it goes, as one could be any size, and approving it covers everything in it.
            using var contents = hook.OpenRead();
            found.Add($"{name}: the hook {hook.Name} ({Convert.ToHexString(SHA256.HashData(contents))[..12]})");
        }
        return found;
    }

    public static string FinishApprovalKey(string folder) => $"{Path.TrimEndingDirectorySeparator(folder)}|finish";

    private static async Task<FolderGitSettings?> ReadFolderSettingsAsync(string folder, string wanted, Func<string, bool>? harmless) =>
        await ReadSettingLinesAsync(folder, wanted, harmless) is { } settings ? SummedUp(settings) : null;

    private static async Task<List<string>?> ReadSettingLinesAsync(string folder, string wanted, Func<string, bool>? harmless)
    {
        if (!await FolderExistsAsync(folder)) return [];

        // Asked for by scope, because a folder's settings can also be in a file only used per
        // worktree, or in one it includes. NUL-separated, so a value can't hide on a second line.
        var found = await RunAsync("git", folder, "config", "--show-scope", "--includes", "-z", "--get-regexp", wanted);
        if (found is null) return null;
        if (found.ExitCode == 1) return [];
        if (found.ExitCode != 0) return null;

        var parts = found.Output.Split('\0');
        var settings = new List<string>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (parts[i] is "local" or "worktree" && harmless?.Invoke(parts[i + 1]) != true) settings.Add(parts[i + 1]);
        }
        return settings;
    }

    private static FolderGitSettings SummedUp(List<string> settings)
    {
        if (settings.Count == 0) return FolderGitSettings.None;

        // Only a fingerprint is kept, because these can hold live logins.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', settings))));

        // Everything approved has to have been read, so what can't all be shown can't be approved.
        var shown = settings.Select(ForShowing).ToList();
        if (shown.Count > MaxSettingsShown || shown.Any(line => line.Length > MaxSettingShown)) return new FolderGitSettings("", fingerprint, false);
        return new FolderGitSettings(string.Join('\n', shown), fingerprint);
    }

    // Enough to judge what a setting does, without putting a login on the screen. A value can't
    // fake extra lines in the prompt.
    private static string ForShowing(string setting)
    {
        var parts = setting.Split('\n', 2);
        var key = OneLine(HideLogins(parts[0]));
        if (parts.Length == 1) return key;
        if (SensitiveSetting().IsMatch(parts[0])) return $"{key} (hidden)";

        return $"{key} {OneLine(HideLogins(parts[1]))}";
    }

    public static string OneLine(string text) => WhiteSpaceRun().Replace(text, " ").Trim();

    public static string HideLogins(string text) =>
        TokenLike().Replace(LoginInUrl().Replace(text, "://(hidden)@"), "(hidden)");

    /// <summary>Set up when the app starts, because the answers are kept in its database.</summary>
    public static Func<string, string?> AllowedFolderSettings { get; set; } = _ => null;

    public static Action<string, string> RememberAllowedFolderSettings { get; set; } = (_, _) => { };

    /// <summary>Set up when the app starts, because this has to outlast the app closing mid-download.</summary>
    public static Action<string, bool> RememberUnfinishedDownload { get; set; } = (_, _) => { };

    /// <summary>Set up when the app starts. Gives the name of a project already on this repo.</summary>
    public static Func<string, string?> ProjectUsingRepo { get; set; } = _ => null;

    public static Func<IReadOnlyList<Project>> AllProjects { get; set; } = () => [];

    private const int RepoNamesAskedAtOnce = 4;

    /// <summary>
    /// Which project has this repo, if any, and the name GitHub uses for it now. A project that
    /// hasn't been opened since a rename still has an old name, and only GitHub can say that's
    /// the same repo. It stops asking at the first sign of no connection, so this can't hold up a
    /// save for as long as there are projects, and then only goes by what it knows.
    /// </summary>
    public static async Task<(string Current, string? Holder, bool UnderAnotherName)> WhoHasRepoAsync(string url, string? exceptProjectId = null)
    {
        var others = AllProjects().Where(project => project.Id != exceptProjectId).ToList();
        if (others.FirstOrDefault(project => SameRepo(project.RepoUrl, url)) is { } exact) return (url, exact.Name, false);

        var asked = await CurrentRepoUrlAsync(url);
        var (current, answered) = (asked.Current, asked.Answered);
        if (others.FirstOrDefault(project => SameRepo(project.RepoUrl, current)) is { } holder) return (current, holder.Name, true);
        if (!answered) return (current, null, false);

        foreach (var group in others.Chunk(RepoNamesAskedAtOnce))
        {
            var answers = await Task.WhenAll(group.Select(project => RecentRepoUrlAsync(project.RepoUrl)));
            if (group.Zip(answers).FirstOrDefault(pair => SameRepo(pair.Second.Current, current)).First is { } older) return (current, older.Name, true);
            if (answers.Any(answer => !answer.Answered)) break;
        }
        return (current, null, false);
    }

    public static bool AreAllowed(string folder, FolderGitSettings settings) =>
        settings.IsEmpty || (settings.CanBeApproved && AllowedFolderSettings(folder) == settings.Fingerprint);

    /// <summary>
    /// A folder whose SSH settings file needs ssh's own handling can't be understood until it's
    /// allowed to run, and until then it would just be refused. So this asks, when choosing one.
    /// Says whether it was allowed just now, as that shouldn't outlast the folder being refused.
    /// </summary>
    public static async Task<bool> AskAboutUnreadableSshSettingsAsync(string folder)
    {
        if (await InspectFolderAsync(folder) is not { NeedsSettingsApproval: true }) return false;
        if (await FolderRunnableSettingsAsync(folder) is not { IsEmpty: false, CanBeApproved: true } settings) return false;

        // A message box can't scroll, and what's approved has to have been read. Not being asked
        // leaves this saying it can't tell, so the folder's remote has to say github.com instead.
        if (settings.Shown.Length > MaxSettingsInDialog) return false;

        if (Dialogs.Confirm($"To tell whether this folder's remote is on GitHub, its SSH settings have to run, and they can run programs or send your login somewhere else:\n\n{settings.Shown}\n\nOnly use them if you trust where this folder came from. Use them?"))
        {
            RememberAllowedFolderSettings(folder, settings.Fingerprint);
            return true;
        }
        return false;
    }

    public static void ForgetAllowedFolderSettings(string folder) => RememberAllowedFolderSettings(folder, "");

    internal const string CantTellSshSettings = "Couldn't tell whether";
    private const int MaxSettingsShown = 20;
    private const int MaxSettingShown = 600;
    private const int MaxSettingsInDialog = 1000;

    private const string FolderSettingsThatMatter =
        @"^(core\.(sshcommand|askpass|gitproxy)|credential\..*|http\..*|url\..*|remote\..*\.(uploadpack|receivepack|vcs|proxy))$";

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

    /// <summary>
    /// The form of a repo's URL that works from this PC, HTTPS or SSH. The synced URL may have
    /// come from a PC set up differently. Null if neither works.
    /// </summary>
    public static async Task<string?> ReachableFormAsync(string url, CancellationToken cancel = default) => (await BestFormAsync(url, cancel)).Form;

    // One that can push, or else one that can at least read, so what's chosen here passes the
    // guard's check of the same thing.
    private static async Task<(string? Form, bool CanPush)> BestFormAsync(string url, CancellationToken cancel = default)
    {
        string? readable = null;
        string? unsure = null;
        foreach (var form in await FormsToTryAsync(url))
        {
            if (await LsRemoteAsync(form, Path.GetTempPath(), cancel) is not { ExitCode: 0 }) continue;
            switch (await GitPushAnswerAsync(form, null, cancel))
            {
                case PushAnswer.CanPush:
                    return (form, true);
                case PushAnswer.CantTell:
                    unsure ??= form;
                    break;
            }
            readable ??= form;
        }

        // Only a way to connect, which proves nothing about access, so one that wasn't refused will do.
        return unsure is not null ? (unsure, true) : (readable, false);
    }

    // The one this PC's GitHub CLI is set to comes first. Anyone can read a public repo over
    // HTTPS, so trying that first could choose a form this PC can't push with.
    private static async Task<string[]> FormsToTryAsync(string url)
    {
        var https = $"https://github.com/{GitHubPath(url)}.git";
        var ssh = $"git@github.com:{GitHubPath(url)}.git";
        var protocol = await RunAsync("gh", Path.GetTempPath(), "config", "get", "git_protocol", "--host", "github.com");
        return protocol is { ExitCode: 0 } && protocol.Output.Trim() == "ssh" ? [ssh, https] : [https, ssh];
    }

    private static string GitHubPath(string url)
    {
        var text = url.Trim().TrimEnd('/');
        text = IsScpStyle(text)
            ? text[(text.IndexOf(':') + 1)..]
            : Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.AbsolutePath.Trim('/') : text;
        return text.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
    }

    // This PC's remote for another repo, reached the same way. An alias usually exists to
    // choose an SSH key, so losing it could lose access.
    private static string SameWayTo(string remote, string url)
    {
        var path = GitHubPath(url);
        if (IsScpStyle(remote)) return $"{remote[..remote.IndexOf(':')]}:{path}.git";
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme == "ssh")
        {
            var user = uri.UserInfo.Length > 0 ? $"{uri.UserInfo}@" : "";
            return $"ssh://{user}{uri.Authority}/{path}.git";
        }
        return $"https://github.com/{path}.git";
    }

    // Git's SSH form doesn't have to say github.com. A host can be an alias in the person's
    // SSH settings, which is common with more than one GitHub account.
    private static async Task<(string? GitHubForm, string? Problem)> GitHubFormAsync(string remote, string folder)
    {
        if (RepoUrlProblem(remote) is null) return (remote, null);

        // The username can be left out, and come from the person's SSH settings instead.
        string host, user, path;
        if (ScpStyleRepo().IsMatch(remote))
        {
            var at = remote.IndexOf('@');
            var colon = remote.IndexOf(':');
            (user, host, path) = (remote[..at], remote[(at + 1)..colon], remote[(colon + 1)..]);
        }
        else if (BareScpRepo().IsMatch(remote))
        {
            var colon = remote.IndexOf(':');
            (user, host, path) = ("", remote[..colon], remote[(colon + 1)..]);
        }
        else if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme == "ssh")
        {
            (user, host, path) = (uri.UserInfo, uri.Host, uri.AbsolutePath.TrimStart('/'));
        }
        else
        {
            return (null, null);
        }

        if ((user.Length > 0 && !IsGitHubSshUser(user)) || !GitHubRepoPath().IsMatch($"/{path}") || !SafeSshHost().IsMatch(host)) return (null, null);
        if (IsGitHubSshUser(user) && IsGitHubSshHost(host)) return ($"git@github.com:{path}", null);

        var settings = await ReadSshSettingsAsync(host, folder);
        if (settings is not var (realHost, realUser)) return (null, $"{CantTellSshSettings} {host} is GitHub from the SSH settings in use. Check that they work, or use a remote that says github.com.");

        var effectiveUser = user.Length > 0 ? user : realUser;
        return (effectiveUser is not null && IsGitHubSshUser(effectiveUser) && realHost is not null && IsGitHubSshHost(realHost) ? $"git@github.com:{path}" : null, null);
    }

    // How GitHub's SSH certificates name the person, for organisations that require them. Moving a
    // remote keeps whichever it was.
    private static bool IsGitHubSshUser(string user) => user == "git" || CertificateUser().IsMatch(user);

    private static bool IsScpStyle(string remote) => ScpStyleRepo().IsMatch(remote) || BareScpRepo().IsMatch(remote);

    private static bool IsGitHubSshHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.Equals("ssh.github.com", StringComparison.OrdinalIgnoreCase);

    // Null means there's no way to tell. A folder's own SSH command can name its own settings
    // file, which is read here and never given to ssh, because ssh would run commands in it.
    private static async Task<(string? Host, string? User)?> ReadSshSettingsAsync(string host, string folder)
    {
        // Settings from the person's own Git setup are theirs. Only the folder's need approving.
        var command = Environment.GetEnvironmentVariable("GIT_SSH_COMMAND");
        var fromFolder = false;
        if (string.IsNullOrEmpty(command))
        {
            var configured = await RunAsync("git", folder, "config", "--show-scope", "--get", "core.sshCommand");
            var parts = configured is { ExitCode: 0 } ? configured.Output.Trim().Split('\t', 2) : [];
            command = parts.Length == 2 ? parts[1] : "";
            fromFolder = parts.Length == 2 && parts[0] is "local" or "worktree";
        }

        var file = SshConfigOption().Match(command);
        if (file.Success)
        {
            var path = SshConfigFile.Resolve(file.Groups[1].Value.Trim('"', '\''), folder);
            if (await Task.Run(() => SshConfigFile.Read(path, host)) is { } read) return read;

            // Needs ssh's own handling, which can run commands, so a folder's only once it's been allowed.
            if (fromFolder && (await FolderRunnableSettingsAsync(folder) is not { IsEmpty: false } allowed || !AreAllowed(folder, allowed))) return null;
            return ParseSshSettings(await RunAsync("ssh", folder, "-G", "-F", path, host));
        }

        // This PC's own SSH settings, which belong to the person and not to the folder.
        return ParseSshSettings(await RunAsync("ssh", Path.GetTempPath(), "-G", host));
    }

    private static (string? Host, string? User)? ParseSshSettings(CommandResult? settings)
    {
        if (settings is not { ExitCode: 0 }) return null;

        var name = SshHostName().Match(settings.Output);
        var user = SshUser().Match(settings.Output);
        return (name.Success ? name.Groups[1].Value.Trim() : null, user.Success ? user.Groups[1].Value.Trim() : null);
    }

    /// <summary>Points a folder at a repo after the project moved, whatever remote it had. Its files don't change.</summary>
    public static async Task<GitResult> PointFolderAtRepoAsync(string folder, string repoUrl, string? onlyIfOnRepo = null)
    {
        if (RepoUrlProblem(repoUrl) is { } problem) return new GitResult(false, problem);

        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");
        if (state.Origin is null && onlyIfOnRepo is not null) return new GitResult(false, RemoteChangedMeanwhile);
        if (state.Origin is null) return await ConnectFolderAsync(folder, await ReachableFormAsync(repoUrl) ?? repoUrl);

        var newRemote = state.GitHubOrigin is not null ? SameWayTo(state.Origin, repoUrl) : await ReachableFormAsync(repoUrl) ?? repoUrl;
        return await MoveOriginAsync(folder, newRemote, onlyIfOnRepo ?? state.OriginToCompare!) is { } moveProblem
            ? new GitResult(false, moveProblem)
            : new GitResult(true, repoUrl);
    }

    // A push address on the old repo would otherwise keep sending work there, so that moves too.
    // One that goes anywhere else is a fork, which someone set up on purpose, and it stays. Returns
    // a problem to show, or null once it has moved. Nothing is left half done either way.
    private static async Task<string?> MoveOriginAsync(string folder, string remote, string onlyIfOnRepo)
    {
        var turn = RemoteMoves.GetOrAdd(Path.TrimEndingDirectorySeparator(folder).ToLowerInvariant(), _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync();
        try
        {
            // Other windows too. A semaphore, as this is held across steps that can change threads.
            using var windows = RemoteMoveTurn(folder);
            if (windows is null || !await Task.Run(() => windows.WaitOne(LongestRemoteMoveWait))) return "Another window of the app is changing that folder's remote. Try again in a moment.";
            try
            {
                return await MoveOriginInTurnAsync(folder, remote, onlyIfOnRepo);
            }
            finally
            {
                windows.Release();
            }
        }
        finally
        {
            turn.Release();
        }
    }

    private const string RemoteChangedMeanwhile = "That folder's remote changed while this was being checked, so it was left as it is.";
    private static readonly TimeSpan LongestRemoteMoveWait = TimeSpan.FromSeconds(10);

    private static Semaphore? RemoteMoveTurn(string folder)
    {
        try
        {
            return new Semaphore(1, 1, $@"Local\AgentOrangeRemote-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(folder).ToLowerInvariant())))[..32]}");
        }
        catch (UnauthorizedAccessException)
        {
            return null; // A window running as administrator is taking its turn.
        }
    }

    // An open page, a background engine read and a check can all notice one rename at once.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> RemoteMoves = new();

    private static async Task<string?> MoveOriginInTurnAsync(string folder, string remote, string onlyIfOnRepo)
    {
        if (await RemoteSetElsewhereAsync(folder) is { } elsewhere) return elsewhere;

        const string couldNotChange = "Couldn't change that folder's remote.";

        // A remote can list several addresses, and the first is the one Git fetches from.
        var urls = await AddressesInAsync(folder, "remote.origin.url");
        var pushUrls = await AddressesInAsync(folder, "remote.origin.pushurl");
        if (urls is null || pushUrls is null) return couldNotChange;
        if (urls.Length == 0) return RemoteChangedMeanwhile;

        // An SSH alias, as a second GitHub account uses, has to be read as the repo it really is.
        async Task<string> RepoOfAsync(string address)
        {
            var expanded = await RunAsync("git", folder, "ls-remote", "--get-url", "--", address);
            var full = expanded is { ExitCode: 0 } && expanded.Output.Trim().Length > 0 ? expanded.Output.Trim() : address;
            return (await GitHubFormAsync(full, folder)).Item1 ?? full;
        }

        var oldRepo = await RepoOfAsync(urls[0]);
        if (!SameRepo(oldRepo, onlyIfOnRepo)) return RemoteChangedMeanwhile;
        async Task<string[]> MovedAsync(string[] addresses)
        {
            var moved = new List<string>();
            foreach (var address in addresses)
            {
                moved.Add(SameRepo(await RepoOfAsync(address), oldRepo) ? remote : address);
            }
            return moved.Distinct().ToArray();
        }

        var movedUrls = await MovedAsync(urls);
        var movedPushUrls = await MovedAsync(pushUrls);
        if (movedUrls.SequenceEqual(urls) && movedPushUrls.SequenceEqual(pushUrls)) return null; // Already there.

        var urlsMoved = await SetAddressesAsync(folder, "remote.origin.url", movedUrls);
        if (urlsMoved && (movedPushUrls.Length == 0 || await SetAddressesAsync(folder, "remote.origin.pushurl", movedPushUrls))) return null;

        // Another window can be moving this too, and Git's lock turned this one away. What's there
        // now is then that window's, so only what this attempt wrote gets put back.
        var now = await AddressesInAsync(folder, "remote.origin.url");
        var wroteHalf = now is not null && !now.SequenceEqual(urls) && !now.SequenceEqual(movedUrls);
        if (urlsMoved || wroteHalf) await SetAddressesAsync(folder, "remote.origin.url", urls);
        if (urlsMoved && pushUrls.Length > 0) await SetAddressesAsync(folder, "remote.origin.pushurl", pushUrls);
        return couldNotChange;
    }

    // Null if that couldn't be read. A setting that isn't there is an ordinary answer, and an empty list.
    private static async Task<string[]?> AddressesInAsync(string folder, string key)
    {
        var read = await RunAsync("git", folder, "config", "--get-all", key);
        return read switch
        {
            { ExitCode: 0 } => read.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            { ExitCode: 1 } => [],
            _ => null,
        };
    }

    private static async Task<bool> SetAddressesAsync(string folder, string key, string[] addresses)
    {
        if (await RunAsync("git", folder, "config", "--replace-all", key, addresses[0]) is not { ExitCode: 0 }) return false;
        foreach (var address in addresses.Skip(1))
        {
            if (await RunAsync("git", folder, "config", "--add", key, address) is not { ExitCode: 0 }) return false;
        }
        return true;
    }

    // Only a folder's own settings file is changed here, because one it includes may be shared.
    // An address set anywhere else would stay in use, so that's found out before changing anything.
    private static async Task<string?> RemoteSetElsewhereAsync(string folder)
    {
        foreach (var key in new[] { "remote.origin.url", "remote.origin.pushurl" })
        {
            var everywhere = await RunAsync("git", folder, "config", "--get-all", key);
            var own = await RunAsync("git", folder, "config", "--local", "--no-includes", "--get-all", key);
            if (everywhere is null || own is null) return "Git wasn't found on this PC.";

            static int Count(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            if (Count(everywhere.Output) != Count(own.Output))
            {
                return "That folder's remote is also set in another Git settings file, which would keep it on the old address. Nothing was changed. Remove it from that file, then try again.";
            }
        }
        return null;
    }

    private static bool SameForm(string a, string b)
    {
        static string Plain(string url)
        {
            var text = url.Trim().TrimEnd('/');
            return text.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
        }
        return string.Equals(Plain(a), Plain(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Changes a folder's remote to whichever of HTTPS and SSH works from this PC.</summary>
    public static async Task<GitResult> RepairRemoteAsync(string folder, string repoUrl)
    {
        if (RepoUrlProblem(repoUrl) is { } problem) return new GitResult(false, problem);

        // Only ever a different way to the same repo. Moving to another one is what Connect it is for.
        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo, so nothing was changed.");
        if (state.Origin is not null && !SameRepo(state.OriginToCompare!, repoUrl))
        {
            return new GitResult(false, $"That folder uses a different repo: {state.Origin}. Nothing was changed.");
        }

        var (reachable, canPush) = await BestFormAsync(repoUrl);
        if (reachable is not { } form)
        {
            return new GitResult(false, "Neither HTTPS nor SSH gets through from this PC, so nothing was changed.");
        }
        if (!canPush)
        {
            return new GitResult(false, "Git on this PC can read this repo, but can't push to it over HTTPS or SSH, so changing the connection won't help. Nothing was changed. Check its SSH keys, or run gh auth setup-git.");
        }

        if (state.Origin is null) return await ConnectFolderAsync(folder, form);

        // Repair can only change the address. If that's already right, it can't help.
        if (SameForm(state.Origin, form))
        {
            return new GitResult(false, "That folder already connects this way, and this way works from outside the folder, so its own Git settings are the likely problem. Nothing was changed.");
        }

        return await MoveOriginAsync(folder, form, state.OriginToCompare!) is { } moveProblem
            ? new GitResult(false, moveProblem)
            : new GitResult(true, form);
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

    /// <summary>Connects a folder with no remote to a repo, setting up Git in it if needed.</summary>
    public static async Task<GitResult> ConnectFolderAsync(string folder, string repoUrl)
    {
        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");

        if (!state.InsideRepo)
        {
            if (await TooBroadForAProjectAsync(folder) is { } tooBroad) return new GitResult(false, tooBroad);
            var init = await RunAsync("git", folder, "init");
            if (init is not { ExitCode: 0 }) return new GitResult(false, "Couldn't set up Git in that folder.");
        }

        var added = await RunAsync("git", folder, "remote", "add", "origin", repoUrl);
        return added is { ExitCode: 0 }
            ? new GitResult(true, repoUrl)
            : new GitResult(false, "Couldn't connect that folder to the repo.");
    }

    /// <summary>
    /// Creates a private GitHub repo for a folder, setting up Git in it if needed. Nothing
    /// is pushed. A folder that already has a remote is only re-pointed when that remote
    /// is the repo being replaced, so an unrelated repo's remote can't be overwritten.
    /// </summary>
    public static async Task<GitResult> CreatePrivateRepoAsync(string projectName, string folder, string? replacingRepo = null, string wantedName = "")
    {
#if DEBUG
        // Trying something out must never leave a real repo on someone's account, or set up
        // Git in a folder for one that's never going to exist.
        if (Headless.Active) return new GitResult(false, "Headless mode doesn't create real repos.");
#endif
        var repoName = ChosenRepoName(wantedName, projectName);
        if (repoName.Length == 0) return new GitResult(false, "A repo's name can only use Latin letters and numbers, and this one has none. Give it a repo name that does, or enter a repo URL.");

        // Only the active account matters, because another one having expired fails this too.
        var signedIn = await RunAsync("gh", Path.GetTempPath(), "auth", "status", "--active", "--hostname", "github.com");
        if (signedIn is null) return new GitResult(false, "Creating a repo needs the GitHub CLI (gh), which wasn't found.");
        if (signedIn.ExitCode != 0)
        {
            if (signedIn.Errors.Contains("unknown flag", StringComparison.OrdinalIgnoreCase)) return new GitResult(false, "This PC's GitHub CLI is too old. Update it from cli.github.com, then try again.");

            // With no connection, gh calls a login it couldn't check invalid, so this really asks.
            var asked = await RunAsync("gh", Path.GetTempPath(), "api", "user");
            return new GitResult(false, LooksLikeNetworkFailure(asked?.Errors ?? "")
                ? "Couldn't reach GitHub. Try again when this PC is online."
                : "Sign in to the GitHub CLI first, with gh auth login.");
        }

        // A deleted repo's name is free on GitHub, but a project can still be pointing at that
        // address, and it would then pass the guard on a repo it never held.
        if (await ActiveAccountAsync() is { } owner)
        {
            var address = $"https://github.com/{owner}/{repoName}.git";
            if ((replacingRepo is null || !SameRepo(address, replacingRepo)) && ProjectUsingRepo(address) is { } otherProject)
            {
                return new GitResult(false, $"{otherProject} still points at {owner}/{repoName}, so a new repo can't use that name. Rename this project, or change {otherProject}'s repo first.");
            }
        }

        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");
        if (state.Origin is not null && (replacingRepo is null || !SameRepo(state.OriginToCompare!, replacingRepo)))
        {
            return new GitResult(false, $"That folder is already connected to a different repo: {state.Origin}");
        }

        // Before creating anything, because the new repo couldn't then be linked to this folder.
        if (state.Origin is not null && await RemoteSetElsewhereAsync(folder) is { } elsewhere) return new GitResult(false, elsewhere);
        if (!state.InsideRepo && await TooBroadForAProjectAsync(folder) is { } tooBroad) return new GitResult(false, tooBroad);

        if (!state.InsideRepo)
        {
            var init = await RunAsync("git", folder, "init");
            if (init is not { ExitCode: 0 }) return new GitResult(false, "Couldn't set up Git in that folder.");
        }

        var created = state.Origin is null
            ? await RunAsync("gh", folder, "repo", "create", repoName, "--private", "--source", folder, "--remote", "origin")
            : await RunAsync("gh", folder, "repo", "create", repoName, "--private");
        if (created is not { ExitCode: 0 })
        {
            if (created?.Errors == TimedOut) return new GitResult(false, $"GitHub took too long to answer, so {repoName} may have been created anyway. Check GitHub before trying again.");
            if (created?.Errors.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new GitResult(false, $"You already have a repo called {repoName}. Enter its URL, or rename this project.");
            }

            // gh can create the repo and only then fail, such as on a locked settings file.
            if (await RunAsync("gh", Path.GetTempPath(), "repo", "view", repoName) is { ExitCode: 0 })
            {
                return new GitResult(false, $"The repo {repoName} was created on GitHub, but couldn't be linked to that folder. Enter its URL to use it.");
            }

            var reason = created?.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            return new GitResult(false, string.IsNullOrEmpty(reason) ? "Couldn't create the repo on GitHub." : $"Couldn't create the repo on GitHub: {reason}");
        }

        // The folder keeps reaching GitHub the way it does now. With no remote yet, a PC whose
        // project was on SSH may have no other way in.
        var webUrl = CreatedRepoUrl().Match(created.Output).Value;
        if (state.Origin is not null && webUrl.Length == 0) return new GitResult(false, $"The repo {repoName} was created, but couldn't be linked to that folder. Enter its URL to use it.");

        string? newOrigin = null;
        if (state.GitHubOrigin is not null) newOrigin = SameWayTo(state.Origin!, webUrl);
        else if (state.Origin is not null) newOrigin = $"{webUrl}.git";
        else if (replacingRepo is not null && UsesSsh(replacingRepo) && webUrl.Length > 0) newOrigin = SameWayTo(replacingRepo, webUrl);

        // With no remote before, the one gh just added is what's there.
        if (newOrigin is not null && await MoveOriginAsync(folder, newOrigin, state.OriginToCompare ?? $"{webUrl}.git") is { } moveProblem)
        {
            return new GitResult(false, $"The repo {repoName} was created, but couldn't be linked to that folder. {moveProblem}");
        }

        var finalState = await InspectFolderAsync(folder);
        if (finalState?.Origin is null) return new GitResult(false, $"The repo {repoName} was created, but couldn't be linked to that folder. Enter its URL to use it.");

        // What syncs says which repo this is. How this PC's folder reaches it stays in the folder.
        var projectUrl = webUrl.Length > 0 ? $"{webUrl}.git" : finalState.OriginToCompare!;
        return RepoUrlProblem(projectUrl) is null
            ? new GitResult(true, projectUrl)
            : new GitResult(false, $"The repo was created, but its address couldn't be worked out from {finalState.Origin}.");
    }

    /// <summary>Where a clone of this repo would end up inside a folder, if the URL names one.</summary>
    public static string? CloneDestination(string url, string parentFolder) =>
        FolderNameFor(url) is { } name ? Path.Combine(parentFolder, name) : null;

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

    /// <summary>Whether a problem with a folder is that it's on another repo, which is what moving it to this one fixes.</summary>
    public static bool ConnectingCanFix(string folderProblem) =>
        folderProblem.StartsWith("That folder uses a different repo", StringComparison.Ordinal)
        || folderProblem.StartsWith(MayUseDifferentRepo, StringComparison.Ordinal)
        || folderProblem.StartsWith("That folder isn't connected to a repo", StringComparison.Ordinal);

    /// <summary>Whether a folder was really seen on another repo, and not just thought to be with no way to check.</summary>
    public static bool SeenOnAnotherRepo(string folderProblem) =>
        ConnectingCanFix(folderProblem) && !folderProblem.StartsWith(MayUseDifferentRepo, StringComparison.Ordinal);

    private const string MayUseDifferentRepo = "That folder may use a different repo: ";

    /// <summary>Whether a repo has anything in it, or null if that couldn't be found out.</summary>
    public static async Task<bool?> RepoHasContentAsync(string url)
    {
        var form = await ReachableFormAsync(url) ?? url;
        var refs = await RunAsync("git", Path.GetTempPath(), null, ["ls-remote", "--", form], default, await SshSettingsAsync(Path.GetTempPath()));
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

    /// <summary>
    /// Clones into a new folder inside the chosen one, or into exactFolder if it's empty, and
    /// returns the resulting folder's path. workingFolder says where it's working, so that can
    /// be cleaned up if the app closes first, and is told null once that folder holds a finished
    /// clone that must not be cleaned up. Its large files and submodules aren't fetched yet, and
    /// that's FinishDownloadAsync, once the folder is linked.
    /// </summary>
    public static async Task<GitResult> CloneAsync(string url, string parentFolder, CancellationToken cancel, Action<string?> workingFolder, string? exactFolder = null, Action<string>? progress = null)
    {
        if (RepoUrlProblem(url) is { } problem) return new GitResult(false, problem);

        // A repo inside a repo gets picked up by the outer one's next commit.
        var parent = await InspectFolderAsync(parentFolder);
        if (parent is { InsideRepo: true }) return new GitResult(false, $"{parentFolder} is inside a Git repo, and a project can't be cloned into another repo. Choose somewhere else.");
        if (parent?.Problem is { } parentProblem) return new GitResult(false, $"Couldn't clone into {parentFolder}. {parentProblem}");
        if ((exactFolder ?? CloneDestination(url, parentFolder)) is not { } destination) return new GitResult(false, "That repo URL doesn't name a folder to clone into.");

        var form = await ReachableFormAsync(url, cancel) ?? url;
        if (cancel.IsCancellationRequested) return new GitResult(false, "The clone was cancelled.");

        // Cloning into a folder that only this clone owns means a failure can never remove
        // anything else, and the real name only appears once the clone is complete.
        // Short, because a long name here can push a deep repo past the limit of tools that
        // don't handle long paths, even with Git set up to.
        var working = Path.Combine(parentFolder, $".ao-{Guid.NewGuid():N}"[..12]);
        workingFolder(working);
        string? checkoutProblem = null;
        try
        {
            if (await DestinationTakenAsync(destination)) return new GitResult(false, $"{destination} already exists and isn't empty.");

            // Working beside the chosen folder needs permission there too, so that's found out first.
            if (exactFolder is not null) await Task.Run(() => Directory.CreateDirectory(working));

            string? stage = null;
            void ShowStage(string line)
            {
                var match = CloneStage().Match(line);
                if (!match.Success) return;
                var text = $"{match.Groups[1].Value}: {match.Groups[2].Value}";
                if (text == stage) return;
                stage = text;
                progress?.Invoke(text);
            }

            // Large files come afterward, on their own, because that can pick up where it left off
            // and a problem with them shouldn't cost the rest.
            var settings = await SshSettingsAsync(Path.GetTempPath()) ?? [];
            settings["GIT_LFS_SKIP_SMUDGE"] = "1";

            var result = await RunAsync("git", parentFolder, null, ["clone", "--progress", "--config", "core.longpaths=true", "--", form, working], cancel, settings, ShowStage);
            if (result is null) return new GitResult(false, "Git wasn't found on this PC.");

            if (result.ExitCode != 0)
            {
                var reason = GitFailureReason(result.Errors);

                // Everything arrived, and only putting the files in place failed, such as on a
                // file name Windows can't use. That's hours of download, and it can be fixed.
                // Git removes the folder when the download itself fails. If it's still there, only
                // Git's own answer that it holds no commits can cost what's in it.
                if (!cancel.IsCancellationRequested && await FolderExistsAsync(working) && !SaysNoCommits(await RunAsync("git", working, "rev-parse", "--verify", "-q", "HEAD")))
                {
                    checkoutProblem = $"Everything downloaded, but some files couldn't be put in place: {AsSentence(reason)} Fix that, then run git restore --source=HEAD --staged --worktree :/ in the folder. That also discards any changes made in it since.";
                }
                else
                {
                    await TryDeleteOwnedFolderAsync(working);
                    if (cancel.IsCancellationRequested) return new GitResult(false, "The clone was cancelled.");
                    return new GitResult(false, reason.Length == 0 ? "Couldn't clone that repo." : $"Couldn't clone that repo: {reason}");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't start a clone: {error.Message}");
            await TryDeleteOwnedFolderAsync(working);
            return new GitResult(false, exactFolder is null
                ? $"Couldn't put the project in {parentFolder}."
                : $"Couldn't clone into {exactFolder}. The clone is prepared beside it, in {parentFolder}, and that can't be written to. Make a new folder inside this one and choose that.");
        }

        // From here it's a finished clone, which could have taken hours, so it's never deleted,
        // including by the cleanup at the next start if the app closes during this.
        if (!await NoteUnfinishedInFolderAsync(working, true))
        {
            checkoutProblem ??= "The clone finished, but this app couldn't write to its Git data, so its large files and submodules weren't fetched. If it uses them, run git submodule update --init --recursive and git lfs pull in the folder.";

            // Without that note, this record is all that would say it's a clone to clean up.
            TryNoting(() => workingFolder(null));
        }

        // Already marked is some other clone's, which this one not getting there says nothing about.
        var alreadyMarked = false;
        TryNoting(() => alreadyMarked = HasUnfinishedDownload(destination));
        TryNoting(() => RememberUnfinishedDownload(destination, true));
        try
        {
            await MoveIntoPlaceAsync(working, destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't name a finished clone: {error.Message}");
            if (!alreadyMarked) TryNoting(() => RememberUnfinishedDownload(destination, false));
            TryNoting(() => workingFolder(null));
            return new GitResult(false, $"The clone finished, but it couldn't be named {destination}. It's kept at {await KeepVisibleAsync(working, destination)}. You can rename it and select it. If it uses large files or submodules, get them with git lfs pull and git submodule update --init --recursive.");
        }
        TryNoting(() => workingFolder(null));
        return new GitResult(true, destination, checkoutProblem);
    }

    /// <summary>
    /// The last of a clone, which can take longer than the rest and can pick up where it left off.
    /// Until this has worked, nothing in the folder would say that files are missing or are only
    /// placeholders, so it stays remembered. Returns what's still missing, or null if nothing is.
    /// </summary>
    public static async Task<string?> FinishDownloadAsync(string folder, CancellationToken cancel = default, Action<string>? progress = null)
    {
        try
        {
            return await FinishDownloadInFolderAsync(folder, cancel, progress);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't finish a download: {error.Message}");
            return $"That folder stopped being available: {error.Message} What's arrived so far is kept, so try again once it's back.";
        }
    }

    private static async Task<string?> FinishDownloadInFolderAsync(string folder, CancellationToken cancel, Action<string>? progress)
    {
        // Getting no answer isn't the same as there being nothing to do, so it stays unfinished.
        const string noAnswer = "Git didn't answer when asked what this folder still needs, so it can't be called finished yet. Try again.";

        // An empty repo has nothing to fetch.
        var head = await RunAsync("git", folder, "rev-parse", "--verify", "-q", "HEAD");
        if (head is not { ExitCode: 0 or 1 }) return noAnswer;
        var hasCommits = head.ExitCode == 0;

        // A checkout that failed outright leaves Git with no list of the repo's files at all, and
        // with nothing to go on it would say all is well. Files someone has since deleted or changed
        // are ordinary work, and have no bearing on this.
        if (hasCommits)
        {
            var list = await RunAsync("git", folder, "rev-parse", "--git-path", "index");
            var inCommit = await RunAsync("git", folder, "ls-tree", "HEAD");
            if (list is not { ExitCode: 0 } || inCommit is not { ExitCode: 0 }) return noAnswer;
            if (inCommit.Output.Length > 0 && !await Task.Run(() => File.Exists(Path.GetFullPath(list.Output.Trim(), folder))))
            {
                return "This repo's files were never put in the folder, so the rest can't be finished yet. Fix what stopped them, then run git restore --source=HEAD --staged --worktree :/ in the folder and try again. That also discards any changes made in it since.";
            }
        }

        if (hasCommits)
        {
            var submodules = await FetchSubmodulesAsync(folder, cancel, progress);
            if (cancel.IsCancellationRequested) return submodules ?? "The download was stopped before it finished.";

            var largeFiles = await FetchLargeFilesAsync(folder, cancel, progress);
            var notes = new[] { submodules, largeFiles }.OfType<string>().ToList();
            if (notes.Count > 0) return string.Join(' ', notes);
            if (cancel.IsCancellationRequested) return "The download was stopped before it finished.";
        }

        RememberUnfinishedDownload(folder, false);
        await NoteUnfinishedInFolderAsync(folder, false);
        return null;
    }

    /// <summary>For when the person has got what was missing some other way, or doesn't need it.</summary>
    public static async Task ForgetUnfinishedDownloadAsync(string folder)
    {
        RememberUnfinishedDownload(folder, false);
        await NoteUnfinishedInFolderAsync(folder, false);
    }

    private const string UnfinishedNoteFile = "agent-orange-unfinished";

    private const int MaxUnfinishedNoteBytes = 100;

    /// <summary>Set up when the app starts. Says which notes this PC wrote, and remembers or forgets one.</summary>
    public static Func<string, bool> IsUnfinishedNoteFromHere { get; set; } = _ => false;

    public static Action<string, bool> RememberUnfinishedNote { get; set; } = (_, _) => { };

    public static Func<string, bool> HasUnfinishedDownload { get; set; } = _ => false;

    // Also kept with the clone itself, as a folder that gets moved leaves what's remembered by its
    // path behind. It holds something only this PC knows, as a folder can come from anyone, and
    // it shouldn't be able to bring a button that runs Git in it.
    private static async Task<bool> NoteUnfinishedInFolderAsync(string folder, bool unfinished)
    {
        var note = Path.Combine(folder, ".git", UnfinishedNoteFile);
        try
        {
            if (unfinished)
            {
                var token = Guid.NewGuid().ToString("N");
                TryNoting(() => RememberUnfinishedNote(token, true));
                await File.WriteAllTextAsync(note, token);
                return true;
            }

            if (await ReadUnfinishedNoteAsync(note) is { } written) TryNoting(() => RememberUnfinishedNote(written, false));
            await Task.Run(() => File.Delete(note));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't note a clone's unfinished download in its folder: {error.Message}");
            return false;
        }
    }

    // Null if it isn't there. Anything stopping it being read throws, which isn't the same answer.
    private static Task<string?> ReadUnfinishedNoteAsync(string note) => Task.Run(() =>
    {
        try
        {
            return SshConfigFile.IsLink(note) || new FileInfo(note).Length > MaxUnfinishedNoteBytes ? null : File.ReadAllText(note).Trim();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    });

    // True if it's a clone from this PC, false if it's been seen not to be, and null if that can't be told.
    private static async Task<bool?> HoldsNoteFromHereAsync(string folder)
    {
        try
        {
            if (!await FolderExistsAsync(folder)) return null;
            if (await ReadUnfinishedNoteAsync(Path.Combine(folder, ".git", UnfinishedNoteFile)) is { } written && IsUnfinishedNoteFromHere(written)) return true;

            // A drive can drop while that was being read, which would look just the same.
            return await FolderExistsAsync(folder) ? false : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't read a folder's unfinished note: {error.Message}");
            return null;
        }
    }

    /// <summary>A clone from this PC, chosen after being moved, still has what it had when it was left unfinished.</summary>
    public static async Task PickUpUnfinishedNoteAsync(string folder)
    {
        try
        {
            if (await ReadUnfinishedNoteAsync(Path.Combine(folder, ".git", UnfinishedNoteFile)) is { } written && IsUnfinishedNoteFromHere(written)) RememberUnfinishedDownload(folder, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't read a folder's unfinished note: {error.Message}"); // A folder can come from anyone, and it isn't one of this PC's.
        }
    }

    // What GitHub answered, as a number that doesn't change the way the wording of a message can.
    // Null if it didn't answer at all.
    private static async Task<int?> RepoStatusAsync(string slug, string? token)
    {
        var asked = await RunAsync("gh", Path.GetTempPath(), token, ["api", $"repos/{slug}", "--include"]);
        if (asked is null || HttpStatusLine().Match(asked.Output) is not { Success: true } line) return null;
        var status = int.Parse(line.Groups[1].Value);

        // Running out of requests is a 403 too, and says nothing about the repo.
        return status == 403 && RateLimited().IsMatch(asked.Output) ? null : status;
    }

    private static bool MeansBlocked(int? status) => status is 403 or 451;

    private static bool SaysNoCommits(CommandResult? headCheck) =>
        headCheck is { ExitCode: 1 } || (headCheck is { ExitCode: 128 } && headCheck.Errors.Contains("not a git repository", StringComparison.OrdinalIgnoreCase));

    // The database can be on a drive that's dropped, and nothing being noted is worth more than the clone.
    private static void TryNoting(Action note)
    {
        try
        {
            note();
        }
        catch (SqliteException error)
        {
            AppLog.Write($"Couldn't note something about a clone: {error.Message}");
        }
    }

    /// <summary>
    /// Says this folder's download is being finished, to the other windows of the app, until it's
    /// disposed. The handle is what counts, so it goes away by itself if the app does. Null if
    /// another window already has it.
    /// </summary>
    public static IDisposable? TryTakeDownload(string folder)
    {
        try
        {
            // Another window's look at it lasts a moment, and a real hold doesn't.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) Thread.Sleep(100);
                var hold = new Mutex(false, DownloadHoldName(folder), out var isFirst);
                if (isFirst) return hold;
                hold.Dispose();
            }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null; // Another window has it, running as administrator.
        }
    }

    public static bool DownloadIsHeld(string folder)
    {
        try
        {
            if (!Mutex.TryOpenExisting(DownloadHoldName(folder), out var hold)) return false;
            hold.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // There is one, and it belongs to a window running as administrator.
        }
    }

    private static string DownloadHoldName(string folder) =>
        $@"Local\AgentOrangeDownload-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(folder).ToLowerInvariant())))[..32]}";

    // After the clone is safely in place, because a submodule that can't be fetched, such as a
    // private one, shouldn't cost everything else. Git's allowed protocols apply to these too.
    private static async Task<string?> FetchSubmodulesAsync(string folder, CancellationToken cancel, Action<string>? progress, int depth = 0)
    {
        if (depth > MaxSubmoduleDepth) return $"The clone finished, but its submodules go too many levels deep, so the rest weren't fetched. Run git submodule update --init --recursive in {folder} if you trust them.";

        // Only 1 says it's not there. Anything else is Git not being able to answer.
        var declared = await RunAsync("git", folder, "rev-parse", "--verify", "-q", "HEAD:.gitmodules");
        if (declared is { ExitCode: 1 }) return null;
        if (declared is not { ExitCode: 0 }) return "Git didn't answer when asked whether this repo has submodules, so that can't be called finished yet. Try again.";

        // Git can look at a path before its list of allowed protocols applies, and one on a network
        // share would send the person's Windows login there. Both copies count, as Git reads the file.
        if (await IsLinkInFolderAsync(folder, ".gitmodules")) return "The clone finished, but its submodules weren't fetched, because its .gitmodules is a link, which could lead onto a network share and send your Windows login there. Replace it with a real file, then try again.";
        var committed = await RunAsync("git", folder, "config", "--blob", "HEAD:.gitmodules", "-z", "--list");
        var inFolder = await Task.Run(() => File.Exists(Path.Combine(folder, ".gitmodules"))) ? await ReadSettingsFileAsync(Path.Combine(folder, ".gitmodules")) : "";
        if (committed is not { ExitCode: 0 } || inFolder is null) return "Git couldn't read this repo's list of submodules, so they can't be fetched yet. Try again.";
        foreach (var setting in (committed.Output + inFolder).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('\n', 2)).Where(parts => parts.Length == 2 && SubmoduleUrlSetting().IsMatch(parts[0])))
        {
            if (!AllowedSubmoduleUrl().IsMatch(setting[1])) return $"The clone finished, but its submodules weren't fetched, because one points at {HideLogins(OneLine(setting[1]))}, which isn't an address on GitHub or another server. Fix that in .gitmodules, then try again.";
        }

        progress?.Invoke("Fetching submodules");
        // Nothing here can be reached through a link, as just looking could reach a network share.
        var declaredPaths = SubmodulePathsIn(committed.Output).Concat(SubmodulePathsIn(inFolder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in declaredPaths)
        {
            var submodule = Path.GetFullPath(Path.Combine(folder, path));
            var isInside = submodule.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!isInside || await Task.Run(() => SshConfigFile.HasLinkBelowChosenPath(folder, submodule)))
            {
                return $"The clone finished, but its submodules weren't fetched, because {OneLine(path)} is outside the project or reached through a link, which could lead onto a network share and send your Windows login there. Fix that in the repo, then try again.";
            }
        }

        // One level at a time, so each one's list of addresses is checked before Git acts on it.
        var settings = await SshSettingsAsync(folder) ?? [];
        settings["GIT_LFS_SKIP_SMUDGE"] = "1"; // Fetched next, where its address gets checked and it can pick up where it left off.
        var result = await RunAsync("git", folder, null, ["-c", "core.longpaths=true", "submodule", "update", "--init"], cancel, settings);
        if (result is not { ExitCode: 0 })
        {
            var reason = cancel.IsCancellationRequested ? "That was cancelled." : GitFailureReason(result?.Errors ?? "");
            return $"The clone finished, but its submodules didn't. {AsSentence(reason)} Finish the download to try again.";
        }

        foreach (var path in declaredPaths)
        {
            if (cancel.IsCancellationRequested) return null; // Whoever asked for this to stop gets told.
            var submodule = Path.GetFullPath(Path.Combine(folder, path));
            if (!await Task.Run(() => File.Exists(Path.Combine(submodule, ".git")) || Directory.Exists(Path.Combine(submodule, ".git")))) continue;
            if (await FetchSubmodulesAsync(submodule, cancel, progress, depth + 1) is { } further) return further;
            if (cancel.IsCancellationRequested) return null;
            if (await FetchLargeFilesAsync(submodule, cancel, progress) is { } itsLargeFiles) return itsLargeFiles;
        }
        return null;
    }

    // Git LFS keeps a repo's big files apart. Without it set up, a repo that uses it clones fine,
    // with small placeholders instead. Asked of Git, as any folder can declare it, and a file here
    // could be a link. A line that's commented out doesn't count. A quota running out or a dropped
    // connection only costs this step.
    private static async Task<string?> FetchLargeFilesAsync(string folder, CancellationToken cancel, Action<string>? progress)
    {
        var declared = await RunAsync("git", folder, "grep", "-q", "-E", "^[[:space:]]*[^#[:space:]].*filter=lfs", "HEAD", "--", "*.gitattributes");
        if (declared is { ExitCode: 1 }) return null;
        if (declared is not { ExitCode: 0 })
        {
            return "Couldn't check whether this repo uses Git LFS. If its big files look like small text files, run git lfs pull in the folder.";
        }

        var isInstalled = await RunAsync("git", Path.GetTempPath(), "lfs", "version") is { ExitCode: 0 };
        var isSetUp = await RunAsync("git", folder, "config", "--get", "filter.lfs.smudge") is { ExitCode: 0 };
        if (!isInstalled || !isSetUp)
        {
            return "This repo uses Git LFS, which isn't set up on this PC, so its big files are only small placeholders. Install it from git-lfs.com, run git lfs install, then run git lfs pull in the folder.";
        }

        // A repo can name where its large files come from, and this runs by itself after a clone.
        if (await IsLinkInFolderAsync(folder, ".lfsconfig")) return "The clone finished, but its large files weren't fetched, because its .lfsconfig is a link, which could lead onto a network share and send your Windows login there. Replace it with a real file, then try again.";
        var lfsConfig = await RunAsync("git", folder, "config", "--blob", "HEAD:.lfsconfig", "-z", "--list");
        var lfsConfigHere = await Task.Run(() => File.Exists(Path.Combine(folder, ".lfsconfig"))) ? await ReadSettingsFileAsync(Path.Combine(folder, ".lfsconfig")) : "";
        if (lfsConfig is null or { ExitCode: < 0 } || lfsConfigHere is null) return "Git couldn't read where this repo says its large files are, so they can't be fetched yet. Try again.";
        foreach (var setting in ((lfsConfig is { ExitCode: 0 } ? lfsConfig.Output : "") + lfsConfigHere).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('\n', 2)).Where(parts => parts.Length == 2 && LargeFileUrlSetting().IsMatch(parts[0])))
        {
            if (!AllowedLargeFileUrl().IsMatch(setting[1])) return $"The clone finished, but its large files weren't fetched, because the repo says they're at {HideLogins(OneLine(setting[1]))}, which isn't on GitHub. Git LFS would answer a server asking for Windows authentication with your login. If you trust it, run git lfs pull in the folder yourself.";
        }

        // It goes by the remote otherwise, and a submodule's can be anywhere.
        var remote = await RunAsync("git", folder, "remote", "get-url", "origin");
        if (remote is not { ExitCode: 0 }) return "Git couldn't say where this repo's large files come from, so they can't be called finished yet. Try again.";
        var address = WithoutCredentials(remote.Output.Trim());
        var (remoteOnGitHub, _) = await GitHubFormAsync(address, folder);
        if (RepoUrlProblem(remoteOnGitHub ?? address) is not null) return $"The clone finished, but the large files in {folder} weren't fetched, because they'd come from {HideLogins(OneLine(address))}, which isn't on GitHub. Git LFS would answer a server asking for Windows authentication with your login. If you trust it, run git lfs pull in that folder yourself.";

        var gitDirectory = await RunAsync("git", folder, "rev-parse", "--absolute-git-dir");
        if (gitDirectory is not { ExitCode: 0 }) return "Git didn't answer when asked where this repo's data is, so its large files can't be called finished yet. Try again.";
        if (await Task.Run(() => GitFolderSafety.LargeFileStoreProblem(gitDirectory.Output.Trim())) is { } leadsAway)
        {
            return $"The clone finished, but its large files weren't fetched, because {leadsAway}, which is on a network or goes through too many links. Opening one could send your Windows login there. Fix that, then try again.";
        }

        progress?.Invoke("Downloading large files");
        var pulled = await RunAsync("git", folder, null, ["lfs", "pull"], cancel, await SshSettingsAsync(folder));
        if (pulled is { ExitCode: 0 }) return null;

        var reason = cancel.IsCancellationRequested ? "That was cancelled." : GitFailureReason(pulled?.Errors ?? "");
        return $"The clone finished, but its large files didn't. {AsSentence(reason)} Run git lfs pull in the folder to carry on, which picks up where it left off.";
    }

    /// <summary>
    /// GitHub follows renames and transfers, so a repo can keep passing under an old name. This
    /// gives the repo's current address if it has moved, or null if it hasn't or that can't be told.
    /// </summary>
    public static async Task<string?> RenamedRepoUrlAsync(string url)
    {
        var current = (await CurrentRepoUrlAsync(url)).Current;
        return SameRepo(current, url) ? null : current;
    }

    private static readonly TimeSpan RepoNamesKeptFor = TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Current, DateTimeOffset Asked)> RecentRepoNames = new(StringComparer.OrdinalIgnoreCase);

    // For asking about many at once, every other project or a folder's push addresses, where an
    // answer a few minutes old will do.
    private static async Task<(string Current, bool Answered)> RecentRepoUrlAsync(string url)
    {
        if (RecentRepoNames.TryGetValue(url, out var known) && DateTimeOffset.UtcNow - known.Asked < RepoNamesKeptFor) return (known.Current, true);

        var answer = await CurrentRepoUrlAsync(url);
        if (answer.Answered) RecentRepoNames[url] = (answer.Current, DateTimeOffset.UtcNow);
        return (answer.Current, answer.Answered);
    }

    // The address itself if it hasn't moved, or that can't be told. Answered says GitHub really
    // answered, with a name or by saying the repo's gone, and anything else isn't kept or relied on.
    // Found says GitHub gave a name, which a repo that's gone, or one this account can't see, doesn't get.
    private readonly record struct RepoAnswer(string Current, bool Answered, bool Found);

    private static async Task<RepoAnswer> CurrentRepoUrlAsync(string url)
    {
        if (RepoUrlProblem(url) is not null) return new(url, true, false);

        var parts = RepoIdentity(url).Split('/');
        var slug = $"{parts[1]}/{parts[2]}";
        var view = await RunAsync("gh", Path.GetTempPath(), "repo", "view", slug, "--json", "nameWithOwner", "--jq", ".nameWithOwner");
        if (view is null or { ExitCode: < 0 }) return new(url, false, false);
        if (view.ExitCode != 0) return new(url, SaysRepoMissing(view.Errors) || view.Errors.Contains("HTTP 404", StringComparison.Ordinal), false);

        var current = view.Output.Trim();
        if (current.Length == 0) return new(url, false, false);
        if (current.Equals(slug, StringComparison.OrdinalIgnoreCase)) return new(url, true, true);

        var renamed = $"https://github.com/{current}.git";
        return new(RepoUrlProblem(renamed) is null ? renamed : url, true, true);
    }

    private static string AsSentence(string reason) => reason.Length == 0 ? "" : $"{reason.TrimEnd('.')}.";

    // Git's real complaint, not its last line, which is often advice for a folder now gone.
    private static string GitFailureReason(string errors)
    {
        var lines = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failures = lines.Where(line => line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase) || SshComplaint().IsMatch(line)).Take(3).ToList();
        return failures.Count > 0 ? string.Join(' ', failures) : lines.LastOrDefault() ?? "";
    }

    private static async Task<string> KeepVisibleAsync(string working, string destination)
    {
        var kept = $"{destination}-cloned-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            await Task.Run(() => Directory.Move(working, kept));
            return kept;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't rename a finished clone: {error.Message}");
            return working;
        }
    }

    /// <summary>
    /// Removes what a clone left when the app closed in the middle of it. Only ever touches a
    /// folder named by CloneAsync. TryAgain means it's still there, or its drive isn't plugged
    /// in, so it should be tried again next time.
    /// </summary>
    public static async Task<LeftoverClone> RemoveLeftoverCloneAsync(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !CloneWorkingFolderName().IsMatch(Path.GetFileName(folder))) return LeftoverClone.Gone;

        var parent = Path.GetDirectoryName(folder);
        if (parent is null || !await FolderExistsAsync(parent)) return LeftoverClone.TryAgain;

        // It finished, and only forgetting about it failed, such as with the database out of reach.
        switch (await Task.Run(() => NoteIsThere(Path.Combine(folder, ".git", UnfinishedNoteFile))))
        {
            case true:
                AppLog.Write($"Kept a finished clone that was still on the cleanup list: {folder}");
                return LeftoverClone.KeptFinished;
            case null:
                return LeftoverClone.TryAgain;
        }

        // A drive that's just dropped says the note isn't there too.
        if (!await FolderExistsAsync(parent)) return LeftoverClone.TryAgain;

        await TryDeleteOwnedFolderAsync(folder);
        return await FolderExistsAsync(folder) ? LeftoverClone.TryAgain : LeftoverClone.Gone;
    }

    // Null if that can't be told, which anything other than it being missing means.
    private static bool? NoteIsThere(string note)
    {
        try
        {
            File.GetAttributes(note);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't tell whether a clone had finished: {error.Message}");
            return null;
        }
    }

    // A virus scanner can hold new files for a moment, and giving up would throw away a whole clone.
    private static async Task MoveIntoPlaceAsync(string working, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (Directory.Exists(destination) && !Directory.EnumerateFileSystemEntries(destination).Any()) Directory.Delete(destination);
                    Directory.Move(working, destination);
                });
                return;
            }
            catch (Exception error) when (attempt < 10 && error is IOException or UnauthorizedAccessException)
            {
                if (await DestinationTakenAsync(destination)) throw;
                await Task.Delay(1000);
            }
        }
    }

    private static Task<bool> DestinationTakenAsync(string destination) => Task.Run(() => IsTaken(destination));

    private static bool HasAnything(string destination)
    {
        try
        {
            return Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true; // Can't be looked into, so it can't be counted on as free.
        }
    }

    // A link there isn't an empty folder to take over. Removing it would put the clone somewhere else.
    private static bool IsTaken(string destination) =>
        HasAnything(destination) || File.Exists(destination) || (Directory.Exists(destination) && new DirectoryInfo(destination).LinkTarget is not null);

    // Files can stay locked for a moment after a stopped clone exits, so this tries a few times.
    private static async Task TryDeleteOwnedFolderAsync(string folder)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (!Directory.Exists(folder)) return;

                    // Links aren't followed, because what they point at isn't this clone's to change.
                    var inside = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in Directory.EnumerateFiles(folder, "*", inside)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(folder, recursive: true);
                });
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) AppLog.Write($"Couldn't remove a partial clone: {error.Message}");
                else await Task.Delay(500);
            }
        }
    }

    // A port can mean a different server, so it stays. GitHub ignores case in its paths.
    private static string RepoIdentity(string url)
    {
        var text = url.Trim();
        var host = "";
        if (ScpStyleRepo().IsMatch(text))
        {
            var at = text.IndexOf('@');
            var colon = text.IndexOf(':');
            host = text[(at + 1)..colon];
            text = text[(colon + 1)..];
        }
        else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            text = uri.AbsolutePath.TrimStart('/');
        }

        text = text.TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        return (host.Length > 0 ? $"{host}/{text}" : text).ToLowerInvariant();
    }

    // Git can report a remote with a token in it, which must never be shown or saved.
    private static string WithoutCredentials(string url)
    {
        if (ScpStyleRepo().IsMatch(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0) return url;
        if (uri.Scheme == "ssh" && !uri.UserInfo.Contains(':') && uri.Query.Length == 0 && uri.Fragment.Length == 0) return url;

        var clean = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return clean.Uri.AbsoluteUri;
    }

    private static Task<CommandResult?> ViewRepoAsync(string slug, string? token) =>
        RunAsync("gh", Path.GetTempPath(), token, ["repo", "view", slug, "--json", "viewerPermission,isArchived"]);

    private static async Task<List<string>> SignedInAccountsAsync()
    {
        var status = await RunAsync("gh", Path.GetTempPath(), "auth", "status", "--hostname", "github.com");
        if (status is null) return [];
        return SignedInAccount().Matches(status.Output + status.Errors).Select(match => match.Groups[1].Value).Distinct().ToList();
    }

    private static async Task<string?> ActiveAccountAsync()
    {
        var user = await RunAsync("gh", Path.GetTempPath(), "api", "user", "--jq", ".login");
        return user is { ExitCode: 0 } ? user.Output.Trim() : null;
    }

    // A GitHub login isn't enough, because Git may use a different key or credential on this
    // PC. If Git gets no answer at all, such as port 22 being blocked, that's the same as being
    // offline. Only being turned away means this PC's setup is what's wrong.
    private static async Task<RepoCheck> CheckGitCanReachAsync(string url, string? folder, bool useFolderSettings)
    {
        if (await Task.Run(() => FindOnPath("git")) is null) return new RepoCheck(RepoAccess.NoGit);

        // A connected folder is how this PC really works with the repo. It runs from inside
        // the folder because the folder's own Git settings can say which key to use.
        var state = folder is not null && await FolderExistsAsync(folder) ? await InspectFolderAsync(folder) : null;
        if (useFolderSettings && state?.OriginToCompare is { } origin && SameRepo(origin, url))
        {
            // By name, because the address on show has had any credentials taken out of it.
            var viaFolder = await LsRemoteAsync("origin", folder!);
            if (viaFolder is { ExitCode: 0 })
            {
                // A folder that pushes somewhere else gets reported as that, without contacting it.
                if (await FolderProblemAsync(folder!, url) is not null || await PushAddressElsewhereAsync(folder!, "origin", url) is not null) return new RepoCheck(RepoAccess.Allowed);
                return new RepoCheck(await GitPushAnswerAsync("origin", folder!) switch
                {
                    PushAnswer.CanPush => RepoAccess.Allowed,
                    PushAnswer.Refused => RepoAccess.GitReadOnly,
                    _ => RepoAccess.GitPushUnchecked,
                });
            }

            // No answer this way, such as a blocked SSH port, isn't being offline if there's
            // another way in. Saying so is what offers to repair the folder.
            var failure = GitFailure(viaFolder);
            if (failure == RepoAccess.Offline && await TryFormsAsync(url) != RepoAccess.Offline) failure = RepoAccess.GitCantReach;
            return new RepoCheck(failure);
        }

        return new RepoCheck(await TryFormsAsync(url));
    }

    // If neither way works, what the way this PC usually connects says is what counts. No answer
    // there is a network or GitHub problem, even if the other way turns it away, as it would on a
    // PC with no SSH key. Being turned away there means something on this PC needs fixing.
    private static async Task<RepoAccess> TryFormsAsync(string url)
    {
        RepoAccess? usualWay = null;
        var readOnly = false;
        var unsure = false;
        foreach (var form in await FormsToTryAsync(url))
        {
            var attempt = await LsRemoteAsync(form, Path.GetTempPath());
            if (attempt is { ExitCode: 0 })
            {
                switch (await GitPushAnswerAsync(form, null))
                {
                    case PushAnswer.CanPush:
                        return RepoAccess.Allowed;
                    case PushAnswer.Refused:
                        readOnly = true;
                        break;
                    default:
                        unsure = true;
                        break;
                }
                continue;
            }
            usualWay ??= GitFailure(attempt);
        }
        return readOnly ? RepoAccess.GitReadOnly : unsure ? RepoAccess.GitPushUnchecked : usualWay ?? RepoAccess.Offline;
    }

    private enum PushAnswer { CanPush, Refused, CantTell }

    // Reading works with a read-only deploy key too, so this asks whether Git could push. It's a
    // dry run of deleting a branch that doesn't exist, which GitHub has to authorize the same way
    // and which can't change anything. Being told that branch doesn't exist means it was allowed
    // to ask. A clear refusal is the only other answer, and anything else says nothing either way.
    private static async Task<PushAnswer> GitPushAnswerAsync(string remote, string? folder, CancellationToken cancel = default)
    {
        // Git only pushes from inside a repo, and folder is the one whose settings are allowed.
        var scratch = folder is null ? Path.Combine(Path.GetTempPath(), $".ao-{Guid.NewGuid():N}"[..12]) : null;
        try
        {
            if (scratch is not null)
            {
                await Task.Run(() => Directory.CreateDirectory(scratch));
                if (await RunAsync("git", scratch, "init", "-q") is not { ExitCode: 0 }) return PushAnswer.CantTell;
            }

            var where = folder ?? scratch!;
            List<string> push = folder is null ? [] : ["-c", $"remote.{remote}.mirror=false"];
            push.AddRange(["push", "--dry-run", "--no-verify", "--", remote, $":refs/heads/agent-orange-check-{Guid.NewGuid():N}"]);
            var result = await RunAsync("git", where, null, push.ToArray(), cancel, await SshSettingsAsync(where));
            if (result is null or { ExitCode: < 0 }) return PushAnswer.CantTell;
            if (result.ExitCode == 0) return PushAnswer.CanPush;

            // Addresses come out first, because a repo can be called something like read-only-demo.
            var said = QuotedText().Replace(result.Errors, "");
            if (PushRefusal().IsMatch(said)) return PushAnswer.Refused;
            return said.Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase) ? PushAnswer.CanPush : PushAnswer.CantTell;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't check whether Git can push: {error.Message}");
            return PushAnswer.CantTell;
        }
        finally
        {
            if (scratch is not null) await TryDeleteOwnedFolderAsync(scratch);
        }
    }

    private static RepoAccess GitFailure(CommandResult? result) =>
        result is not null && LooksLikeNetworkFailure(result.Errors) ? RepoAccess.Offline : RepoAccess.GitCantReach;

    private static async Task<CommandResult?> LsRemoteAsync(string remote, string folder, CancellationToken cancel = default) =>
        await RunAsync("git", folder, null, ["ls-remote", "--", remote, "HEAD"], cancel, await SshSettingsAsync(folder));

    // There's no terminal to answer SSH's questions in, so it should fail rather than wait.
    // A custom SSH command from the person's Git settings is left alone.
    private static async Task<Dictionary<string, string>?> SshSettingsAsync(string folder)
    {
        var custom = await RunAsync("git", folder, "config", "--get", "core.sshCommand");
        var hasCustom = custom is { ExitCode: 0 } && custom.Output.Trim().Length > 0;
        return hasCustom
            || Environment.GetEnvironmentVariable("GIT_SSH_COMMAND") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("GIT_SSH") is { Length: > 0 }
            ? null
            : new Dictionary<string, string> { ["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes" };
    }

    // A clear refusal from the server wins, so a lost permission can't pass as being offline.
    // URLs are removed first, because a repo can be called something like proxy-wars.
    private static bool LooksLikeNetworkFailure(string errors)
    {
        var message = QuotedText().Replace(errors, "");
        return !ServerRefusal().IsMatch(message) && NetworkFailure().IsMatch(message);
    }

    private static bool SaysRepoMissing(string errors) =>
        errors.Contains("Could not resolve to a Repository", StringComparison.OrdinalIgnoreCase);

    // All of it, because Git often puts the way to fix a problem on its later lines.
    private static string GitProblem(string summary, string errors)
    {
        var reason = string.Join('\n', errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Take(8));
        return reason.Length == 0 ? $"{summary}." : $"{summary}: {reason}";
    }

    private static string CommandShell =>
        Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } shell
            ? shell
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    // Includes Windows' saved settings, so something installed while the app is open is found
    // when the person clicks Try again, without a restart.
    private static string CurrentPath()
    {
        EnvironmentVariableTarget[] targets = [EnvironmentVariableTarget.Process, EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User];
        var folders = targets
            .SelectMany(target => (Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Select(folder => folder.Trim().Trim('"')) // A terminal copes with these, so they count here too.
            .Where(Path.IsPathFullyQualified) // Others mean whatever folder something runs in.
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(Path.PathSeparator, folders);
    }

    // Starting a program by bare name can pick up a file in the current folder, which may
    // be one the person just browsed to. Looking in PATH ourselves rules that out.
    private static string? FindOnPath(string program)
    {
        var fileName = $"{program}.exe";
        foreach (var folder in CurrentPath().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool UsesSsh(string url) =>
        ScpStyleRepo().IsMatch(url) || url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

    public static string RepoName(string projectName) => UsableRepoName(projectName.Trim().ToLowerInvariant());

    /// <summary>What a typed name stays as, capitals included, or else what comes from the project's name.</summary>
    public static string ChosenRepoName(string typed, string projectName) =>
        typed.Trim().Length > 0 ? UsableRepoName(typed.Trim()) : RepoName(projectName);

    private static string UsableRepoName(string text)
    {
        var name = NotRepoSafe().Replace(text, "-").Trim('-');

        // GitHub has no use for these, and asking comes after Git has been set up in the folder.
        return name.Trim('.').Length == 0 || name.Equals(".git", StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    // The URL can come from another PC, so only a plain name can come out of it.
    private static string? FolderNameFor(string url)
    {
        var last = url.TrimEnd('/').Split('/', ':', '\\').Last();
        var name = last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
        return SafeFolderName().IsMatch(name) && name.Trim('.').Length > 0 ? name : null;
    }

    private static Task<CommandResult?> RunAsync(string program, string folder, params string[] arguments) =>
        RunAsync(program, folder, null, arguments);

    // Checks have a time limit. A clone can take as long as it needs, and stops only when cancelled.
    private static async Task<CommandResult?> RunAsync(string program, string folder, string? gitHubToken, string[] arguments, CancellationToken cancel = default, Dictionary<string, string>? extraSettings = null, Action<string>? statusLine = null)
    {
#if DEBUG
        // Trying something out must never leave a real repo on someone's account.
        if (Headless.Active && arguments is ["repo", "create", ..] or ["auth", "switch", ..]) return new CommandResult(1, "", "Headless mode doesn't change anything real on GitHub.");
#endif
        if (await Task.Run(() => FindOnPath(program)) is not { } programPath) return null;
        if (!await FolderExistsAsync(folder)) return new CommandResult(-1, "", "That folder can't be reached.");

        // Before Git runs at all, because Git follows these just by starting up in the folder.
        if (program == "git" && folder != Path.GetTempPath() && await GitFolderSafety.NetworkPathProblemAsync(folder, ReadSettingsFileAsync) is { } networkProblem)
        {
            return new CommandResult(128, "", networkProblem);
        }

        var info = new ProcessStartInfo(programPath)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), // Both tools write UTF-8, whatever Windows is set to.
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // A folder with no .git that's laid out as a bare repo would otherwise count as one, with
        // settings Git reads as soon as it starts. Older versions of Git ignore this.
        if (program == "git")
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("safe.bareRepository=explicit");

            // A repo can name any server for its submodules. One that asks for Windows authentication
            // would otherwise get the person's Windows login. This reaches submodule commands too.
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("http.emptyAuth=false");
        }

        // Projects need the GitHub CLI anyway, so its login backs up whatever Git has of its own.
        // Helpers given here come after the person's own, so theirs still goes first.
        if (program == "git" && await Task.Run(() => FindOnPath("gh")) is { } gitHubCli)
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"credential.https://github.com.helper=!'{gitHubCli.Replace('\\', '/').Replace("'", "'\\''")}' auth git-credential");
        }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PATH"] = CurrentPath(); // gh runs git itself.
        info.Environment["LC_ALL"] = "C.UTF-8"; // Some answers are read by this app, in English.
        info.Environment["GH_HOST"] = "github.com"; // Repos are on github.com, whatever else this PC is set up for.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Set if this app was started from a Git hook, and every answer would be about that repo.
        foreach (var inherited in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_PREFIX" })
        {
            info.Environment.Remove(inherited);
        }
        info.Environment["GIT_ALLOW_PROTOCOL"] = "https:ssh";
        if (gitHubToken is not null) info.Environment["GH_TOKEN"] = gitHubToken;
        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings) info.Environment[key] = value;
        }

        // Never a login window: the person has a GitHub CLI login by the time any of this runs.
        var isClone = arguments is ["clone", ..] or ["lfs", "pull"] || arguments.Contains("submodule");
        info.Environment["GCM_INTERACTIVE"] = "never";
        info.Environment["GCM_ALLOW_WINDOWSAUTH"] = "0";

        var isCreate = arguments is ["repo", "create", ..];

        // Before a clone can be called finished, on what can be a very large repo and a cold disk.
        var readsWholeRepo = arguments is ["grep", ..];
        using var timeout = new CancellationTokenSource(isClone ? Timeout.InfiniteTimeSpan : isCreate ? CreateTimeout : readsWholeRepo ? WholeRepoTimeout : CommandTimeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel);

        Process? process = null;
        try
        {
            process = await Task.Run(() => Process.Start(info));
            if (process is null) return null;
            ChildProcesses.Track(process);
            var output = process.StandardOutput.ReadToEndAsync(stop.Token);
            var errors = statusLine is null ? process.StandardError.ReadToEndAsync(stop.Token) : ReadErrorsWithStatusAsync(process.StandardError, statusLine, stop.Token);
            await process.WaitForExitAsync(stop.Token);

            // Something it left running can hold these open forever, and a clone has no other limit.
            await Task.WhenAny(Task.WhenAll(output, errors), Task.Delay(AfterExitLimit, stop.Token));

            // What it would have said isn't known, and empty is an answer. A clone says nothing there.
            if (!output.IsCompletedSuccessfully && !isClone) return new CommandResult(-1, "", TimedOut);
            return new CommandResult(process.ExitCode, output.IsCompletedSuccessfully ? output.Result : "", errors.IsCompletedSuccessfully ? errors.Result : "");
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process);
            return new CommandResult(-1, "", TimedOut);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            // It was found, so it's not missing, and asking someone to install it wouldn't help.
            AppLog.Write($"Couldn't run {program}: {error.Message}");
            return new CommandResult(-1, "", $"Couldn't run {program}: {error.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    // From outside the folder, and without following anything it includes.
    private static async Task<string?> ReadSettingsFileAsync(string file) =>
        await RunAsync("git", Path.GetTempPath(), "config", "--no-includes", "--file", file, "-z", "--list") is { ExitCode: 0 } read ? read.Output : null;

    // Git rewrites a status line by ending it with a return, so only finished lines are kept. ssh
    // ends each of its lines with a return and a new line, and those are finished lines.
    private static async Task<string> ReadErrorsWithStatusAsync(StreamReader reader, Action<string> statusLine, CancellationToken cancel)
    {
        var kept = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[1024];
        var afterReturn = false; // Which of the two it was depends on what comes next, which may not have arrived yet.
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancel)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (character == '\n')
                {
                    kept.Append(line).Append('\n');
                    line.Clear();
                }
                else if (character == '\r')
                {
                    statusLine(line.ToString());
                }
                else
                {
                    if (afterReturn) line.Clear();
                    line.Append(character);
                }
                afterReturn = character == '\r';
            }
        }
        if (!afterReturn) kept.Append(line);
        return kept.ToString();
    }

    // Waits for it to really be gone, because its files can't be cleaned up until then.
    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            process.Kill(entireProcessTree: true);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(limit.Token);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException or OperationCanceledException)
        {
            // It finished by itself in the meantime, or is taking too long to matter.
        }
    }

    // The short form Git uses for SSH, like git@github.com:you/game.git.
    [GeneratedRegex(@"^[\w.-]+@[\w.-]+:[\w./-]+\z")]
    private static partial Regex ScpStyleRepo();

    [GeneratedRegex(@"^org-\d+\z")]
    private static partial Regex CertificateUser();

    [GeneratedRegex(@"^(?:org-\d+@(?i:(?:ssh\.)?github\.com):|ssh://org-\d+@(?i:(?:ssh\.)?github\.com)(?::\d+)?/)([A-Za-z0-9][A-Za-z0-9_-]*/(?!(?:\.{1,2}|\.git)/?\z)[A-Za-z0-9._-]+?)/?\z")]
    private static partial Regex CertificateAddress();

    [GeneratedRegex(@"^git@github\.com:[A-Za-z0-9][A-Za-z0-9_-]*/(?!(\.{1,2}|\.git)/?\z)[A-Za-z0-9._-]+/?\z", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubScpRepo();

    [GeneratedRegex(@"^/[A-Za-z0-9][A-Za-z0-9_-]*/(?!(\.{1,2}|\.git)/?\z)[A-Za-z0-9._-]+/?\z")]
    private static partial Regex GitHubRepoPath();

    // Git's short form with no username, like work:you/game.git. One letter would be a drive.
    [GeneratedRegex(@"^[A-Za-z0-9][\w.-]+:[\w.-]+/[\w./-]+\z")]
    private static partial Regex BareScpRepo();

    [GeneratedRegex(@"\.(extraheader|password|oauthrefreshtoken)$", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveSetting();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRun();

    [GeneratedRegex(@"^(Receiving objects|Resolving deltas|Updating files|Filtering content):\s+(\d+%)")]
    private static partial Regex CloneStage();

    [GeneratedRegex(@"marked as read.only|Permission to \S+ denied|Permission denied \(|error: 403|authentication failed|could not read username|invalid username|host key verification failed", RegexOptions.IgnoreCase)]
    private static partial Regex PushRefusal();

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex LoginInUrl();

    [GeneratedRegex(@"\b(gh[pousr]_|github_pat_)\w+")]
    private static partial Regex TokenLike();

    [GeneratedRegex(@"""isArchived""\s*:\s*true")]
    private static partial Regex ArchivedRepo();

    [GeneratedRegex(@"^user\s+(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SshUser();

    [GeneratedRegex(@"(?:^|\s)-F\s*(""[^""]+""|'[^']+'|\S+)")]
    private static partial Regex SshConfigOption();

    [GeneratedRegex(@"^[A-Za-z0-9][\w.-]*\z")]
    private static partial Regex SafeSshHost();

    [GeneratedRegex(@"^hostname\s+(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SshHostName();

    [GeneratedRegex(@"""viewerPermission""\s*:\s*""(ADMIN|MAINTAIN|WRITE)""")]
    private static partial Regex WritePermission();

    [GeneratedRegex(@"SAML|not accessible by|IP allow|allow list|is not permitted", RegexOptions.IgnoreCase)]
    private static partial Regex AccessBlocked();

    [GeneratedRegex(@"^\.(ao-[0-9a-f]{8}|.+\.cloning-[0-9a-f]{32})\z")]
    private static partial Regex CloneWorkingFolderName();

    [GeneratedRegex(@"HTTP 401|gh auth login|not logged in", RegexOptions.IgnoreCase)]
    private static partial Regex SignedOutMessage();

    [GeneratedRegex(@"timeout|timed out|schannel: failed to receive handshake|ssl_connect|unexpected disconnect|could not resolve host|no such host|dial tcp|could not connect|failed to connect|error connecting to|check your internet connection|connection (was )?(refused|reset|closed)|returned error: 5\d\d|remote end hung up|empty reply from server|network is unreachable|tls handshake|proxyconnect|temporary failure", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkFailure();

    [GeneratedRegex(@"^HTTP/\S+\s+(\d{3})\b", RegexOptions.Multiline)]
    private static partial Regex HttpStatusLine();

    [GeneratedRegex(@"x-ratelimit-remaining:\s*0\b|rate limit", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimited();

    [GeneratedRegex(@"returned error: 4\d\d|\bnot found\b|permission denied|authentication failed|access denied", RegexOptions.IgnoreCase)]
    private static partial Regex ServerRefusal();

    [GeneratedRegex(@"'[^']*'|""[^""]*""|\S+://\S+")]
    private static partial Regex QuotedText();

    [GeneratedRegex(@"^[\w.-]+\z")]
    private static partial Regex SafeFolderName();

    [GeneratedRegex(@"Logged in to github\.com account (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SignedInAccount();

    [GeneratedRegex(@"https://github\.com/[\w.-]+/[\w.-]+")]
    private static partial Regex CreatedRepoUrl();

    [GeneratedRegex(@"^include(if\..*)?\.", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SubmoduleInclude();

    [GeneratedRegex(@"^submodule\..*\.path$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SubmodulePathSetting();

    [GeneratedRegex(@"^submodule\..*\.url$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SubmoduleUrlSetting();

    [GeneratedRegex(@"^(https://|ssh://|git@|[\w.-]+@[\w.-]+:|\.\.?/)")]
    private static partial Regex AllowedSubmoduleUrl();

    [GeneratedRegex(@"\A#!/bin/sh\r?\ncommand -v git-lfs >/dev/null 2>&1 \|\| \{ printf >&2 ""\\n%s\\n\\n"" ""[^""$`]*""; exit 2; \}\r?\ngit lfs (pre-push|post-checkout|post-commit|post-merge) ""\$@""\s*\z")]
    private static partial Regex StandardLfsHook();

    // A key and its value, as Git lists them. What a clone, Git LFS and submodules set up for
    // themselves, and nothing that can name a program, a hook or another settings file.
    [GeneratedRegex(@"\A((core\.(repositoryformatversion|filemode|bare|logallrefupdates|ignorecase|precomposeunicode|symlinks|longpaths|autocrlf|eol|safecrlf)|remote\.[^\r\n]*\.(url|fetch|pushurl|tagopt|prune|promisor|partialclonefilter|gh-resolved)|branch\.[^\r\n]*\.(remote|merge)|submodule\.[^\r\n]*\.(url|active|branch|ignore)|lfs\.repositoryformatversion|user\.(name|email))\n[^\r\n]*\z|filter\.lfs\.(clean|smudge|process)\ngit-lfs [^&|;<>`$\r\n]*\z|filter\.lfs\.required\n(true|false)\z|lfs\.[^\r\n]*\.access\n(basic|none|private|negotiate|ntlm)\z|lfs\.[^\r\n]*\.locksverify\n(true|false)\z|core\.fsmonitor\nfalse\z|core\.hookspath\n/dev/null\z|submodule\.[^\r\n]*\.update\n(checkout|none)\z)", RegexOptions.IgnoreCase)]
    private static partial Regex SafeWhileFinishing();

    [GeneratedRegex(@"^(https://github\.com/|ssh://git@github\.com/|git@github\.com:)", RegexOptions.IgnoreCase)]
    private static partial Regex AllowedLargeFileUrl();

    [GeneratedRegex(@"^(lfs\.url|lfs\.pushurl|remote\..*\.(lfsurl|lfspushurl))$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LargeFileUrlSetting();

    // What ssh itself says went wrong, which Git only follows with "Could not read from remote repository".
    [GeneratedRegex(@"(Permission denied|Host key verification failed|Connection (timed out|refused|closed|reset)|Could not resolve hostname|^ssh: |^kex_exchange)", RegexOptions.IgnoreCase)]
    private static partial Regex SshComplaint();

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex NotRepoSafe();
}
