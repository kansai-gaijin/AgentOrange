using System.IO;

namespace AgentOrange;

/// <summary>A plain text log, for finding out what happened after something goes wrong.</summary>
public static class AppLog
{
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
        }
    }

    public static void Write(string message)
    {
        if (_file is null) return;
        try
        {
            File.AppendAllText(_file, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app.
        }
    }
}
