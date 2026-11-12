using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>Asks GitHub and Git whether this PC can work on a repo.</summary>
public static partial class GitTools
{
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
        if (view.ExitCode == 0 && WritePermission().IsMatch(view.Output))
        {
            var viaGit = await CheckGitCanReachAsync(url, folder, useFolderSettings);
            var id = RepoIdField().Match(view.Output) is { Success: true } found ? found.Groups[1].Value : null;
            return (viaGit.Access == RepoAccess.Offline ? new RepoCheck(RepoAccess.GitOffline) : viaGit) with { RepoId = id };
        }

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
        if (inconclusive is not null) return new RepoCheck(RepoAccess.CheckFailed);

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

    private static Task<CommandResult?> ViewRepoAsync(string slug, string? token) =>
        RunAsync("gh", Path.GetTempPath(), token, ["repo", "view", slug, "--json", "viewerPermission,isArchived,id"]);

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
                // Its access to this repo is still asked, at the repo's own address, reached the way
                // the folder fetches, as a URL doesn't go by the folder's push addresses.
                var pushesElsewhere = await PushAddressElsewhereAsync(folder!, "origin", url) is not null;
                return new RepoCheck(await GitPushAnswerAsync(pushesElsewhere ? SameWayTo(state, url) : "origin", folder!) switch
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

    /// <summary>
    /// The push check's scratch folder is removed when it's done, but not if the app crashes during
    /// it. One left from a day ago can't be in use by another window. Only an empty git init is
    /// taken to be one, so a clone working in Temp under the same kind of name is left alone.
    /// </summary>
    public static void RemoveLeftoverPushChecks()
    {
        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(Path.GetTempPath(), ".ao-*").ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var folder in folders)
        {
            try
            {
                if (!CloneWorkingFolderName().IsMatch(Path.GetFileName(folder))) continue;
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) < TimeSpan.FromDays(1)) continue;
                var git = Path.Combine(folder, ".git");
                var onlyGit = Directory.EnumerateFileSystemEntries(folder).All(entry => Path.GetFileName(entry) == ".git");
                if (!onlyGit || !Directory.Exists(git) || SshConfigFile.IsLink(folder) || SshConfigFile.IsLink(git)) continue;
                var objects = Path.Combine(git, "objects");
                if (Directory.Exists(objects) && Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).Any()) continue;
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Couldn't remove a push check left in Temp: {error.Message}");
            }
        }
    }

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
            if (folder is not null && remote.Contains(':') && remote.Contains('=')) return PushAnswer.CantTell;
            // An address rather than a remote's name has no mirror setting to turn off, and goes there
            // rather than wherever the folder's own settings rewrite it to. A folder rule for that exact
            // address would still win, as Git takes the first of two equal matches and reads the
            // folder's first, but those settings only run once the person has allowed them.
            List<string> push = folder is null ? ["-c", "push.gpgSign=false"]
                : remote.Contains(':') ? ["-c", $"url.{remote}.pushInsteadOf={remote}", "-c", "push.gpgSign=false"]
                : ["-c", $"remote.{remote}.mirror=false", "-c", "push.gpgSign=false"];
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
}
