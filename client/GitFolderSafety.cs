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
/// can't be worked out, the folder is refused. A folder that's already on a network drive has
/// nothing to add to that risk, so it's left alone.
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
            if (IsNetworkPath(full)) return null;

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
        }
        return null;
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
            if (line.StartsWith('"') || StartsLikeShare(line)) return $"{alternates} points at {line}";
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

            // No setting has a reason to name a network location in a folder that isn't on one.
            // %(prefix) is Git's own shorthand, which leaves a path this can't see the end of.
            var value = parts[1].StartsWith(OptionalPath, StringComparison.Ordinal) ? parts[1][OptionalPath.Length..] : parts[1];
            var named = $"the setting {parts[0]} in {file}";
            if (StartsLikeShare(value) || value.StartsWith("%(prefix)", StringComparison.Ordinal) || NamesNetworkDrive(value)) return named;

            // Pointing a setting at nothing is the usual way to turn off a repo's hooks.
            // Git can read these from either place, depending on what's asking.
            if (PathSetting().IsMatch(parts[0]) && value != "/dev/null" && (IsUnsafeGitPath(chosen, value, directory, true, out _) || IsUnsafeGitPath(chosen, value, chosen, true, out _))) return named;

            if (!IncludeSetting().IsMatch(parts[0])) continue;
            if (IsUnsafeGitPath(chosen, value, directory, true, out var included)) return named;
            if (await SettingsProblemAsync(chosen, included, depth + 1, readSettingsFile) is { } further) return further;
        }
        return null;
    }

    private static bool IsUnsafeToOpen(string chosen, string path) => IsNetworkPath(path) || SshConfigFile.HasLinkBelowChosenPath(chosen, path);

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

    private static bool NamesNetworkDrive(string value)
    {
        try
        {
            return IsNetworkPath(value);
        }
        catch (ArgumentException)
        {
            return false; // Most settings aren't paths at all.
        }
    }

    private static bool IsNetworkPath(string path)
    {
        if (StartsLikeShare(path)) return true;
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
    }

    [GeneratedRegex(@"^(ref:\s*refs/|[0-9a-fA-F]{40}|[0-9a-fA-F]{64})")]
    private static partial Regex ValidHead();

    [GeneratedRegex(@"^include(if\..*)?\.path$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IncludeSetting();

    [GeneratedRegex(@"^core\.(worktree|hookspath|excludesfile|attributesfile)$", RegexOptions.IgnoreCase)]
    private static partial Regex PathSetting();
}
