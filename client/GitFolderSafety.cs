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

            foreach (var entry in FindGitEntries(full))
            {
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

    // Git looks upward from where it runs, and skips one that isn't valid, so every one on the way
    // up counts. This doesn't follow a link to see what's there, as File.Exists would. A folder
    // that's a repo with no .git at all, a bare one, is ruled out by how Git is run.
    private static IEnumerable<string> FindGitEntries(string folder)
    {
        for (var at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            var entry = Path.Combine(at, ".git");
            if (Exists(entry)) yield return entry;
        }
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

            gitDirectory = SshConfigFile.Resolve(line["gitdir:".Length..].Trim(), chosen);
            if (IsUnsafeToOpen(chosen, gitDirectory)) return true;
        }

        var directories = new List<string> { gitDirectory };

        // A worktree shares most of its settings and data with the repo it came from.
        var shared = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(shared))
        {
            if (new FileInfo(shared).Length > MaxBytes) return true;
            var common = SshConfigFile.Resolve(File.ReadAllText(shared).Trim(), gitDirectory);
            if (IsUnsafeToOpen(chosen, common)) return true;
            directories.Add(common);
        }

        foreach (var directory in directories.Where(Directory.Exists))
        {
            if (HasLinkInside(directory) || HasLinkInside(Path.Combine(directory, "objects"))) return true;
            if (HasNetworkAlternate(chosen, directory)) return true;
            if (await SettingsPointAtNetworkAsync(chosen, Path.Combine(directory, "config"), 0, readSettingsFile)) return true;
            if (await SettingsPointAtNetworkAsync(chosen, Path.Combine(directory, "config.worktree"), 0, readSettingsFile)) return true;
        }
        return false;
    }

    // Git LFS keeps its big files here, and moving them onto another drive with a link is normal.
    private static bool HasLinkInside(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(entry => Path.GetFileName(entry) != "lfs" && SshConfigFile.IsLink(entry));

    // Other places a repo borrows its history from. Git opens them to look for any commit.
    private static bool HasNetworkAlternate(string chosen, string gitDirectory)
    {
        var objects = Path.Combine(gitDirectory, "objects");
        var alternates = Path.Combine(objects, "info", "alternates");
        if (!Exists(alternates)) return false;
        if (SshConfigFile.IsLink(alternates) || new FileInfo(alternates).Length > MaxBytes) return true;

        // A quoted line can spell a path in escapes, and ordinary ones have no need to be quoted.
        return File.ReadLines(alternates)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Any(line => line.StartsWith('"') || StartsLikeShare(line) || IsUnsafeToOpen(chosen, SshConfigFile.Resolve(line, objects)));
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

            if (PathSetting().IsMatch(parts[0]) && IsUnsafeToOpen(chosen, SshConfigFile.Resolve(value, directory))) return true;

            if (!IncludeSetting().IsMatch(parts[0])) continue;
            if (await SettingsPointAtNetworkAsync(chosen, SshConfigFile.Resolve(value, directory), depth + 1, readSettingsFile)) return true;
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
