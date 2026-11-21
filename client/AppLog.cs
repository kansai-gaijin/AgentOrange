using System.IO;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>A plain text log, for finding out what happened after something goes wrong.</summary>
public static class AppLog
{
    private const long MaxBytes = 5_000_000;

    private static string? _file;

    public static void Start(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _file = Path.Combine(folder, "agent-orange.log");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _file = null; // Logging must never stop the app.
            return;
        }

        try
        {
            // One older log is kept, so this never grows without limit.
            if (File.Exists(_file) && new FileInfo(_file).Length > MaxBytes) File.Move(_file, Path.ChangeExtension(_file, ".old.log"), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another window is writing to it, and it can be moved next time.
        }
    }

    /// <summary>
    /// Notes earlier runs whose start is in the log with no end after it. A run ends with a line
    /// saying so whatever closes it, so one without was ended from outside (Task Manager, a dotnet
    /// watch restart, Windows) or crashed in a way that couldn't be written down. Runs still going,
    /// in other windows of the app, are left out.
    /// </summary>
    public static void NoteSilentEnds()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            string tail;
            using (var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Math.Max(0, stream.Length - 500_000), SeekOrigin.Begin);
                tail = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
            }

            var unended = new Dictionary<int, string>();
            foreach (Match line in RunLine.Matches(tail))
            {
                var process = int.Parse(line.Groups["process"].Value);
                if (line.Groups["what"].Value == "Started") unended[process] = line.Groups["at"].Value;
                else unended.Remove(process);
            }
            foreach (var (process, at) in unended)
            {
                if (process == Environment.ProcessId || IsRunning(process)) continue;
                Write($"Ended with no word on why (process {process}), the run started at {at}: something outside it ended it, such as Task Manager, a dotnet watch restart or Windows, or it crashed in a way that couldn't be written down. Found when starting again.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app.
        }
    }

    private static readonly Regex RunLine = new(@"^(?<at>\S+ \S+) (?<what>Started|Ended)\b[^\r\n]*\(process (?<process>\d+)\)", RegexOptions.Multiline);

    private static bool IsRunning(int process)
    {
        try
        {
            using var running = System.Diagnostics.Process.GetProcessById(process);
            return !running.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return error is System.ComponentModel.Win32Exception; // It's there, but can't be asked.
        }
    }

    public static void Write(string message)
    {
        if (_file is null) return;
        try
        {
            using var log = new FileInfo(_file).Create(FileMode.Append, FileSystemRights.AppendData, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None, null);
            log.Write(Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app.
        }
    }
}
