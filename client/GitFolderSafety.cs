using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// Git follows some things in a folder's own files as soon as it runs there, even just to read
/// the remote. Opening a file on a network share can send the person's Windows login to whoever
/// runs it, and a folder can come from anyone. So this reads those files itself first, without
/// following anything onto a network. A folder that's already on a network drive has nothing to
/// add to that risk, so it's left alone.
/// </summary>
internal static partial class GitFolderSafety
{
    private const int MaxIncludeDepth = 5;
    private const int MaxBytes = 1_000_000;
    private const string Problem = "fatal: That folder's Git settings point at a network location, and opening it could send your Windows login there. Nothing was run in it. Remove that from its Git settings if you trust where it came from.";

    /// <summary>Why Git shouldn't run in this folder, or null if nothing's wrong that can be seen.</summary>
    public static string? NetworkPathProblem(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            if (IsNetworkPath(full) || FindGitEntry(full) is not { } entry) return null;

            var repoFolder = Path.GetDirectoryName(entry)!;
            var gitDirectory = entry;

            // A .git file says where the real Git data is, as worktrees and submodules have.
            if (File.Exists(entry))
            {
                if (new FileInfo(entry).Length > MaxBytes) return Problem;
                var line = File.ReadLines(entry).FirstOrDefault(text => text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
                if (line is null) return null;

                gitDirectory = SshConfigFile.Resolve(line["gitdir:".Length..].Trim(), repoFolder);
                if (IsNetworkPath(gitDirectory)) return Problem;
            }
            else if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
            {
                return Problem; // A link could lead anywhere, and following it to find out is the risk.
            }

            var shared = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(shared) && new FileInfo(shared).Length < MaxBytes && IsNetworkPath(SshConfigFile.Resolve(File.ReadAllText(shared).Trim(), gitDirectory))) return Problem;

            return HasNetworkPath(Path.Combine(gitDirectory, "config"), 0) || HasNetworkPath(Path.Combine(gitDirectory, "config.worktree"), 0) ? Problem : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // Git's own answer about a folder it can't read is a better one.
        }
    }

    // Git looks upward from where it runs, and the nearest one counts.
    private static string? FindGitEntry(string folder)
    {
        for (var at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            var entry = Path.Combine(at, ".git");
            if (File.Exists(entry) || Directory.Exists(entry)) return entry;
        }
        return null;
    }

    private static bool HasNetworkPath(string configFile, int depth)
    {
        if (depth > MaxIncludeDepth) return true; // Git gives up on this too, and it can't be vouched for.
        var file = new FileInfo(configFile);
        if (!file.Exists) return false;
        if (file.LinkTarget is not null || file.Length > MaxBytes) return true;

        var directory = file.DirectoryName!;
        var inInclude = false;
        foreach (var line in File.ReadLines(configFile))
        {
            if (Section().Match(line) is { Success: true } section)
            {
                inInclude = section.Groups[1].Value.StartsWith("include", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (Setting().Match(line) is not { Success: true } setting) continue;
            var raw = setting.Groups[2].Value.Trim().Trim('"');
            var value = raw.Replace(@"\\", @"\", StringComparison.Ordinal);

            // No setting has a reason to name a network location in a folder that isn't on one.
            if (StartsLikeShare(raw) || StartsLikeShare(value)) return true;
            if (!inInclude || !setting.Groups[1].Value.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;

            var included = SshConfigFile.Resolve(value, directory);
            if (IsNetworkPath(included) || SshConfigFile.HasLinkOnTheWay(included)) return true;
            if (HasNetworkPath(included, depth + 1)) return true;
        }
        return false;
    }

    private static bool StartsLikeShare(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsNetworkPath(string path)
    {
        if (StartsLikeShare(path)) return true;
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
    }

    [GeneratedRegex(@"^\s*\[\s*([A-Za-z.-]+)")]
    private static partial Regex Section();

    [GeneratedRegex(@"^\s*([A-Za-z][A-Za-z0-9-]*)\s*=\s*(.*)$")]
    private static partial Regex Setting();
}
