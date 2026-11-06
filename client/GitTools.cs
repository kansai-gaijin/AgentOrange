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
    SignedOut,
    OtherAccount,
    ReadOnly,
    NotFound,
    NotFoundOrNoAccess,
    GitCantReach,
    CantCheck,
}

/// <summary>Account is the signed-in GitHub account this applies to, when there is one.</summary>
public sealed record RepoCheck(RepoAccess Access, string? Account = null);

/// <summary>What projects need from Git and the GitHub CLI.</summary>
public static partial class GitTools
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

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

        var hasCredentials = uri.Scheme == "ssh" ? uri.UserInfo.Contains(':') : uri.UserInfo.Length > 0;
        return hasCredentials ? "Take the password or token out of that URL." : null;
    }

    /// <summary>The folder's existing remote, if it has one.</summary>
    public static async Task<string?> OriginOfAsync(string folder)
    {
        var result = await RunAsync("git", folder, "remote", "get-url", "origin");
        return result is { ExitCode: 0 } ? result.Output.Trim() : null;
    }

    /// <summary>
    /// Whether this PC can work on a repo. GitHub answers "not found" both for a repo
    /// that's gone and for a private one you can't see, so a repo outside your own
    /// accounts can only be reported as one or the other.
    /// </summary>
    public static async Task<RepoCheck> CheckRepoAccessAsync(string url)
    {
        var parts = RepoIdentity(url).Split('/');
        if (parts.Length != 3 || !parts[0].Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new RepoCheck(await GitCanReachAsync(url) ? RepoAccess.Allowed : RepoAccess.GitCantReach);
        }

        var slug = $"{parts[1]}/{parts[2]}";
        var view = await ViewRepoAsync(slug, null);
        if (view is null) return new RepoCheck(RepoAccess.NoGitHubCli);

        if (view.ExitCode == 0)
        {
            if (!WritePermission().IsMatch(view.Output)) return new RepoCheck(RepoAccess.ReadOnly, await ActiveAccountAsync());
            return new RepoCheck(await GitCanReachAsync(url) ? RepoAccess.Allowed : RepoAccess.GitCantReach);
        }

        if (view.Errors.Contains("Could not resolve to a Repository", StringComparison.OrdinalIgnoreCase))
        {
            var accounts = await SignedInAccountsAsync();
            foreach (var account in accounts)
            {
                var token = await RunAsync("gh", Path.GetTempPath(), "auth", "token", "--hostname", "github.com", "--user", account);
                if (token is not { ExitCode: 0 }) continue;
                var viewed = await ViewRepoAsync(slug, token.Output.Trim());
                if (viewed is { ExitCode: 0 }) return new RepoCheck(RepoAccess.OtherAccount, account);
            }

            var ownerIsYours = accounts.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
            return new RepoCheck(ownerIsYours ? RepoAccess.NotFound : RepoAccess.NotFoundOrNoAccess);
        }

        if (SignedOutMessage().IsMatch(view.Errors)) return new RepoCheck(RepoAccess.SignedOut);
        return new RepoCheck(RepoAccess.CantCheck);
    }

    public static async Task<bool> SwitchAccountAsync(string account) =>
        await RunAsync("gh", Path.GetTempPath(), "auth", "switch", "--hostname", "github.com", "--user", account) is { ExitCode: 0 };

    /// <summary>Signing in is interactive, so it gets a terminal of its own.</summary>
    public static void OpenGitHubSignIn()
    {
        var info = new ProcessStartInfo("cmd.exe", "/d /k gh auth login") { UseShellExecute = false, CreateNoWindow = false };
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"Couldn't open the GitHub sign-in: {error.Message}");
        }
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
    private static async Task<bool> GitCanReachAsync(string url) =>
        await RunAsync("git", Path.GetTempPath(), "ls-remote", url, "HEAD") is { ExitCode: 0 };

    /// <summary>Checks a folder really holds this project's repo. Returns a problem to show, or null if it does.</summary>
    public static async Task<string?> FolderProblemAsync(string folder, string repoUrl)
    {
        if (!Directory.Exists(folder)) return "That folder doesn't exist.";
        if (repoUrl.Length == 0) return null;

        var origin = await OriginOfAsync(folder);
        if (origin is null) return "That folder isn't connected to a repo, so it can't be this project.";
        return SameRepo(origin, repoUrl) ? null : $"That folder uses a different repo: {WithoutCredentials(origin)}";
    }

    /// <summary>Compares repos by host and path, so HTTPS and SSH forms of one repo match.</summary>
    public static bool SameRepo(string a, string b) =>
        string.Equals(RepoIdentity(a), RepoIdentity(b), StringComparison.OrdinalIgnoreCase);

    private static string RepoIdentity(string url)
    {
        var text = url.Trim();
        var scp = ScpStyleRepo().IsMatch(text);
        if (scp)
        {
            var at = text.IndexOf('@');
            var colon = text.IndexOf(':');
            text = $"{text[(at + 1)..colon]}/{text[(colon + 1)..]}";
        }
        else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            text = uri.Host + uri.AbsolutePath;
        }

        text = text.TrimEnd('/');
        return text.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
    }

    // A remote can have a token in it, which must not be shown on screen.
    private static string WithoutCredentials(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0 ? RepoIdentity(url) : url;

    /// <summary>Creates a private GitHub repo for the folder, turning the folder into a Git repo if needed.</summary>
    public static async Task<GitResult> CreatePrivateRepoAsync(string projectName, string folder)
    {
        var repoName = RepoName(projectName);
        if (repoName.Length == 0) return new GitResult(false, "That project name can't be used as a repo name.");

        var signedIn = await RunAsync("gh", folder, "auth", "status");
        if (signedIn is null) return new GitResult(false, "Creating a repo needs the GitHub CLI (gh), which wasn't found.");
        if (signedIn.ExitCode != 0) return new GitResult(false, "Sign in to the GitHub CLI first, with gh auth login.");

        var root = await RunAsync("git", folder, "rev-parse", "--show-toplevel");
        if (root is null) return new GitResult(false, "Git wasn't found.");
        if (root.ExitCode != 0)
        {
            var init = await RunAsync("git", folder, "init");
            if (init is not { ExitCode: 0 }) return new GitResult(false, "Couldn't set up Git in that folder.");
        }
        else if (!SameFolder(root.Output.Trim(), folder))
        {
            return new GitResult(false, "That folder is inside another Git repo.");
        }

        // A folder whose old repo was deleted still has a remote, which gets pointed at the new repo.
        var oldOrigin = await OriginOfAsync(folder);
        var created = oldOrigin is null
            ? await RunAsync("gh", folder, "repo", "create", repoName, "--private", "--source", folder, "--remote", "origin")
            : await RunAsync("gh", folder, "repo", "create", repoName, "--private");
        if (created is not { ExitCode: 0 })
        {
            var reason = created?.Errors.Trim() ?? "";
            return new GitResult(false, reason.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                ? $"You already have a repo called {repoName}. Enter its URL, or rename this project."
                : "Couldn't create the repo on GitHub.");
        }

        if (oldOrigin is not null)
        {
            var newUrl = CreatedRepoUrl().Match(created.Output).Value;
            if (newUrl.Length == 0 || await RunAsync("git", folder, "remote", "set-url", "origin", $"{newUrl}.git") is not { ExitCode: 0 })
            {
                return new GitResult(false, "The repo was created, but couldn't be linked to that folder.");
            }
        }

        return await OriginOfAsync(folder) is { } origin
            ? new GitResult(true, origin)
            : new GitResult(false, "The repo was created, but couldn't be linked to that folder.");
    }

    /// <summary>Clones into a new folder inside the chosen one, and returns the new folder's path.</summary>
    public static async Task<GitResult> CloneAsync(string url, string parentFolder)
    {
        var name = FolderNameFor(url);
        var destination = Path.Combine(parentFolder, name);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            return new GitResult(false, $"{destination} already exists and isn't empty.");
        }

        var result = await RunAsync("git", parentFolder, "clone", url, destination);
        if (result is null) return new GitResult(false, "Git wasn't found.");
        return result.ExitCode == 0
            ? new GitResult(true, destination)
            : new GitResult(false, "Couldn't clone that repo. Check the URL and that this PC can access it.");
    }

    private static string RepoName(string projectName) =>
        NotRepoSafe().Replace(projectName.Trim().ToLowerInvariant(), "-").Trim('-');

    private static string FolderNameFor(string url)
    {
        var last = url.TrimEnd('/').Split('/', ':').Last();
        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static Task<CommandResult?> RunAsync(string program, string folder, params string[] arguments) =>
        RunAsync(program, folder, null, arguments);

    // Checks shouldn't wait forever or pop up a login window, but a clone can take a long time.
    private static async Task<CommandResult?> RunAsync(string program, string folder, string? gitHubToken, string[] arguments)
    {
        var info = new ProcessStartInfo(program)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0"; // There's no terminal to answer in.
        if (gitHubToken is not null) info.Environment["GH_TOKEN"] = gitHubToken;

        var isClone = arguments.Length > 0 && arguments[0] == "clone";
        if (!isClone) info.Environment["GCM_INTERACTIVE"] = "never";
        using var timeout = new CancellationTokenSource(isClone ? Timeout.InfiniteTimeSpan : CommandTimeout);

        Process? started = null;
        try
        {
            using var process = Process.Start(info);
            started = process;
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(process.ExitCode, await output, await errors);
        }
        catch (OperationCanceledException)
        {
            started?.Kill(entireProcessTree: true);
            return new CommandResult(-1, "", "timeout");
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            AppLog.Write($"Couldn't run {program}: {error.Message}");
            return null;
        }
    }

    // The short form Git uses for SSH, like git@github.com:you/game.git.
    [GeneratedRegex(@"^[\w.-]+@[\w.-]+:[\w./-]+$")]
    private static partial Regex ScpStyleRepo();

    [GeneratedRegex(@"""viewerPermission""\s*:\s*""(ADMIN|MAINTAIN|WRITE)""")]
    private static partial Regex WritePermission();

    [GeneratedRegex(@"HTTP 401|gh auth login|not logged in", RegexOptions.IgnoreCase)]
    private static partial Regex SignedOutMessage();

    [GeneratedRegex(@"Logged in to github\.com account (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SignedInAccount();

    [GeneratedRegex(@"https://github\.com/[\w.-]+/[\w.-]+")]
    private static partial Regex CreatedRepoUrl();

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex NotRepoSafe();
}
