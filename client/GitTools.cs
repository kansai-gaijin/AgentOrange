using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    NotFound,
    NotFoundOrNoAccess,
    GitCantReach,
    BadUrl,
    Offline,
    CheckFailed,
}

/// <summary>Account is the signed-in GitHub account this applies to, when there is one.</summary>
public sealed record RepoCheck(RepoAccess Access, string? Account = null)
{
    /// <summary>The check got a clear answer that this PC can't use the repo, as opposed to not getting one.</summary>
    public bool IsDenied => Access is RepoAccess.ReadOnly or RepoAccess.NotFound or RepoAccess.NotFoundOrNoAccess or RepoAccess.BadUrl;

    public (string Heading, string Description) Describe() => Access switch
    {
        RepoAccess.NoGitHubCli => ("The GitHub CLI is needed", "Agent Orange uses it to check your access. Install it from cli.github.com, then try again."),
        RepoAccess.NoGit => ("Git is needed", "Git wasn't found on this PC. Install it from git-scm.com, then try again."),
        RepoAccess.SignedOut => ("Sign in to GitHub", "This PC isn't signed in, so your access to this repo can't be checked."),
        RepoAccess.OtherAccount => ("Wrong GitHub account", $"Your active GitHub account can't work on this repo, but {Account} can."),
        RepoAccess.ReadOnly => ("You don't have access to this repo", $"{Account ?? "Your GitHub account"} can only read it, so you can't work on it. Get write access, or use a different repo."),
        RepoAccess.NotFound => ("This repo doesn't exist", "It may have been deleted or renamed, or your GitHub login may not be allowed to see it."),
        RepoAccess.NotFoundOrNoAccess => ("Can't find this repo", "It was deleted, or your GitHub account doesn't have access. GitHub doesn't say which. If you should have access, ask the owner."),
        RepoAccess.GitCantReach => ("Git on this PC can't reach this repo", "Check this PC's Git setup, such as its SSH keys, or run gh auth setup-git."),
        RepoAccess.BadUrl => ("This repo URL can't be used", "Change it in project settings."),
        RepoAccess.Offline => ("Couldn't reach GitHub", "This PC seems to be offline, and it hasn't had access to this repo before."),
        RepoAccess.Allowed => ("You have access", ""),
        _ => ("Couldn't check this repo", "GitHub gave an unexpected answer. Try again in a moment."),
    };
}

/// <summary>What a chosen folder is, as far as Git is concerned.</summary>
public sealed record FolderRepo(bool InsideRepo, bool IsRepoRoot, string? Origin, string? Problem = null)
{
    /// <summary>A folder inside someone else's repo would inherit that repo without really being it.</summary>
    public bool IsInsideAnotherRepo => InsideRepo && !IsRepoRoot;
}

/// <summary>What projects need from Git and the GitHub CLI.</summary>
public static partial class GitTools
{
    private const string TimedOut = "timeout";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(2);

    private sealed record CommandResult(int ExitCode, string Output, string Errors);

    /// <summary>A repo URL syncs to the server and other PCs, so it must not carry a password or token.</summary>
    public static string? RepoUrlProblem(string url)
    {
        if (ScpStyleRepo().IsMatch(url)) return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "ssh" or "git")
            || uri.Host.Length == 0)
        {
            return "Enter a repo URL, like https://github.com/you/game.git.";
        }

        var hasCredentials = uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || (uri.Scheme == "ssh" ? uri.UserInfo.Contains(':') : uri.UserInfo.Length > 0);
        return hasCredentials ? "Take the password or token out of that URL." : null;
    }

    /// <summary>Compares repos by host and path, so HTTPS and SSH forms of one repo match.</summary>
    public static bool SameRepo(string a, string b) => RepoIdentity(a) == RepoIdentity(b);

    /// <summary>The same for every form of a repo's URL.</summary>
    public static string RepoKey(string url) => RepoIdentity(url);

    /// <summary>
    /// Whether this PC can work on a repo. GitHub answers "not found" both for a repo
    /// that's gone and for a private one you can't see, so a repo outside your own
    /// accounts can only be reported as one or the other.
    /// </summary>
    public static async Task<RepoCheck> CheckRepoAccessAsync(string url)
    {
        // Synced URLs come from other PCs, so they're checked before anything runs them.
        if (RepoUrlProblem(url) is not null) return new RepoCheck(RepoAccess.BadUrl);

        var parts = RepoIdentity(url).Split('/');
        if (parts.Length != 3 || !parts[0].Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return await CheckGitCanReachAsync(url);
        }

        var slug = $"{parts[1]}/{parts[2]}";
        var view = await ViewRepoAsync(slug, null);
        if (view is null) return new RepoCheck(RepoAccess.NoGitHubCli);

        if (view.ExitCode == 0 && WritePermission().IsMatch(view.Output)) return await CheckGitCanReachAsync(url);

        var readOnly = view.ExitCode == 0;
        var notFound = SaysRepoMissing(view.Errors);
        if (!readOnly && !notFound)
        {
            if (LooksLikeNetworkFailure(view.Errors)) return new RepoCheck(RepoAccess.Offline);
            return new RepoCheck(SignedOutMessage().IsMatch(view.Errors) ? RepoAccess.SignedOut : RepoAccess.CheckFailed);
        }

        // Another signed-in account may be the one that can work on this repo.
        string? inconclusive = null;
        var accounts = await SignedInAccountsAsync();
        foreach (var account in accounts)
        {
            var token = await RunAsync("gh", Path.GetTempPath(), "auth", "token", "--hostname", "github.com", "--user", account);
            if (token is not { ExitCode: 0 }) continue;
            var viewed = await ViewRepoAsync(slug, token.Output.Trim());
            if (viewed is { ExitCode: 0 } && WritePermission().IsMatch(viewed.Output)) return new RepoCheck(RepoAccess.OtherAccount, account);

            // If an account couldn't answer, nothing can be said about the repo being gone or
            // who can write to it. An account with an expired login just can't work on it.
            var errors = viewed?.Errors ?? "";
            var answered = viewed is { ExitCode: 0 } || SaysRepoMissing(errors) || SignedOutMessage().IsMatch(errors);
            if (!answered) inconclusive = errors;
        }

        if (inconclusive is not null) return new RepoCheck(LooksLikeNetworkFailure(inconclusive) ? RepoAccess.Offline : RepoAccess.CheckFailed);
        if (readOnly) return new RepoCheck(RepoAccess.ReadOnly, await ActiveAccountAsync());

        var ownerIsYours = accounts.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
        return new RepoCheck(ownerIsYours ? RepoAccess.NotFound : RepoAccess.NotFoundOrNoAccess, await ActiveAccountAsync());
    }

    public static async Task<bool> SwitchAccountAsync(string account) =>
        await RunAsync("gh", Path.GetTempPath(), "auth", "switch", "--hostname", "github.com", "--user", account) is { ExitCode: 0 };

    /// <summary>Signing in is interactive, so it gets a terminal of its own.</summary>
    public static void OpenGitHubSignIn()
    {
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
        var prefix = await RunAsync("git", folder, "rev-parse", "--show-prefix");
        if (prefix is null) return null;
        if (prefix.ExitCode != 0)
        {
            return prefix.Errors.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? new FolderRepo(false, false, null)
                : new FolderRepo(false, false, null, GitProblem("Git couldn't read that folder", prefix.Errors));
        }
        if (prefix.Output.Trim().Length > 0) return new FolderRepo(true, false, null);

        var origin = await RunAsync("git", folder, "remote", "get-url", "origin");
        if (origin is null) return null;
        if (origin.ExitCode == 0) return new FolderRepo(true, true, WithoutCredentials(origin.Output.Trim()));
        return origin.Errors.Contains("No such remote", StringComparison.OrdinalIgnoreCase)
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
        return SameRepo(state.Origin, repoUrl) ? null : $"That folder uses a different repo: {state.Origin}";
    }

    /// <summary>A disconnected network drive can take a long time to answer, so this stays off the UI thread.</summary>
    public static Task<bool> FolderExistsAsync(string folder) => Task.Run(() => Directory.Exists(folder));

    /// <summary>True only when Git clearly says the folder's remote is some other repo.</summary>
    public static async Task<bool> FolderUsesOtherRepoAsync(string folder, string repoUrl)
    {
        if (!await FolderExistsAsync(folder)) return false;
        var state = await InspectFolderAsync(folder);
        return state?.Origin is { } origin && !SameRepo(origin, repoUrl);
    }

    /// <summary>Whether a folder can move to a new repo: readable, a repo's top or no repo at all, and not using an unrelated one.</summary>
    public static async Task<bool> FolderCanReplaceRepoAsync(string folder, string oldRepo)
    {
        if (!await FolderExistsAsync(folder)) return false;
        var state = await InspectFolderAsync(folder);
        return state is { Problem: null, IsInsideAnotherRepo: false }
            && (state.Origin is null || SameRepo(state.Origin, oldRepo));
    }

    /// <summary>Points a folder at a repo after the project moved, whatever remote it had. Its files don't change.</summary>
    public static async Task<GitResult> PointFolderAtRepoAsync(string folder, string repoUrl)
    {
        if (RepoUrlProblem(repoUrl) is { } problem) return new GitResult(false, problem);

        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");
        if (state.Origin is null) return await ConnectFolderAsync(folder, repoUrl);

        var changed = await RunAsync("git", folder, "remote", "set-url", "origin", repoUrl);
        return changed is { ExitCode: 0 }
            ? new GitResult(true, repoUrl)
            : new GitResult(false, "Couldn't connect that folder to the repo.");
    }

    /// <summary>Connects a folder with no remote to a repo, setting up Git in it if needed.</summary>
    public static async Task<GitResult> ConnectFolderAsync(string folder, string repoUrl)
    {
        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);

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
        var repoName = RepoName(projectName);
        if (repoName.Length == 0) return new GitResult(false, "That project name can't be used as a repo name. Rename the project, or enter a repo URL.");

        // Only the active account matters, because another one having expired fails this too.
        var signedIn = await RunAsync("gh", Path.GetTempPath(), "auth", "status", "--active", "--hostname", "github.com");
        if (signedIn is null) return new GitResult(false, "Creating a repo needs the GitHub CLI (gh), which wasn't found.");
        if (signedIn.ExitCode != 0)
        {
            return new GitResult(false, LooksLikeNetworkFailure(signedIn.Output + signedIn.Errors)
                ? "Couldn't reach GitHub. Try again when this PC is online."
                : "Sign in to the GitHub CLI first, with gh auth login.");
        }

        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");
        if (state.Origin is not null && (replacingRepo is null || !SameRepo(state.Origin, replacingRepo)))
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
            return new GitResult(false, created?.Errors.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true
                ? $"You already have a repo called {repoName}. Enter its URL, or rename this project."
                : "Couldn't create the repo on GitHub.");
        }

        // A PC that reached the old repo over SSH may have no other way in, so it stays on SSH.
        var keepSsh = replacingRepo is not null && UsesSsh(replacingRepo);
        var webUrl = CreatedRepoUrl().Match(created.Output).Value;
        if (state.Origin is not null || (keepSsh && webUrl.Length > 0))
        {
            var newOrigin = keepSsh ? $"git@github.com:{webUrl["https://github.com/".Length..]}.git" : $"{webUrl}.git";
            if (webUrl.Length == 0 || await RunAsync("git", folder, "remote", "set-url", "origin", newOrigin) is not { ExitCode: 0 })
            {
                return new GitResult(false, "The repo was created, but couldn't be linked to that folder.");
            }
        }

        var finalState = await InspectFolderAsync(folder);
        return finalState?.Origin is { } origin
            ? new GitResult(true, origin)
            : new GitResult(false, "The repo was created, but couldn't be linked to that folder.");
    }

    /// <summary>Clones into a new folder inside the chosen one, and returns the new folder's path.</summary>
    public static async Task<GitResult> CloneAsync(string url, string parentFolder, CancellationToken cancel)
    {
        if (RepoUrlProblem(url) is { } problem) return new GitResult(false, problem);
        if (FolderNameFor(url) is not { } name) return new GitResult(false, "That repo URL doesn't name a folder to clone into.");

        var destination = Path.Combine(parentFolder, name);

        // Cloning into a folder that only this clone owns means a failure can never remove
        // anything else, and the real name only appears once the clone is complete.
        var working = Path.Combine(parentFolder, $".{name}.cloning-{Guid.NewGuid():N}");
        try
        {
            if (await DestinationTakenAsync(destination)) return new GitResult(false, $"{destination} already exists and isn't empty.");

            var result = await RunAsync("git", parentFolder, null, ["clone", "--", url, working], cancel, await SshSettingsAsync());
            if (result is null) return new GitResult(false, "Git wasn't found on this PC.");

            if (result.ExitCode != 0)
            {
                await TryDeleteOwnedFolderAsync(working);
                if (cancel.IsCancellationRequested) return new GitResult(false, "The clone was cancelled.");
                var reason = result.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
                return new GitResult(false, string.IsNullOrEmpty(reason) ? "Couldn't clone that repo." : $"Couldn't clone that repo: {reason}");
            }

            await MoveIntoPlaceAsync(working, destination);
            return new GitResult(true, destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't finish a clone: {error.Message}");
            await TryDeleteOwnedFolderAsync(working);
            return new GitResult(false, $"Couldn't put the project in {parentFolder}.");
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
            catch (Exception error) when (attempt < 10 && error is IOException or UnauthorizedAccessException && !Directory.Exists(destination))
            {
                await Task.Delay(1000);
            }
        }
    }

    private static Task<bool> DestinationTakenAsync(string destination) =>
        Task.Run(() => Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any());

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
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
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

    // A port can mean a different server, and only GitHub is known to ignore case in its
    // paths, so anything else has to match exactly to count as the same repo.
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

        host = host.ToLowerInvariant();
        text = text.TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        if (host == "github.com") text = text.ToLowerInvariant();
        return host.Length > 0 ? $"{host}/{text}" : text;
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
        RunAsync("gh", Path.GetTempPath(), token, ["repo", "view", slug, "--json", "viewerPermission"]);

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

    // A GitHub login isn't enough, because Git may use a different key or credential on this PC.
    private static async Task<RepoCheck> CheckGitCanReachAsync(string url)
    {
        var result = await RunAsync("git", Path.GetTempPath(), null, ["ls-remote", "--", url, "HEAD"], default, await SshSettingsAsync());
        if (result is null) return new RepoCheck(RepoAccess.NoGit);
        if (result.ExitCode == 0) return new RepoCheck(RepoAccess.Allowed);
        return new RepoCheck(LooksLikeNetworkFailure(result.Errors) ? RepoAccess.Offline : RepoAccess.GitCantReach);
    }

    // There's no terminal to answer SSH's questions in, so it should fail rather than wait.
    // A custom SSH command from the person's Git settings is left alone.
    private static async Task<Dictionary<string, string>?> SshSettingsAsync()
    {
        var custom = await RunAsync("git", Path.GetTempPath(), "config", "--get", "core.sshCommand");
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

    private static string GitProblem(string summary, string errors)
    {
        var reason = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(reason) ? $"{summary}." : $"{summary}: {reason}";
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

    // Checks have a time limit and can't pop up a login. A clone can take as long as it
    // needs, and stops only when cancelled.
    private static async Task<CommandResult?> RunAsync(string program, string folder, string? gitHubToken, string[] arguments, CancellationToken cancel = default, Dictionary<string, string>? extraSettings = null)
    {
        if (FindOnPath(program) is not { } programPath) return null;
        if (!await FolderExistsAsync(folder)) return new CommandResult(-1, "", "That folder can't be reached.");

        var info = new ProcessStartInfo(programPath)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PATH"] = CurrentPath(); // gh runs git itself.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_ALLOW_PROTOCOL"] = "https:http:ssh:git";
        if (gitHubToken is not null) info.Environment["GH_TOKEN"] = gitHubToken;
        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings) info.Environment[key] = value;
        }

        var isClone = arguments.Length > 0 && arguments[0] == "clone";
        if (!isClone) info.Environment["GCM_INTERACTIVE"] = "never";

        var isCreate = arguments is ["repo", "create", ..];
        using var timeout = new CancellationTokenSource(isClone ? Timeout.InfiniteTimeSpan : isCreate ? CreateTimeout : CommandTimeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel);

        Process? process = null;
        try
        {
            process = await Task.Run(() => Process.Start(info));
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(stop.Token);
            var errors = process.StandardError.ReadToEndAsync(stop.Token);
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
            AppLog.Write($"Couldn't run {program}: {error.Message}");
            return null;
        }
        finally
        {
            process?.Dispose();
        }
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
    [GeneratedRegex(@"^[\w.-]+@[\w.-]+:[\w./-]+$")]
    private static partial Regex ScpStyleRepo();

    [GeneratedRegex(@"""viewerPermission""\s*:\s*""(ADMIN|MAINTAIN|WRITE)""")]
    private static partial Regex WritePermission();

    [GeneratedRegex(@"HTTP 401|gh auth login|not logged in", RegexOptions.IgnoreCase)]
    private static partial Regex SignedOutMessage();

    [GeneratedRegex(@"timeout|timed out|could not resolve host|no such host|dial tcp|could not connect|failed to connect|error connecting to|check your internet connection|connection (refused|reset|closed)|network is unreachable|tls handshake|proxyconnect|temporary failure", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkFailure();

    [GeneratedRegex(@"returned error: 4\d\d|\bnot found\b|permission denied|authentication failed|access denied", RegexOptions.IgnoreCase)]
    private static partial Regex ServerRefusal();

    [GeneratedRegex(@"'[^']*'|""[^""]*""|\S+://\S+")]
    private static partial Regex QuotedText();

    [GeneratedRegex(@"^[\w.-]+$")]
    private static partial Regex SafeFolderName();

    [GeneratedRegex(@"Logged in to github\.com account (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SignedInAccount();

    [GeneratedRegex(@"https://github\.com/[\w.-]+/[\w.-]+")]
    private static partial Regex CreatedRepoUrl();

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex NotRepoSafe();
}
