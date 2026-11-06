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
    private const string Problem = "fatal: That folder's Git settings or data point at a network location, or through a link that could lead to one, or couldn't be checked. Opening one could send your Windows login there, so nothing was run in it. Fix that in the folder if you trust where it came from.";

    // Git runs many times in a row for one check, and each would otherwise look at all this again.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Answer)> Recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why Git shouldn't run in this folder, or null if nothing's wrong.</summary>
    public static async Task<string?> NetworkPathProblemAsync(string folder, Func<string, Task<string?>> readSettingsFile)
    {
        if (Recent.TryGetValue(folder, out var known) && DateTimeOffset.UtcNow - known.At < RememberFor) return known.Answer;

        // Off the UI thread, because a dying drive can make any of this slow.
        var answer = await Task.Run(() => InspectAsync(folder, readSettingsFile));
        Recent[folder] = (DateTimeOffset.UtcNow, answer);
        return answer;
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
                if (LooksLikeBareRepo(at)) return Problem;

                var entry = Path.Combine(at, ".git");
                if (!Exists(entry)) continue;
                foundOne = true;
                if (await PointsAtNetworkAsync(entry, readSettingsFile)) return Problem;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Write($"Couldn't check a folder's Git settings: {error.Message}");
            return foundOne ? Problem : null;
        }
    }

    private static bool LooksLikeBareRepo(string folder) =>
        Path.GetFileName(folder) != ".git"
        && Exists(Path.Combine(folder, "HEAD")) && Exists(Path.Combine(folder, "objects")) && Exists(Path.Combine(folder, "refs"));

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

    private static async Task<bool> PointsAtNetworkAsync(string entry, Func<string, Task<string?>> readSettingsFile)
    {
        if (SshConfigFile.IsLink(entry)) return true; // It could lead anywhere, and following it to find out is the risk.

        // Where this repo is, however the person chooses to get there.
        var chosen = Path.GetDirectoryName(entry)!;

        var gitDirectory = entry;

        // A .git file says where the real Git data is, as worktrees and submodules have.
        if (!File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
        {
            if (new FileInfo(entry).Length > MaxBytes) return true;
            var line = File.ReadLines(entry).FirstOrDefault(text => text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
            if (line is null) return false;

            if (IsUnsafeGitPath(chosen, line["gitdir:".Length..].Trim(), chosen, false, out gitDirectory)) return true;
        }

        var directories = new List<string> { gitDirectory };

        // Before anything in there is opened, as opening one is the risk.
        if (Directory.Exists(gitDirectory) && HasLinkInside(gitDirectory)) return true;

        // A worktree shares most of its settings and data with the repo it came from.
        var shared = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(shared))
        {
            if (new FileInfo(shared).Length > MaxBytes) return true;
            if (IsUnsafeGitPath(chosen, File.ReadAllText(shared).Trim(), gitDirectory, false, out var common)) return true;
            directories.Add(common);
        }

        foreach (var directory in directories.Where(Directory.Exists))
        {
            if (directory != gitDirectory && HasLinkInside(directory)) return true;
            if (HasNetworkAlternate(chosen, Path.Combine(directory, "objects"), 0)) return true;
            if (await SettingsPointAtNetworkAsync(chosen, Path.Combine(directory, "config"), 0, readSettingsFile)) return true;
            if (await SettingsPointAtNetworkAsync(chosen, Path.Combine(directory, "config.worktree"), 0, readSettingsFile)) return true;
        }
        return false;
    }

    // Git opens what it finds among a repo's own files, such as following a branch that's a link.
    // Git LFS keeps its big files in lfs, and moving them onto another drive with a link is normal.
    private static bool HasLinkInside(string gitDirectory)
    {
        var waiting = new Stack<string>();
        waiting.Push(gitDirectory);
        while (waiting.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (directory == gitDirectory && entry.Name == "lfs") continue;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null) return true;
                if (entry.Attributes.HasFlag(FileAttributes.Directory)) waiting.Push(entry.FullName);
            }
        }
        return false;
    }

    // Other places a repo borrows its history from, and those can borrow from others in turn. Git
    // opens them all to look for any commit.
    private static bool HasNetworkAlternate(string chosen, string objects, int depth)
    {
        if (depth > MaxIncludeDepth) return true;

        var alternates = Path.Combine(objects, "info", "alternates");
        if (IsUnsafeToOpen(chosen, alternates)) return true;
        if (!Exists(alternates)) return false;
        if (new FileInfo(alternates).Length > MaxBytes) return true;

        foreach (var line in File.ReadLines(alternates).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            // A quoted line can spell a path in escapes, and ordinary ones have no need to be quoted.
            if (line.StartsWith('"') || StartsLikeShare(line)) return true;
            if (IsUnsafeGitPath(chosen, line, objects, false, out var next)) return true;
            if (HasNetworkAlternate(chosen, next, depth + 1)) return true;
        }
        return false;
    }

    private static async Task<bool> SettingsPointAtNetworkAsync(string chosen, string file, int depth, Func<string, Task<string?>> readSettingsFile)
    {
        if (depth > MaxIncludeDepth) return true; // Git gives up on this too, and it can't be vouched for.
        if (IsUnsafeToOpen(chosen, file)) return true;

        var info = new FileInfo(file);
        if (!info.Exists) return false; // Git skips a missing one, and carries on to the next.
        if (info.Length > MaxBytes) return true;

        // As Git itself reads it, which settles continued lines, quoting and settings on a header's line.
        if (await readSettingsFile(file) is not { } settings) return true;

        var directory = info.DirectoryName!;
        foreach (var setting in settings.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = setting.Split('\n', 2);
            if (parts.Length < 2) continue;

            // No setting has a reason to name a network location in a folder that isn't on one.
            // %(prefix) is Git's own shorthand, which leaves a path this can't see the end of.
            var value = parts[1];
            if (StartsLikeShare(value) || value.StartsWith("%(prefix)", StringComparison.Ordinal) || NamesNetworkDrive(value)) return true;

            if (PathSetting().IsMatch(parts[0]) && IsUnsafeGitPath(chosen, value, directory, true, out _)) return true;

            if (!IncludeSetting().IsMatch(parts[0])) continue;
            if (IsUnsafeGitPath(chosen, value, directory, true, out var included)) return true;
            if (await SettingsPointAtNetworkAsync(chosen, included, depth + 1, readSettingsFile)) return true;
        }
        return false;
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

    [GeneratedRegex(@"^include(if\..*)?\.path$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IncludeSetting();

    [GeneratedRegex(@"^core\.(worktree|hookspath|excludesfile|attributesfile)$", RegexOptions.IgnoreCase)]
    private static partial Regex PathSetting();
}
