using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// Git follows some things in a folder's own files as soon as it runs there, even just to read
/// the remote. Opening a file on a network share can send the person's Windows login to whoever
/// runs it, and a folder can come from anyone. So this looks at those files first, without
/// following anything onto a network. Settings files are read by Git itself, from outside the
/// folder and without includes, so they mean exactly what they'd mean to Git. If any of this
/// can't be worked out, the folder is refused. A folder chosen on a network share means that share
/// is trusted, but not any other it points at.
/// </summary>
internal static partial class GitFolderSafety
{
    private const int MaxIncludeDepth = 5;
    private const int MaxBytes = 1_000_000;
    private static readonly TimeSpan RememberFor = TimeSpan.FromSeconds(3);
    private const int MaxRemembered = 200;
    private const int MaxHeadBytes = 4096;
    private const string OptionalPath = ":(optional)";
    private const int MaxFoundLength = 300;

    // Git runs many times in a row for one check, and each would otherwise look at all this again.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Answer)> Recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why Git shouldn't run in this folder, or null if nothing's wrong.</summary>
    public static async Task<string?> NetworkPathProblemAsync(string folder, Func<string, Task<string?>> readSettingsFile)
    {
        if (Recent.TryGetValue(folder, out var known) && DateTimeOffset.UtcNow - known.At < RememberFor) return known.Answer;

        // Off the UI thread, because a dying drive can make any of this slow.
        var answer = await Task.Run(() => InspectAsync(folder, readSettingsFile));
        var now = DateTimeOffset.UtcNow;
        Recent[folder] = (now, answer);

        // Every clone works in its own new folder, so these would otherwise only pile up.
        if (Recent.Count > MaxRemembered)
        {
            foreach (var old in Recent.Where(entry => now - entry.Value.At >= RememberFor)) Recent.TryRemove(old.Key, out _);
        }
        return answer;
    }

    // Names only what stopped it, as a setting's value can be a login, and so can part of a name.
    private static string Refusal(string found)
    {
        var shown = GitTools.HideLogins(GitTools.OneLine(found)).TrimEnd('.');
        if (shown.Length > MaxFoundLength) shown = $"{shown[..MaxFoundLength]}…";
        return $"That folder's Git settings or data point at a network location, or through a link that could lead to one, or couldn't be checked. Opening one could send your Windows login there, so nothing was run in it. What stopped it: {shown}. Fix that in the folder if you trust where it came from.";
    }

    private static async Task<string?> InspectAsync(string folder, Func<string, Task<string?>> readSettingsFile)
    {
        var foundOne = false;
        try
        {
            var full = Path.GetFullPath(folder);

            for (var at = full; at is not null; at = Path.GetDirectoryName(at))
            {
                // Laid out as a bare repo, which older versions of Git would count as one.
                if (LooksLikeBareRepo(at)) return Refusal($"{at} is laid out like a bare repo");

                var entry = Path.Combine(at, ".git");
                if (!Exists(entry)) continue;
                foundOne = true;
                if (await WhatPointsAtNetworkAsync(entry, readSettingsFile) is { } found) return Refusal(found);

                // Git stops at the first real repo it finds, so what's further up has no say here.
                if (StopsGitLooking(entry)) break;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Write($"Couldn't check a folder's Git settings: {error.Message}");
            return foundOne ? Refusal($"part of it couldn't be read ({error.Message})") : null;
        }
    }

    // Git goes by a .git first, so a project that just has folders with these names isn't one. It
    // passes over a .git folder that isn't really a repo, though, and carries on to the rest.
    private static bool LooksLikeBareRepo(string folder)
    {
        if (Path.GetFileName(folder) == ".git" || !HasRepoLayout(folder)) return false;

        var git = Path.Combine(folder, ".git");
        if (!Exists(git)) return true;
        if (SshConfigFile.IsLink(git) || !File.GetAttributes(git).HasFlag(FileAttributes.Directory)) return false; // Looked at next, without following it.
        return !HasRepoLayout(git);
    }

    // Only when Git would really accept it, because it passes over one that it wouldn't and carries
    // on upward. Being wrong here has to mean looking at too much, never at too little.
    private static bool StopsGitLooking(string entry)
    {
        if (!File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) return true; // A .git file either works or is an error.
        if (File.Exists(Path.Combine(entry, "commondir")) || !Directory.Exists(Path.Combine(entry, "objects")) || !Directory.Exists(Path.Combine(entry, "refs"))) return false;

        var head = new FileInfo(Path.Combine(entry, "HEAD"));
        return head.Exists && head.Length <= MaxHeadBytes && ValidHead().IsMatch(File.ReadAllText(head.FullName));
    }

    private static bool HasRepoLayout(string folder) =>
        Exists(Path.Combine(folder, "HEAD")) && Exists(Path.Combine(folder, "objects")) && Exists(Path.Combine(folder, "refs"));

    // How Git itself reads a path in these files, which is literally, apart from ~/ in settings.
    // A shell would expand %TEMP%, and looking there instead of where Git will look would be
    // checking the wrong file. Null for anything it may read differently from this, which isn't safe.
    private static string? GitPath(string value, string relativeTo, bool homeAllowed = false)
    {
        var text = value;

        // Stays on whichever share it names, however it goes on, and that gets looked at next. An
        // admin share has a $ in its name, which means nothing here.
        if (StartsLikeShare(text))
        {
            try
            {
                return Path.GetFullPath(text);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        if (text.Length == 0 || text.IndexOfAny(['%', '$']) >= 0 || text[0] is '/' or '\\') return null;
        if (text[0] == '~')
        {
            if (!homeAllowed || !(text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))) return null;
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
        }
        return Path.GetFullPath(text, relativeTo);
    }

    private static bool IsUnsafeGitPath(string chosen, string value, string relativeTo, bool homeAllowed, out string path)
    {
        var resolved = GitPath(value, relativeTo, homeAllowed);
        path = resolved ?? "";
        return resolved is null || IsUnsafeToOpen(chosen, resolved);
    }

    private static async Task<string?> WhatPointsAtNetworkAsync(string entry, Func<string, Task<string?>> readSettingsFile)
    {
        if (SshConfigFile.IsLink(entry)) return $"{entry} is a link"; // It could lead anywhere, and following it to find out is the risk.

        // Where this repo is, however the person chooses to get there.
        var chosen = Path.GetDirectoryName(entry)!;

        var gitDirectory = entry;

        // A .git file says where the real Git data is, as worktrees and submodules have.
        if (!File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
        {
            if (new FileInfo(entry).Length > MaxBytes) return $"{entry} is too big to check";
            var line = File.ReadLines(entry).FirstOrDefault(text => text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
            if (line is null) return null;

            var target = line["gitdir:".Length..].Trim();
            if (IsUnsafeGitPath(chosen, target, chosen, false, out gitDirectory)) return $"{entry} points at {target}";
        }

        var directories = new List<string> { gitDirectory };

        // Before anything in there is opened, as opening one is the risk.
        if (Directory.Exists(gitDirectory) && LinkInside(gitDirectory) is { } link) return $"{link} is a link";

        // A worktree shares most of its settings and data with the repo it came from.
        var shared = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(shared))
        {
            if (new FileInfo(shared).Length > MaxBytes) return $"{shared} is too big to check";
            var target = File.ReadAllText(shared).Trim();
            if (IsUnsafeGitPath(chosen, target, gitDirectory, false, out var common)) return $"{shared} points at {target}";
            directories.Add(common);
        }

        foreach (var directory in directories.Where(Directory.Exists))
        {
            if (directory != gitDirectory && LinkInside(directory) is { } otherLink) return $"{otherLink} is a link";
            if (AlternateProblem(chosen, Path.Combine(directory, "objects"), 0) is { } alternate) return alternate;
            if (await SettingsProblemAsync(chosen, Path.Combine(directory, "config"), 0, readSettingsFile) is { } settings) return settings;
            if (await SettingsProblemAsync(chosen, Path.Combine(directory, "config.worktree"), 0, readSettingsFile) is { } worktreeSettings) return worktreeSettings;
        }

        // Git opens a submodule's own settings and history as soon as it works in one.
        if (!Directory.Exists(gitDirectory)) return null;
        foreach (var module in ModuleDirectories(gitDirectory))
        {
            if (AlternateProblem(chosen, Path.Combine(module, "objects"), 0) is { } alternate) return alternate;
            if (await SettingsProblemAsync(chosen, Path.Combine(module, "config"), 0, readSettingsFile) is { } settings) return settings;
            if (await SettingsProblemAsync(chosen, Path.Combine(module, "config.worktree"), 0, readSettingsFile) is { } worktreeSettings) return worktreeSettings;
        }
        return null;
    }

    /// <summary>The same look as a repo's own Git data gets, for one a submodule keeps somewhere of its own.</summary>
    public static async Task<string?> SubmoduleDataProblemAsync(string chosen, string place, Func<string, Task<string?>> readSettingsFile)
    {
        var found = await Task.Run(async () =>
        {
            return LinkInside(place) is { } link ? $"{link} is a link"
                : AlternateProblem(chosen, Path.Combine(place, "objects"), 0)
                ?? await SettingsProblemAsync(chosen, Path.Combine(place, "config"), 0, readSettingsFile)
                ?? await SettingsProblemAsync(chosen, Path.Combine(place, "config.worktree"), 0, readSettingsFile);
        });
        return found is null ? null : Refusal(found);
    }

    /// <summary>Where a repo keeps its submodules' Git data, including submodules inside those. A submodule's name can have slashes in it.</summary>
    public static List<string> ModuleDirectories(string gitDirectory)
    {
        var found = new List<string>();
        var waiting = new Stack<string>();
        waiting.Push(Path.Combine(gitDirectory, "modules"));
        while (waiting.TryPop(out var directory))
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var entry in new DirectoryInfo(directory).EnumerateDirectories())
            {
                if (entry.LinkTarget is not null) continue; // Refused by what looks for links, and not to be followed.
                if (Exists(Path.Combine(entry.FullName, "HEAD")))
                {
                    found.Add(entry.FullName);
                    waiting.Push(Path.Combine(entry.FullName, "modules"));
                }
                else
                {
                    waiting.Push(entry.FullName);
                }
            }
        }
        return found;
    }

    // Git opens what it finds among a repo's own files, such as following a branch that's a link.
    // Git LFS keeps its big files in lfs, and moving them onto another drive with a link is normal.
    private static string? LinkInside(string gitDirectory)
    {
        var waiting = new Stack<string>();
        waiting.Push(gitDirectory);
        while (waiting.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.Name == "lfs" && (directory == gitDirectory || IsSubmoduleData(gitDirectory, directory))) continue;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null) return entry.FullName;
                if (entry.Attributes.HasFlag(FileAttributes.Directory)) waiting.Push(entry.FullName);
            }
        }
        return null;
    }

    /// <summary>
    /// Moving big files onto another drive with a link is normal, so the usual look leaves them
    /// alone, but Git LFS opens what's there. So where each link leads is read, without following
    /// it, and one onto a network is refused. Only for when Git LFS is about to run, as there can
    /// be a lot of these. Says what stopped it, or null if nothing did.
    /// </summary>
    public static string? LargeFileStoreProblem(string gitDirectory)
    {
        var linksFollowed = 0;
        var waiting = new Stack<string>();
        waiting.Push(Path.Combine(gitDirectory, "lfs"));
        while (waiting.TryPop(out var place))
        {
            if (!Exists(place)) continue;

            var directory = new DirectoryInfo(place);
            if (directory.LinkTarget is { } leadsTo)
            {
                if (++linksFollowed > MaxIncludeDepth || LeadsAway(directory.Parent?.FullName, leadsTo, out var next)) return $"{directory.FullName} leads to {leadsTo}";
                waiting.Push(next);
                continue;
            }

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.Directory)) waiting.Push(entry.FullName);

                // Git LFS keeps real files here, so one that's a link didn't come from moving the store.
                else if (entry.LinkTarget is { } fileLeadsTo) return $"{entry.FullName} leads to {fileLeadsTo}";
            }
        }
        return null;
    }

    // Read one step at a time, from the top, as just asking about a path goes through any link on
    // the way to it. Another link on the way counts too, as it could lead anywhere.
    private static bool LeadsAway(string? from, string target, out string next)
    {
        // The form Windows can keep a local target in, which would otherwise look like a share.
        var plain = target.StartsWith(@"\\?\", StringComparison.Ordinal) && !target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? target[4..] : target;
        next = "";
        if (StartsLikeShare(plain) || from is null) return true;

        next = Path.GetFullPath(plain, from);
        if (IsNetworkPath(next)) return true;

        var at = Path.GetPathRoot(next) ?? "";
        foreach (var part in next[at.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, part);
            if (!Exists(at)) return false;
            if (SshConfigFile.IsLink(at)) return true;
        }
        return false;
    }

    private static bool IsSubmoduleData(string gitDirectory, string directory) =>
        directory.StartsWith(Path.Combine(gitDirectory, "modules") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Exists(Path.Combine(directory, "HEAD"));

    // Other places a repo borrows its history from, and those can borrow from others in turn. Git
    // opens them all to look for any commit.
    private static string? AlternateProblem(string chosen, string objects, int depth)
    {
        var alternates = Path.Combine(objects, "info", "alternates");
        if (depth > MaxIncludeDepth) return $"{alternates} borrows through too many others";
        if (IsUnsafeToOpen(chosen, alternates)) return $"{alternates} is on a network or reached through a link";
        if (!Exists(alternates)) return null;
        if (new FileInfo(alternates).Length > MaxBytes) return $"{alternates} is too big to check";

        foreach (var line in File.ReadLines(alternates).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            // A quoted line can spell a path in escapes, and ordinary ones have no need to be quoted.
            if (line.StartsWith('"')) return $"{alternates} points at {line}";
            if (IsUnsafeGitPath(chosen, line, objects, false, out var next)) return $"{alternates} points at {line}";
            if (Directory.Exists(next) && LinkInside(next) is { } link) return $"{link} is a link";
            if (AlternateProblem(chosen, next, depth + 1) is { } further) return further;
        }
        return null;
    }

    private static async Task<string?> SettingsProblemAsync(string chosen, string file, int depth, Func<string, Task<string?>> readSettingsFile)
    {
        if (depth > MaxIncludeDepth) return $"{file} includes too many other settings files"; // Git gives up on this too, and it can't be vouched for.
        if (IsUnsafeToOpen(chosen, file)) return $"{file} is on a network or reached through a link";

        var info = new FileInfo(file);
        if (!info.Exists) return null; // Git skips a missing one, and carries on to the next.
        if (info.Length > MaxBytes) return $"{file} is too big to check";

        // As Git itself reads it, which settles continued lines, quoting and settings on a header's line.
        if (await readSettingsFile(file) is not { } settings) return $"Git couldn't read {file}";

        var directory = info.DirectoryName!;
        foreach (var setting in settings.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = setting.Split('\n', 2);
            if (parts.Length < 2) continue;

            // No setting has a reason to name a network location, other than the share the folder's on.
            // %(prefix) is Git's own shorthand, which leaves a path this can't see the end of.
            var value = parts[1].StartsWith(OptionalPath, StringComparison.Ordinal) ? parts[1][OptionalPath.Length..] : parts[1];
            var named = $"the setting {parts[0]} in {file}";
            if (NamesAnotherShare(chosen, value) || value.StartsWith("%(prefix)", StringComparison.Ordinal)) return named;

            // Pointing a setting at nothing is the usual way to turn off a repo's hooks.
            // Git can read these from either place, depending on what's asking.
            if (PathSetting().IsMatch(parts[0]) && value is not ("" or "/dev/null"))
            {
                // How Git Bash writes a drive, which may be how this got set. Anything else with one
                // slash at the start isn't something this can be sure how Git reads, so it's refused.
                var path = BashDrivePath().Replace(value, "$1:/$2");
                if (IsUnsafeGitPath(chosen, path, directory, true, out _) || IsUnsafeGitPath(chosen, path, chosen, true, out _)) return named;
            }

            if (!IncludeSetting().IsMatch(parts[0])) continue;
            if (IsUnsafeGitPath(chosen, value, directory, true, out var included)) return named;
            if (await SettingsProblemAsync(chosen, included, depth + 1, readSettingsFile) is { } further) return further;
        }
        return null;
    }

    private static bool IsUnsafeToOpen(string chosen, string path) => OnAnotherShare(chosen, path) || SshConfigFile.HasLinkBelowChosenPath(chosen, path);

    private static bool OnAnotherShare(string chosen, string path) =>
        IsNetworkPath(path) && !string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), Path.GetPathRoot(Path.GetFullPath(chosen)), StringComparison.OrdinalIgnoreCase);

    private static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path); // Answers about a link itself, without following it.
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    // Windows treats either kind of slash the same here, in any mix.
    private static bool StartsLikeShare(string path) =>
        path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    // Most settings aren't paths at all, and "Z: Team" isn't one just because there's a Z drive.
    // A value shaped like a share that this can't read is still refused.
    private static bool NamesAnotherShare(string chosen, string value)
    {
        if (!ShareShape().IsMatch(value) && !DrivePath().IsMatch(value)) return false;
        try
        {
            return OnAnotherShare(chosen, value);
        }
        catch (ArgumentException)
        {
            return true; // Starts like one, and there's no telling where it leads.
        }
    }

    private static bool IsNetworkPath(string path)
    {
        if (StartsLikeShare(path)) return true;
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
    }

    // What "//" alone isn't, as a comment character or the start of a description can be just that.
    [GeneratedRegex(@"^[\\/]{2}[^\\/\s]+[\\/][^\\/\s]")]
    private static partial Regex ShareShape();

    [GeneratedRegex(@"^/([A-Za-z])/(.*)$", RegexOptions.Singleline)]
    private static partial Regex BashDrivePath();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"^(ref:\s*refs/|[0-9a-fA-F]{40}|[0-9a-fA-F]{64})")]
    private static partial Regex ValidHead();

    [GeneratedRegex(@"^include(if\..*)?\.path$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IncludeSetting();

    [GeneratedRegex(@"^core\.(worktree|hookspath|excludesfile|attributesfile)$", RegexOptions.IgnoreCase)]
    private static partial Regex PathSetting();
}
