using System.IO;
using System.Text;

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

    public static void Write(string message)
    {
        if (_file is null) return;
        try
        {
            using var log = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            log.Write(Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app.
        }
    }
}
