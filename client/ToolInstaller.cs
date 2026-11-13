using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace AgentOrange;

/// <summary>
/// Installs a tool from the newest release of its repo, and updates it the same way. The release's
/// zip is unpacked beside the install first, then copied over it, so a download that fails leaves
/// the copy that was there working. Files the tool writes next to itself, such as its settings,
/// stay, as nothing is removed.
/// </summary>
public static class ToolInstaller
{
    // Inside the install, so it describes the files actually there, on this PC.
    private const string VersionFile = "agent-orange-release.txt";

    /// <summary>The release tag installed by the app, "" for a copy installed some other way, or null if there's none.</summary>
    public static string? InstalledVersion(EditorDefinition tool)
    {
        if (tool.ProgramFile is not { } program || !File.Exists(Path.Combine(tool.InstallFolder, program))) return null;
        try
        {
            var noted = Path.Combine(tool.InstallFolder, VersionFile);
            return File.Exists(noted) ? File.ReadAllText(noted).Trim() : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    public static bool IsRunning(EditorDefinition tool)
    {
        var folder = Path.TrimEndingDirectorySeparator(tool.InstallFolder) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Someone else's, or one that's just closed.
                }
            }
        }
        return false;
    }

    /// <summary>Returns what went wrong, or null once it's installed.</summary>
    public static async Task<string?> InstallAsync(EditorDefinition tool, ToolRelease release, Action<string> progress)
    {
        if (tool.Repo is not { } repo || tool.ProgramFile is not { } program) return $"{tool.Name} can't be installed from here.";
        if (IsRunning(tool)) return $"Close {tool.Name} first. It's running from the folder this installs to.";

        var work = Path.Combine(Path.GetTempPath(), "AgentOrange-install", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            progress($"Downloading {release.Tag} ({AppFolders.FormatSize(release.Size)})…");
            if (await GitTools.DownloadReleaseAsync(repo, release, work) is { } failed) return failed;

            progress("Unpacking…");
            var unpacked = Path.Combine(work, "unpacked");
            var found = await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(Path.Combine(work, release.FileName), unpacked);

                // A zip often has one folder at its top, with everything in it.
                return Directory.EnumerateFiles(unpacked, program, SearchOption.AllDirectories)
                    .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
                    .FirstOrDefault();
            });
            if (found is null) return $"The release has no {program} in it.";

            // Checked again, as the download can take a while.
            if (IsRunning(tool)) return $"Close {tool.Name} first. It's running from the folder this installs to.";
            progress("Installing…");
            var from = Path.GetDirectoryName(found)!;
            await Task.Run(() =>
            {
                CopyOver(from, tool.InstallFolder);
                File.WriteAllText(Path.Combine(tool.InstallFolder, VersionFile), release.Tag);
            });
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Write($"Couldn't install {tool.Name}: {error.Message}");
            return $"Couldn't install it: {error.Message}";
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Couldn't clear a tool download: {error.Message}");
            }
        }
    }

    private static void CopyOver(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }
}
