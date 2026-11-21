using System.Diagnostics;
using System.IO;

namespace AgentOrange;

/// <summary>
/// A copy of Agent Orange's own program in the agent files folder, which everything agent sessions
/// need of it runs from: the MCP relay, the hooks and each session's host (<see cref="SessionHost"/>).
/// Not the app's own files, as an update replaces those, ending whatever runs from them, and a dev
/// build rebuilds them, which a program running from them stops. One copy for each build, so a newer
/// one goes beside those still running, and copies nothing runs from any more are removed.
/// </summary>
internal static class AppCopy
{
    private static readonly object Making = new();

    /// <summary>The copy's AgentOrange.exe, made first if this build has none yet; the app's own if it can't be made.</summary>
    public static string Program(string agentFolder)
    {
        var own = Path.Combine(AppContext.BaseDirectory, "AgentOrange.exe");
        var built = new FileInfo(Path.Combine(AppContext.BaseDirectory, "AgentOrange.dll"));
        if (!built.Exists) return own;
        var copy = Path.Combine(agentFolder, "program", $"{built.LastWriteTimeUtc.Ticks:x}-{built.Length:x}");
        var program = Path.Combine(copy, "AgentOrange.exe");

        lock (Making)
        {
            if (File.Exists(program)) return program;

            // Made aside and moved into place whole, so a copy that's there is never half made.
            var making = $"{copy}-{Guid.NewGuid():N}";
            try
            {
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(AppContext.BaseDirectory, file);
                    if (!Needed(relative)) continue;
                    var to = Path.Combine(making, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(file, to);
                }
                Directory.Move(making, copy);
                return program;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(program)) return program; // Another copy of the app made it first.
                AppLog.Write($"Couldn't copy the program for agent sessions: {error.Message}");
                return own;
            }
            finally
            {
                TryDelete(making);
            }
        }
    }

    /// <summary>Makes this build's copy now, away from the window, so the first session doesn't wait for it.</summary>
    public static void Prepare(string agentFolder) => _ = Task.Run(() =>
    {
        Program(agentFolder);
        CleanUp(agentFolder);
    });

    // The skills that come with the app, and other kinds of PC's native libraries, aren't used by what runs from the copy.
    private static bool Needed(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return parts.Length == 1 || parts[0] switch
        {
            "Skills" => false,
            "runtimes" => parts.Length > 2 && parts[1] == "win-x64",
            _ => true,
        };
    }

    /// <summary>Removes copies of other builds that nothing runs from any more.</summary>
    public static void CleanUp(string agentFolder)
    {
        var root = Path.Combine(agentFolder, "program");
        if (!Directory.Exists(root)) return;
        var current = Path.GetDirectoryName(Program(agentFolder));
        var running = new List<string>();
        foreach (var process in Process.GetProcessesByName("AgentOrange"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } file) running.Add(file);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // It ended while being looked at.
                }
            }
        }
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(folder, current, StringComparison.OrdinalIgnoreCase)) continue;
            if (running.Any(file => file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            TryDelete(folder);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Something started from it meanwhile. It goes next time.
        }
    }
}
