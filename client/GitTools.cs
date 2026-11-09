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
    GitOffline,
}

/// <summary>Account is the signed-in GitHub account this applies to, when there is one.</summary>
public sealed record RepoCheck(RepoAccess Access, string? Account = null)
{
    /// <summary>The check got a clear answer that this PC can't use the repo, as opposed to not getting one.</summary>
    public bool IsDenied => Access is RepoAccess.ReadOnly or RepoAccess.Archived or RepoAccess.Restricted or RepoAccess.NotFound or RepoAccess.NotFoundOrNoAccess or RepoAccess.GitReadOnly or RepoAccess.BadUrl;

    /// <summary>GitHub said this account can see the repo, so what it calls the repo now can be trusted.</summary>
    public bool GitHubSeesRepo => Access is RepoAccess.Allowed or RepoAccess.ReadOnly or RepoAccess.GitCantReach or RepoAccess.GitReadOnly or RepoAccess.GitPushUnchecked or RepoAccess.GitOffline;

    /// <summary>Whether the page shows the folder at all, which is when it's worth looking through.</summary>
    public bool ShowsFolder => Access is RepoAccess.Allowed or RepoAccess.Offline or RepoAccess.CheckFailed or RepoAccess.GitPushUnchecked or RepoAccess.GitOffline or RepoAccess.GitCantReach or RepoAccess.GitReadOnly;

    /// <summary>No clear answer, so a PC that has had access, or a project made here, can keep working.</summary>
    public bool IsUnanswered => Access is RepoAccess.Offline or RepoAccess.CheckFailed or RepoAccess.GitPushUnchecked or RepoAccess.GitOffline;

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
        RepoAccess.GitOffline => ("Git on this PC can't get through", "GitHub says you have access, but Git here couldn't connect to it. Check this PC's network for Git, such as a proxy or a blocked SSH port, then try again."),
        RepoAccess.GitPushUnchecked => ("Couldn't check pushing", "Your GitHub account has access, but Git on this PC couldn't finish checking whether it can push to this repo. Try again in a moment, and if this keeps happening, check this PC's Git setup, such as a proxy or firewall."),
        RepoAccess.Allowed => ("You have access", ""),
        _ => ("Couldn't check this repo", "GitHub gave an unexpected answer. Try again in a moment."),
    };
}

/// <summary>A folder's own Git settings that matter. The fingerprint says whether they've changed, without keeping them.</summary>
public sealed record FolderGitSettings(string Shown, string Fingerprint, bool CanBeApproved = true, bool ChangesConnection = false)
{
    public static readonly FolderGitSettings None = new("", "");

    public bool IsEmpty => Fingerprint.Length == 0;
}

/// <summary>What finishing a download in a folder would use, and why it can't be allowed if it can't.</summary>
public sealed record FinishLook(FolderGitSettings Settings, string? Refused);

/// <summary>What a chosen folder is, as far as Git is concerned.</summary>
public sealed record FolderRepo(bool InsideRepo, bool IsRepoRoot, string? Origin, string? Problem = null, string? GitHubOrigin = null)
{
    /// <summary>
    /// The remote as Git has it, with any login in it. Never shown, logged or saved anywhere but
    /// that folder's own remote, and internal so a record's text form leaves it out.
    /// </summary>
    internal string? OriginWithLogin { get; init; }

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

    public static string OneLine(string text) => WhiteSpaceRun().Replace(text, " ").Trim();

    public static string HideLogins(string text) =>
        TokenLike().Replace(LoginInUrl().Replace(text, "://(hidden)@"), "(hidden)");

    /// <summary>Set up when the app starts, because this has to outlast the app closing mid-download.</summary>
    public static Action<string, bool> RememberUnfinishedDownload { get; set; } = (_, _) => { };

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
        var startsGitInFolder = program == "git" || arguments.Contains("--source");
        if (startsGitInFolder && folder != Path.GetTempPath() && await GitFolderSafety.NetworkPathProblemAsync(folder, ReadSettingsFileAsync) is { } networkProblem)
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

        // Settings from a `git -c` or a hook that started this app would otherwise reach everything
        // it runs. Headless checks set them on purpose, to stand in for a broken connection.
        var keepsGivenSettings = false;
#if DEBUG
        keepsGivenSettings = Headless.Active;
#endif
        if (!keepsGivenSettings)
        {
            foreach (var key in info.Environment.Keys.Where(key => key is "GIT_CONFIG_PARAMETERS" or "GIT_CONFIG_COUNT" || key.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal) || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)).ToList())
            {
                info.Environment.Remove(key);
            }
        }
        // Git looks for a repo upward from where it starts, and above Temp can be a home folder kept
        // as a repo, whose settings would then apply. Temp itself is only ever the app's own.
        if (folder == Path.GetTempPath() && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { } aboveTemp)
        {
            info.Environment["GIT_CEILING_DIRECTORIES"] = aboveTemp;
        }

        // gh runs Git itself, such as in the folder for repo create --source, so it gets the same
        // settings given to Git above, in the form Git reads from its environment.
        if (program == "gh")
        {
            var given = info.Environment.TryGetValue("GIT_CONFIG_COUNT", out var countText) && int.TryParse(countText, out var count) ? count : 0;
            foreach (var (key, value) in new[] { ("safe.bareRepository", "explicit"), ("http.emptyAuth", "false") })
            {
                info.Environment[$"GIT_CONFIG_KEY_{given}"] = key;
                info.Environment[$"GIT_CONFIG_VALUE_{given}"] = value;
                given++;
            }
            info.Environment["GIT_CONFIG_COUNT"] = given.ToString();
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

    // Settings that change how Git gets to a server: the SSH program, a proxy, or another address.
    [GeneratedRegex(@"^(core\.(sshcommand|gitproxy)|http\.|url\..*\.(insteadof|pushinsteadof)$|remote\..*\.(proxy|vcs)$)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionSetting();

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

    [GeneratedRegex(@"^\.ao-[0-9a-f]{8}\z")]
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
