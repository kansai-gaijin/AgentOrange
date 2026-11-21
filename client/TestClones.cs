using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// A test machine's own clones, one a project, in its work folder (#93): a big game's is tens of
/// GB, so Settings › Test machines says how much disk each uses and can remove one. Only folders
/// named as the runner names them and holding a repo count, so nothing else a person keeps in the
/// work folder is offered. Links inside one, such as a junction, are never followed: they count
/// for nothing, and removing the clone removes the link, never what it points at.
/// </summary>
public static partial class TestClones
{
    /// <summary>A clone: its folder, the name it's under, and when it last changed.</summary>
    public sealed record Clone(string Folder, string Name, DateTime ChangedAt);

    // As TestRunner names a project's folder: a plain name and a part of the project's id, or the part alone.
    [GeneratedRegex(@"\A([A-Za-z0-9_-]{1,40}-)?[0-9a-f]{8}\z")]
    private static partial Regex RunnersName();

    /// <summary>The clones in the work folder, by name. None when it isn't there or can't be read.</summary>
    public static List<Clone> In(string workFolder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(workFolder) || !Directory.Exists(workFolder)) return [];
            return [.. new DirectoryInfo(workFolder).EnumerateDirectories()
                .Where(folder => !folder.Attributes.HasFlag(FileAttributes.ReparsePoint) && RunnersName().IsMatch(folder.Name) && Directory.Exists(Path.Combine(folder.FullName, ".git")))
                .Select(folder => new Clone(folder.FullName, folder.Name, folder.LastWriteTime))
                .OrderBy(clone => clone.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The bytes its files take, links not followed. Slow for a big clone, so never on the window's thread.</summary>
    public static long SizeOf(string folder, CancellationToken cancel = default)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            cancel.ThrowIfCancellationRequested();
            total += file.Length;
        }
        return total;
    }

    /// <summary>A size as people read one: 812 KB, 4.2 MB, 24.1 GB.</summary>
    public static string SizeText(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : size.ToString(size < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>
    /// Removes one of the clones in the work folder, and only one of those. A link inside it is
    /// removed as a link, never followed. Null once it's gone, or why it isn't, in words a person
    /// can act on.
    /// </summary>
    public static string? Remove(string folder, string workFolder)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(workFolder)), StringComparison.OrdinalIgnoreCase) || !RunnersName().IsMatch(Path.GetFileName(full)))
            {
                return "That folder isn't one of this PC's test clones, so it was left alone.";
            }
            var clone = new DirectoryInfo(full);
            if (!clone.Exists) return null;
            if (clone.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                clone.Delete(); // Only the link goes.
                return null;
            }
            RemoveInside(clone);
            if (clone.Attributes.HasFlag(FileAttributes.ReadOnly)) clone.Attributes &= ~FileAttributes.ReadOnly;
            clone.Delete();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write($"Couldn't remove the test clone {folder}: {error.Message}");
            return $"Some of it couldn't be removed: {error.Message} A program may have a file in it open. Close it and remove it again.";
        }
    }

    // Depth first. Git keeps its objects read-only, which Windows won't delete, so that's cleared first.
    private static void RemoveInside(DirectoryInfo folder)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var entry in folder.EnumerateFileSystemInfos("*", options).ToList())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Delete(); // A junction or symbolic link: the link itself, never its target.
                continue;
            }
            if (entry is DirectoryInfo inner)
            {
                RemoveInside(inner);
                if (inner.Attributes.HasFlag(FileAttributes.ReadOnly)) inner.Attributes &= ~FileAttributes.ReadOnly;
                inner.Delete();
                continue;
            }
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }
    }
}
