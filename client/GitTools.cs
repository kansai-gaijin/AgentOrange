using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrange;

public sealed record GitResult(bool Worked, string Value);

public enum RepoAccess
{
    Allowed,
    NoGitHubCli,
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
}

/// <summary>Account is the signed-in GitHub account this applies to, when there is one.</summary>
public sealed record RepoCheck(RepoAccess Access, string? Account = null)
{
    /// <summary>The check got a clear answer that this PC can't use the repo, as opposed to not getting one.</summary>
    public bool IsDenied => Access is RepoAccess.ReadOnly or RepoAccess.Archived or RepoAccess.Restricted or RepoAccess.NotFound or RepoAccess.NotFoundOrNoAccess or RepoAccess.GitReadOnly or RepoAccess.BadUrl;

    public (string Heading, string Description) Describe() => Access switch
    {
        RepoAccess.NoGitHubCli => ("The GitHub CLI is needed", "Agent Orange uses it to check your access. Install it from cli.github.com, then try again."),
        RepoAccess.NoGit => ("Git is needed", "Git wasn't found on this PC. Install it from git-scm.com, then try again."),
        RepoAccess.SignedOut => ("Sign in to GitHub", "This PC isn't signed in, so your access to this repo can't be checked."),
        RepoAccess.OtherAccount => ("Wrong GitHub account", $"Your active GitHub account can't work on this repo, but {Account} can."),
        RepoAccess.ReadOnly => ("You don't have access to this repo", $"{Account ?? "Your GitHub account"} can only read it, so you can't work on it. Get write access, or use a different repo."),
        RepoAccess.Archived => ("This repo is archived", "GitHub won't accept changes to it. Unarchive it on GitHub, or change the URL in project settings."),
        RepoAccess.Restricted => ("GitHub is blocking this login from the repo", "An organization or token setting doesn't let this login see it, so trying again won't help. Authorize the login for the organization on GitHub, or sign in again with gh auth refresh."),
        RepoAccess.NotFound => ("This repo doesn't exist", "It may have been deleted or renamed, or your GitHub login may not be allowed to see it."),
        RepoAccess.NotFoundOrNoAccess => ("Can't find this repo", "It was deleted, or your GitHub account doesn't have access. GitHub doesn't say which. If you should have access, ask the owner."),
        RepoAccess.GitCantReach => ("Git on this PC can't reach this repo", "Your GitHub account has access, but Git doesn't get through from here. Check this PC's Git setup, such as its SSH keys or a firewall, or run gh auth setup-git."),
        RepoAccess.GitReadOnly => ("Git on this PC can only read this repo", "Your GitHub account can work on it, but the key or login Git uses here can't push to it. Check this PC's Git setup, such as a read-only deploy key, or run gh auth setup-git."),
        RepoAccess.BadUrl => ("This repo URL can't be used", "It has to be a GitHub repo, with no password or token in the URL. Change it in project settings."),
        RepoAccess.Offline => ("Couldn't reach GitHub", "This PC seems to be offline or blocked from GitHub, and it hasn't had access to this repo before."),
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

/// <summary>What projects need from Git and the GitHub CLI.</summary>
public static partial class GitTools
{
    private const string TimedOut = "timeout";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "ssh")
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !GitHubRepoPath().IsMatch(uri.AbsolutePath))
        {
            return notGitHub;
        }

        var hasCredentials = uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || (uri.Scheme == "ssh" ? uri.UserInfo.Contains(':') : uri.UserInfo.Length > 0);
        if (hasCredentials) return "Take the password or token out of that URL.";
        return uri.Scheme == "ssh" && uri.UserInfo != "git" ? notGitHub : null;
    }

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
        var notFound = SaysRepoMissing(view.Errors);
        if (!readOnly && !notFound)
        {
            if (LooksLikeNetworkFailure(view.Errors)) return new RepoCheck(RepoAccess.Offline);
            if (AccessBlocked().IsMatch(view.Errors)) return new RepoCheck(RepoAccess.Restricted, await ActiveAccountAsync());
            return new RepoCheck(SignedOutMessage().IsMatch(view.Errors) ? RepoAccess.SignedOut : RepoAccess.CheckFailed);
        }

        // Another signed-in account may be the one that can work on this repo.
        string? inconclusive = null;
        string? otherCanRead = null;
        var accounts = await SignedInAccountsAsync();

        // An account just answered, so finding none means the list couldn't be read.
        if (accounts.Count == 0) inconclusive = "";
        foreach (var account in accounts)
        {
            var token = await RunAsync("gh", Path.GetTempPath(), "auth", "token", "--hostname", "github.com", "--user", account);
            if (token is not { ExitCode: 0 })
            {
                // Couldn't ask this account, so it can't be ruled out. One with no login left can.
                var why = token?.Errors ?? "";
                if (!SignedOutMessage().IsMatch(why) && !why.Contains("no oauth token", StringComparison.OrdinalIgnoreCase)) inconclusive = why;
                continue;
            }
            var viewed = await ViewRepoAsync(slug, token.Output.Trim());
            if (viewed is { ExitCode: 0 } && WritePermission().IsMatch(viewed.Output)) return new RepoCheck(RepoAccess.OtherAccount, account);

            // If an account couldn't answer, nothing can be said about the repo being gone or
            // who can write to it. An account with an expired login just can't work on it.
            if (viewed is { ExitCode: 0 }) otherCanRead ??= account;
            var errors = viewed?.Errors ?? "";
            var answered = viewed is { ExitCode: 0 } || SaysRepoMissing(errors) || SignedOutMessage().IsMatch(errors) || AccessBlocked().IsMatch(errors);
            if (!answered) inconclusive = errors;
        }

        // GitHub clearly said read-only, and no other account has shown it can do better.
        if (readOnly) return new RepoCheck(RepoAccess.ReadOnly, await ActiveAccountAsync());

        // The active account was told it doesn't exist, but another one can see it. So it does
        // exist, and offering to replace it would move the project off a real repo.
        if (otherCanRead is not null) return new RepoCheck(RepoAccess.ReadOnly, otherCanRead);
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
        if (!SameRepo(state.OriginToCompare!, repoUrl)) return $"That folder uses a different repo: {state.Origin}";

        // Its work could otherwise go somewhere else entirely, without anyone seeing why. Git
        // pushes to every address listed, so all of them count.
        var push = await RunAsync("git", folder, "remote", "get-url", "--push", "--all", "origin");
        if (push is null) return "Git wasn't found on this PC.";
        if (push.ExitCode != 0) return GitProblem("Git couldn't read where that folder pushes to", push.Errors);
        foreach (var line in push.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pushRemote = WithoutCredentials(line);
            if (pushRemote == state.Origin) continue;

            var (gitHubForm, aliasProblem) = await GitHubFormAsync(pushRemote, folder);
            if (aliasProblem is not null) return aliasProblem;
            if (!SameRepo(gitHubForm ?? pushRemote, repoUrl)) return $"That folder pushes to a different repo: {pushRemote}";
        }
        return null;
    }

    /// <summary>
    /// Settings in a folder that make Git run a program or send a login somewhere else when it
    /// reaches a remote. A folder can come from anyone, as a zip or on a shared drive, so these
    /// are only used once the person has seen them. Null means that couldn't be found out,
    /// which has to be treated as not being safe to use them.
    /// </summary>
    public static async Task<FolderGitSettings?> FolderRunnableSettingsAsync(string folder)
    {
        if (!await FolderExistsAsync(folder)) return FolderGitSettings.None;

        // Asked for by scope, because a folder's settings can also be in a file only used per
        // worktree, or in one it includes. NUL-separated, so a value can't hide on a second line.
        var found = await RunAsync("git", folder, "config", "--show-scope", "--includes", "-z", "--get-regexp", FolderSettingsThatMatter);
        if (found is null) return null;
        if (found.ExitCode == 1) return FolderGitSettings.None;
        if (found.ExitCode != 0) return null;

        var parts = found.Output.Split('\0');
        var settings = new List<string>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (parts[i] is "local" or "worktree") settings.Add(parts[i + 1]);
        }
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

    private static string OneLine(string text) => WhiteSpaceRun().Replace(text, " ").Trim();

    private static string HideLogins(string text) =>
        TokenLike().Replace(LoginInUrl().Replace(text, "://(hidden)@"), "(hidden)");

    /// <summary>Set up when the app starts, because the answers are kept in its database.</summary>
    public static Func<string, string?> AllowedFolderSettings { get; set; } = _ => null;

    public static Action<string, string> RememberAllowedFolderSettings { get; set; } = (_, _) => { };

    public static bool AreAllowed(string folder, FolderGitSettings settings) =>
        settings.IsEmpty || (settings.CanBeApproved && AllowedFolderSettings(folder) == settings.Fingerprint);

    /// <summary>
    /// A folder whose SSH settings file needs ssh's own handling can't be understood until it's
    /// allowed to run, and until then it would just be refused. So this asks, when choosing one.
    /// </summary>
    public static async Task AskAboutUnreadableSshSettingsAsync(string folder)
    {
        if (await InspectFolderAsync(folder) is not { NeedsSettingsApproval: true }) return;
        if (await FolderRunnableSettingsAsync(folder) is not { IsEmpty: false, CanBeApproved: true } settings) return;

        if (Dialogs.Confirm($"To tell whether this folder's remote is on GitHub, its SSH settings have to run, and they can run programs or send your login somewhere else:\n\n{settings.Shown}\n\nOnly use them if you trust where this folder came from. Use them?"))
        {
            RememberAllowedFolderSettings(folder, settings.Fingerprint);
        }
    }

    internal const string CantTellSshSettings = "Couldn't tell whether";
    private const int MaxSettingsShown = 20;
    private const int MaxSettingShown = 600;

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
    public static async Task<string?> ReachableFormAsync(string url)
    {
        foreach (var form in await FormsToTryAsync(url))
        {
            if (await LsRemoteAsync(form, Path.GetTempPath()) is { ExitCode: 0 }) return form;
        }
        return null;
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

        if ((user.Length > 0 && user != "git") || !GitHubRepoPath().IsMatch($"/{path}") || !SafeSshHost().IsMatch(host)) return (null, null);
        if (user == "git" && IsGitHubSshHost(host)) return ($"git@github.com:{path}", null);

        var settings = await ReadSshSettingsAsync(host, folder);
        if (settings is not var (realHost, realUser)) return (null, $"{CantTellSshSettings} {host} is GitHub from the SSH settings in use. Check that they work, or use a remote that says github.com.");

        var effectiveUser = user.Length > 0 ? user : realUser;
        return (effectiveUser == "git" && realHost is not null && IsGitHubSshHost(realHost) ? $"git@github.com:{path}" : null, null);
    }

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
    public static async Task<GitResult> PointFolderAtRepoAsync(string folder, string repoUrl)
    {
        if (RepoUrlProblem(repoUrl) is { } problem) return new GitResult(false, problem);

        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");
        if (state.Origin is null) return await ConnectFolderAsync(folder, await ReachableFormAsync(repoUrl) ?? repoUrl);

        var newRemote = state.GitHubOrigin is not null ? SameWayTo(state.Origin, repoUrl) : await ReachableFormAsync(repoUrl) ?? repoUrl;
        return await MoveOriginAsync(folder, newRemote)
            ? new GitResult(true, repoUrl)
            : new GitResult(false, "Couldn't connect that folder to the repo.");
    }

    // A separate push address would otherwise keep sending work to the old repo.
    private static async Task<bool> MoveOriginAsync(string folder, string remote)
    {
        if (await RunAsync("git", folder, "config", "--replace-all", "remote.origin.url", remote) is not { ExitCode: 0 }) return false;

        var pushUrl = await RunAsync("git", folder, "config", "--get-all", "remote.origin.pushurl");
        if (pushUrl is not { ExitCode: 0 }) return true;
        return await RunAsync("git", folder, "config", "--replace-all", "remote.origin.pushurl", remote) is { ExitCode: 0 };
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

        if (await ReachableFormAsync(repoUrl) is not { } form)
        {
            return new GitResult(false, "Neither HTTPS nor SSH gets through from this PC, so nothing was changed.");
        }

        if (state.Origin is null) return await ConnectFolderAsync(folder, form);

        // Repair can only change the address. If that's already right, it can't help.
        if (state.Origin is not null && SameForm(state.Origin, form))
        {
            return new GitResult(false, "That folder already connects this way, and this way works from outside the folder, so its own Git settings are the likely problem. Nothing was changed.");
        }

        return await MoveOriginAsync(folder, form)
            ? new GitResult(true, form)
            : new GitResult(false, "Couldn't change that folder's connection.");
    }

    /// <summary>Connects a folder with no remote to a repo, setting up Git in it if needed.</summary>
    public static async Task<GitResult> ConnectFolderAsync(string folder, string repoUrl)
    {
        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");

        if (!state.InsideRepo)
        {
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
    public static async Task<GitResult> CreatePrivateRepoAsync(string projectName, string folder, string? replacingRepo = null)
    {
#if DEBUG
        // Trying something out must never leave a real repo on someone's account, or set up
        // Git in a folder for one that's never going to exist.
        if (Headless.Active) return new GitResult(false, "Headless mode doesn't create real repos.");
#endif
        var repoName = RepoName(projectName);
        if (repoName.Length == 0) return new GitResult(false, "That project name can't be used as a repo name. Rename the project, or enter a repo URL.");

        // Only the active account matters, because another one having expired fails this too.
        var signedIn = await RunAsync("gh", Path.GetTempPath(), "auth", "status", "--active", "--hostname", "github.com");
        if (signedIn is null) return new GitResult(false, "Creating a repo needs the GitHub CLI (gh), which wasn't found.");
        if (signedIn.ExitCode != 0)
        {
            if (signedIn.Errors.Contains("unknown flag", StringComparison.OrdinalIgnoreCase)) return new GitResult(false, "This PC's GitHub CLI is too old. Update it from cli.github.com, then try again.");

            return new GitResult(false, LooksLikeNetworkFailure(signedIn.Output + signedIn.Errors)
                ? "Couldn't reach GitHub. Try again when this PC is online."
                : "Sign in to the GitHub CLI first, with gh auth login.");
        }

        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");
        if (state.Origin is not null && (replacingRepo is null || !SameRepo(state.OriginToCompare!, replacingRepo)))
        {
            return new GitResult(false, $"That folder is already connected to a different repo: {state.Origin}");
        }

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
            var reason = created?.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            return new GitResult(false, string.IsNullOrEmpty(reason) ? "Couldn't create the repo on GitHub." : $"Couldn't create the repo on GitHub: {reason}");
        }

        // The folder keeps reaching GitHub the way it does now. With no remote yet, a PC whose
        // project was on SSH may have no other way in.
        var webUrl = CreatedRepoUrl().Match(created.Output).Value;
        if (state.Origin is not null && webUrl.Length == 0) return new GitResult(false, "The repo was created, but couldn't be linked to that folder.");

        string? newOrigin = null;
        if (state.GitHubOrigin is not null) newOrigin = SameWayTo(state.Origin!, webUrl);
        else if (state.Origin is not null) newOrigin = $"{webUrl}.git";
        else if (replacingRepo is not null && UsesSsh(replacingRepo) && webUrl.Length > 0) newOrigin = SameWayTo(replacingRepo, webUrl);

        if (newOrigin is not null && !await MoveOriginAsync(folder, newOrigin))
        {
            return new GitResult(false, "The repo was created, but couldn't be linked to that folder.");
        }

        var finalState = await InspectFolderAsync(folder);
        if (finalState?.Origin is null) return new GitResult(false, "The repo was created, but couldn't be linked to that folder.");

        // What syncs says which repo this is. How this PC's folder reaches it stays in the folder.
        var projectUrl = webUrl.Length > 0 ? $"{webUrl}.git" : finalState.OriginToCompare!;
        return RepoUrlProblem(projectUrl) is null
            ? new GitResult(true, projectUrl)
            : new GitResult(false, $"The repo was created, but its address couldn't be worked out from {finalState.Origin}.");
    }

    /// <summary>Where a clone of this repo would end up inside a folder, if the URL names one.</summary>
    public static string? CloneDestination(string url, string parentFolder) =>
        FolderNameFor(url) is { } name ? Path.Combine(parentFolder, name) : null;

    /// <summary>Whether a repo has anything in it, or null if that couldn't be found out.</summary>
    public static async Task<bool?> RepoHasContentAsync(string url)
    {
        var form = await ReachableFormAsync(url) ?? url;
        var heads = await RunAsync("git", Path.GetTempPath(), null, ["ls-remote", "--heads", "--", form], default, await SshSettingsAsync(Path.GetTempPath()));
        return heads is { ExitCode: 0 } ? heads.Output.Trim().Length > 0 : null;
    }

    public static Task<bool> FolderIsEmptyAsync(string folder) => Task.Run(() => Directory.Exists(folder) && !IsTaken(folder));

    public static Task<bool> FolderIsLinkAsync(string folder) => Task.Run(() => new DirectoryInfo(folder).LinkTarget is not null);

    /// <summary>
    /// Clones into a new folder inside the chosen one, or into exactFolder if it's empty, and
    /// returns the resulting folder's path. workingFolder says where it's working, so that can
    /// be cleaned up if the app closes first, and is told null once that folder holds a finished
    /// clone that must not be cleaned up.
    /// </summary>
    public static async Task<GitResult> CloneAsync(string url, string parentFolder, CancellationToken cancel, Action<string?> workingFolder, string? exactFolder = null, Action<string>? progress = null)
    {
        if (RepoUrlProblem(url) is { } problem) return new GitResult(false, problem);

        // A repo inside a repo gets picked up by the outer one's next commit.
        if (await InspectFolderAsync(parentFolder) is { InsideRepo: true }) return new GitResult(false, $"{parentFolder} is inside a Git repo, and a project can't be cloned into another repo. Choose somewhere else.");
        if ((exactFolder ?? CloneDestination(url, parentFolder)) is not { } destination) return new GitResult(false, "That repo URL doesn't name a folder to clone into.");

        var form = await ReachableFormAsync(url) ?? url;

        // Cloning into a folder that only this clone owns means a failure can never remove
        // anything else, and the real name only appears once the clone is complete.
        // Short, because a long name here can push a deep repo past Windows' path limit.
        var working = Path.Combine(parentFolder, $".ao-{Guid.NewGuid():N}"[..12]);
        workingFolder(working);
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

            var result = await RunAsync("git", parentFolder, null, ["clone", "--progress", "--", form, working], cancel, await SshSettingsAsync(Path.GetTempPath()), ShowStage);
            if (result is null) return new GitResult(false, "Git wasn't found on this PC.");

            if (result.ExitCode != 0)
            {
                await TryDeleteOwnedFolderAsync(working);
                if (cancel.IsCancellationRequested) return new GitResult(false, "The clone was cancelled.");
                var reason = GitFailureReason(result.Errors);
                return new GitResult(false, reason.Length == 0 ? "Couldn't clone that repo." : $"Couldn't clone that repo: {reason}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't start a clone: {error.Message}");
            await TryDeleteOwnedFolderAsync(working);
            return new GitResult(false, exactFolder is null
                ? $"Couldn't put the project in {parentFolder}."
                : $"Couldn't clone into {exactFolder}. The clone is prepared beside it, in {parentFolder}, and that can't be written to. Choose a folder somewhere it can.");
        }

        // From here it's a finished clone, which could have taken hours, so it's never deleted,
        // including by the cleanup at the next start if the app closes during this.
        workingFolder(null);
        try
        {
            await MoveIntoPlaceAsync(working, destination);
            return new GitResult(true, destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't name a finished clone: {error.Message}");
            return new GitResult(false, $"The clone finished, but it couldn't be named {destination}. It's kept at {await KeepVisibleAsync(working, destination)}. You can rename it and select it.");
        }
    }

    // Git's real complaint, not its last line, which is often advice for a folder now gone.
    private static string GitFailureReason(string errors)
    {
        var lines = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failures = lines.Where(line => line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
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
    /// folder named by CloneAsync. False means it's still there, or its drive isn't plugged
    /// in, so it should be tried again next time.
    /// </summary>
    public static async Task<bool> RemoveLeftoverCloneAsync(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !CloneWorkingFolderName().IsMatch(Path.GetFileName(folder))) return true;

        var parent = Path.GetDirectoryName(folder);
        if (parent is null || !await FolderExistsAsync(parent)) return false;

        await TryDeleteOwnedFolderAsync(folder);
        return !await FolderExistsAsync(folder);
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
            catch (Exception error) when (attempt < 10 && error is IOException or UnauthorizedAccessException && !IsTaken(destination))
            {
                await Task.Delay(1000);
            }
        }
    }

    private static Task<bool> DestinationTakenAsync(string destination) => Task.Run(() => IsTaken(destination));

    private static bool IsTaken(string destination)
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
            if (viaFolder is { ExitCode: 0 }) return new RepoCheck(await GitRefusesPushAsync("origin", folder!) ? RepoAccess.GitReadOnly : RepoAccess.Allowed);

            // No answer this way, such as a blocked SSH port, isn't being offline if there's
            // another way in. Saying so is what offers to repair the folder.
            var failure = GitFailure(viaFolder);
            if (failure == RepoAccess.Offline && await TryFormsAsync(url) != RepoAccess.Offline) failure = RepoAccess.GitCantReach;
            return new RepoCheck(failure);
        }

        return new RepoCheck(await TryFormsAsync(url));
    }

    // Only offline if nothing answered at all. Being turned away by one form, while another
    // gets no answer, means something on this PC needs fixing, and it should get the chance.
    private static async Task<RepoAccess> TryFormsAsync(string url)
    {
        var turnedAway = false;
        var readOnly = false;
        foreach (var form in await FormsToTryAsync(url))
        {
            var attempt = await LsRemoteAsync(form, Path.GetTempPath());
            if (attempt is { ExitCode: 0 })
            {
                if (!await GitRefusesPushAsync(form, null)) return RepoAccess.Allowed;
                readOnly = true;
                continue;
            }
            turnedAway |= GitFailure(attempt) != RepoAccess.Offline;
        }
        return readOnly ? RepoAccess.GitReadOnly : turnedAway ? RepoAccess.GitCantReach : RepoAccess.Offline;
    }

    // Reading works with a read-only deploy key too, so this asks whether Git could push. It's a
    // dry run of deleting a branch that doesn't exist, which GitHub has to authorize the same way
    // and which can't change anything. Only a clear refusal counts, so a blip can't lock anyone out.
    private static async Task<bool> GitRefusesPushAsync(string remote, string? folder)
    {
        // Git only pushes from inside a repo, and folder is the one whose settings are allowed.
        var scratch = folder is null ? Path.Combine(Path.GetTempPath(), $".ao-{Guid.NewGuid():N}"[..12]) : null;
        try
        {
            if (scratch is not null)
            {
                await Task.Run(() => Directory.CreateDirectory(scratch));
                if (await RunAsync("git", scratch, "init", "-q") is not { ExitCode: 0 }) return false;
            }

            var where = folder ?? scratch!;
            var result = await RunAsync("git", where, null, ["push", "--dry-run", "--no-verify", "--", remote, $":refs/heads/agent-orange-check-{Guid.NewGuid():N}"], default, await SshSettingsAsync(where));
            return result is { ExitCode: not 0 } && PushRefusal().IsMatch(result.Errors);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't check whether Git can push: {error.Message}");
            return false;
        }
        finally
        {
            if (scratch is not null) await TryDeleteOwnedFolderAsync(scratch);
        }
    }

    private static RepoAccess GitFailure(CommandResult? result) =>
        result is not null && LooksLikeNetworkFailure(result.Errors) ? RepoAccess.Offline : RepoAccess.GitCantReach;

    private static async Task<CommandResult?> LsRemoteAsync(string remote, string folder) =>
        await RunAsync("git", folder, null, ["ls-remote", "--", remote, "HEAD"], default, await SshSettingsAsync(folder));

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
            var trimmed = folder.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed)) continue;
            var candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool UsesSsh(string url) =>
        ScpStyleRepo().IsMatch(url) || url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

    private static string RepoName(string projectName) =>
        NotRepoSafe().Replace(projectName.Trim().ToLowerInvariant(), "-").Trim('-');

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
        info.Environment["GIT_ALLOW_PROTOCOL"] = "https:ssh";
        if (gitHubToken is not null) info.Environment["GH_TOKEN"] = gitHubToken;
        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings) info.Environment[key] = value;
        }

        // Never a login window: the person has a GitHub CLI login by the time any of this runs.
        var isClone = arguments.Length > 0 && arguments[0] == "clone";
        info.Environment["GCM_INTERACTIVE"] = "never";

        var isCreate = arguments is ["repo", "create", ..];
        using var timeout = new CancellationTokenSource(isClone ? Timeout.InfiniteTimeSpan : isCreate ? CreateTimeout : CommandTimeout);
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
            return new CommandResult(process.ExitCode, await output, await errors);
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

    // Git rewrites a status line by ending it with a return, so only finished lines are kept.
    private static async Task<string> ReadErrorsWithStatusAsync(StreamReader reader, Action<string> statusLine, CancellationToken cancel)
    {
        var kept = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancel)) > 0)
        {
            foreach (var character in buffer.AsSpan(0, count))
            {
                if (character is not ('\r' or '\n'))
                {
                    line.Append(character);
                    continue;
                }

                if (character == '\r') statusLine(line.ToString());
                else kept.Append(line).Append('\n');
                line.Clear();
            }
        }
        return kept.Append(line).ToString();
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

    [GeneratedRegex(@"^git@github\.com:[\w.-]+/[\w.-]+/?\z", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubScpRepo();

    [GeneratedRegex(@"^/[\w.-]+/[\w.-]+/?\z")]
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

    [GeneratedRegex(@"read.only|Permission to \S+ denied|error: 403", RegexOptions.IgnoreCase)]
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

    [GeneratedRegex(@"timeout|timed out|could not resolve host|no such host|dial tcp|could not connect|failed to connect|error connecting to|check your internet connection|connection (was )?(refused|reset|closed)|returned error: 5\d\d|remote end hung up|empty reply from server|network is unreachable|tls handshake|proxyconnect|temporary failure", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkFailure();

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

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex NotRepoSafe();
}
