using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>Getting a project onto this PC: cloning, then its submodules and large files.</summary>
public static partial class GitTools
{
    /// <summary>Where a clone of this repo would end up inside a folder, if the URL names one.</summary>
    public static string? CloneDestination(string url, string parentFolder) =>
        FolderNameFor(url) is { } name ? Path.Combine(parentFolder, name) : null;

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
                var name = DownloadHoldName(folder);
                var hold = new Mutex(false, name, out var isFirst);
                if (isFirst)
                {
                    lock (OwnDownloads) OwnDownloads.Add(name);
                    return new OwnDownload(hold, name);
                }
                hold.Dispose();
            }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null; // Another window has it, running as administrator.
        }
    }

    // Downloads this window is finishing, so its own work in that folder isn't held back by them.
    private static readonly HashSet<string> OwnDownloads = [];

    private sealed class OwnDownload(Mutex hold, string name) : IDisposable
    {
        public void Dispose()
        {
            lock (OwnDownloads) OwnDownloads.Remove(name);
            hold.Dispose();
        }
    }

    /// <summary>Another window of the app is finishing this folder's download.</summary>
    public static bool DownloadIsHeldElsewhere(string folder)
    {
        lock (OwnDownloads)
        {
            if (OwnDownloads.Contains(DownloadHoldName(folder))) return false;
        }
        return DownloadIsHeld(folder);
    }

    /// <summary>Said when a remote move waits for another window's download to finish.</summary>
    public const string WaitingForDownload = "That folder's download is being finished in another window, so its remote is left as it is until that's done.";

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
}
