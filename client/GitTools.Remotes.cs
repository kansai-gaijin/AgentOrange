using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>The way a folder reaches its repo: which address works, and moving it to another.</summary>
public static partial class GitTools
{
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

    /// <summary>
    /// The form of a repo's URL that works from this PC, HTTPS or SSH. The synced URL may have
    /// come from a PC set up differently. Null if neither works.
    /// </summary>
    public static async Task<string?> ReachableFormAsync(string url, CancellationToken cancel = default) => (await BestFormAsync(url, cancel)).Form;

    // One that can push, or else one that can at least read, so what's chosen here passes the
    // guard's check of the same thing.
    // Opening a page can ask this more than once, and each can take a slow probe of every form,
    // so the answer is kept briefly. A failed one too, as it would only time out again.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Form, bool CanPush)> RecentForms = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan KeepFormsFor = TimeSpan.FromMinutes(1);

    private static async Task<(string? Form, bool CanPush)> BestFormAsync(string url, CancellationToken cancel = default)
    {
        var key = RepoKey(url);
        if (RecentForms.TryGetValue(key, out var recent) && DateTimeOffset.UtcNow - recent.At < KeepFormsFor) return (recent.Form, recent.CanPush);
        var found = await ProbeFormsAsync(url, cancel);
        if (!cancel.IsCancellationRequested) RecentForms[key] = (DateTimeOffset.UtcNow, found.Form, found.CanPush);
        return found;
    }

    private static async Task<(string? Form, bool CanPush)> ProbeFormsAsync(string url, CancellationToken cancel)
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
    // An organisation that requires SSH certificates only lets its members in under its own
    // name, so that goes first for its repos once this PC is seen to use it.
    private static async Task<string[]> FormsToTryAsync(string url)
    {
        var path = GitHubPath(url);
        var https = $"https://github.com/{path}.git";
        var ssh = $"git@github.com:{path}.git";
        var protocol = await RunAsync("gh", Path.GetTempPath(), "config", "get", "git_protocol", "--host", "github.com");
        string[] forms = protocol is { ExitCode: 0 } && protocol.Output.Trim() == "ssh" ? [ssh, https] : [https, ssh];
        return CertificateUserFor(path.Split('/')[0]) is { } user && CertificateUser().IsMatch(user) ? [$"{user}@github.com:{path}.git", .. forms] : forms;
    }

    /// <summary>Set up when the app starts. The org-ID name this PC uses for an owner's repos over SSH, kept on this PC.</summary>
    public static Func<string, string?> CertificateUserFor { get; set; } = _ => null;

    public static Action<string, string> RememberCertificateUser { get; set; } = (_, _) => { };

    private static readonly ConcurrentDictionary<string, byte> CertificateUsersAsked = new(StringComparer.OrdinalIgnoreCase);

    // Only once GitHub says the number is that organisation's, as a folder's remote can come from
    // anyone. Asked once a session for each, and not again once it's kept.
    private static async Task NoteCertificateUserAsync(string owner, string user)
    {
        try
        {
            if (string.Equals(CertificateUserFor(owner), user, StringComparison.Ordinal) || !CertificateUsersAsked.TryAdd($"{owner}|{user}", 0)) return;
            var id = await RunAsync("gh", Path.GetTempPath(), "api", $"orgs/{owner}", "--jq", ".id");
            if (id is { ExitCode: 0 } && $"org-{id.Output.Trim()}" == user) RememberCertificateUser(owner, user);
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            AppLog.Write($"Couldn't remember how this PC signs in to {owner}'s repos: {error.Message}");
        }
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

    // The same, keeping the login in a folder's HTTPS address, as an SSH alias is kept.
    private static string SameWayTo(FolderRepo state, string url) => WithLoginFrom(state.OriginWithLogin, SameWayTo(state.Origin!, url));

    // Another repo reached the way one of a remote's own addresses reaches its repo, login and all.
    private static string KeepingWayIn(string address, string url) => WithLoginFrom(address, SameWayTo(WithoutCredentials(address), url));

    private static string GitHubHost(string host) => host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase) ? "github.com" : host;

    // Only on the same host, as a login is never sent somewhere it wasn't meant for. Elsewhere,
    // access is just asked again.
    private static string WithLoginFrom(string? withLogin, string address)
    {
        if (withLogin is null
            || !Uri.TryCreate(withLogin, UriKind.Absolute, out var from) || from.Scheme != Uri.UriSchemeHttps || from.UserInfo.Length == 0
            || !Uri.TryCreate(address, UriKind.Absolute, out var to) || to.Scheme != Uri.UriSchemeHttps || !string.Equals(GitHubHost(from.Host), GitHubHost(to.Host), StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }
        return $"https://{from.UserInfo}@{to.Authority}{to.AbsolutePath}";
    }

    // Git's SSH form doesn't have to say github.com. A host can be an alias in the person's
    // SSH settings, which is common with more than one GitHub account.
    private static async Task<(string? GitHubForm, string? Problem)> GitHubFormAsync(string remote, string folder)
    {
        if (RepoUrlProblem(remote) is null)
        {
            if (CertificateAddress().Match(remote) is { Success: true } certificate)
            {
                var certificateUser = remote.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ? remote[6..remote.IndexOf('@')] : remote[..remote.IndexOf('@')];
                _ = NoteCertificateUserAsync(certificate.Groups[1].Value.Split('/')[0], certificateUser);
            }
            return (remote, null);
        }

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
        if (IsGitHubSshUser(user) && IsGitHubSshHost(host))
        {
            if (CertificateUser().IsMatch(user)) _ = NoteCertificateUserAsync(path.Split('/')[0], user);
            return ($"git@github.com:{path}", null);
        }

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

    /// <summary>
    /// Points a folder at a repo after the project moved. Its files don't change. Given the remote it was
    /// seen on, or empty for none, it's left as it is if that's changed since.
    /// </summary>
    public static async Task<GitResult> PointFolderAtRepoAsync(string folder, string repoUrl, string? onlyIfOnRepo = null)
    {
        if (RepoUrlProblem(repoUrl) is { } problem) return new GitResult(false, problem);

        var state = await InspectFolderAsync(folder);
        if (state is null) return new GitResult(false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return new GitResult(false, state.Problem);
        if (state.IsInsideAnotherRepo) return new GitResult(false, "That folder is inside another Git repo.");
        // Empty means it was seen with no remote, and anything else is the remote it was seen on.
        if (state.Origin is null && onlyIfOnRepo is { Length: > 0 }) return new GitResult(false, RemoteChangedMeanwhile);
        if (state.Origin is not null && onlyIfOnRepo is { Length: 0 }) return new GitResult(false, RemoteChangedMeanwhile);
        if (state.Origin is null) return await ConnectFolderAsync(folder, await ReachableFormAsync(repoUrl) ?? repoUrl);

        var newRemote = state.GitHubOrigin is not null ? SameWayTo(state, repoUrl) : await ReachableFormAsync(repoUrl) ?? repoUrl;
        return await MoveOriginAsync(folder, newRemote, onlyIfOnRepo is { Length: > 0 } ? onlyIfOnRepo : state.OriginToCompare!) is { } moveProblem
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
        // Its submodules and large files write the same config, and the two would collide on its lock.
        if (DownloadIsHeldElsewhere(folder)) return WaitingForDownload;
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
        async Task<string[]> MovedAsync(string[] addresses, bool fetchAddresses)
        {
            var moved = new List<string>();
            for (var i = 0; i < addresses.Length; i++)
            {
                var address = addresses[i];
                if (!SameRepo(await RepoOfAsync(address), oldRepo)) moved.Add(address);
                else moved.Add(fetchAddresses && i == 0 ? remote : KeepingWayIn(address, remote));
            }
            return moved.Distinct().ToArray();
        }

        var movedUrls = await MovedAsync(urls, true);
        var movedPushUrls = await MovedAsync(pushUrls, false);
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
        if (state.GitHubOrigin is not null) newOrigin = SameWayTo(state, webUrl);
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

    /// <summary>
    /// Whether a project going from one address to another is moving to a different repo, and not
    /// to a new name of the same one. Null if GitHub didn't say.
    /// </summary>
    public static async Task<bool?> IsAnotherRepoAsync(string before, string after)
    {
        if (SameRepo(before, after)) return false;
        var answers = await Task.WhenAll(CurrentRepoUrlAsync(before), CurrentRepoUrlAsync(after));
        if (!answers[0].Answered || !answers[1].Answered) return null;
        return !SameRepo(answers[0].Current, answers[1].Current);
    }

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
}
