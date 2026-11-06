using System.IO;
using Microsoft.Data.Sqlite;

namespace AgentOrange;

/// <summary>Where this PC keeps its data, which people can change.</summary>
public sealed class AppFolders
{
    private const string DatabaseFileName = "agent-orange.db";
    private const string DownloadsSetting = "downloads_folder";

    private readonly string _appFolder;
    private readonly string _pointerFile;

    public AppFolders(bool installed)
    {
        // Dev builds get their own folder so testing never touches real projects.
        _appFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            installed ? "AgentOrange" : "AgentOrange-Dev");
#if DEBUG
        if (Headless.Active) _appFolder = Headless.DataFolder;
#endif
        Directory.CreateDirectory(_appFolder);

        // The database can move, so its location has to be kept outside it.
        _pointerFile = Path.Combine(_appFolder, "database-location.txt");

        AgentFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            installed ? ".agent-orange" : ".agent-orange-dev");
#if DEBUG
        if (Headless.Active) AgentFolder = Path.Combine(Headless.DataFolder, "agent");
#endif
        Directory.CreateDirectory(AgentFolder);
    }

    /// <summary>
    /// What agent sessions load, such as tools and skills, plus things like chat
    /// caches. It keeps these out of login folders, which other apps also use.
    /// </summary>
    public string AgentFolder { get; }

    public string LogsFolder => Path.Combine(_appFolder, "logs");

    public string DatabaseFolder =>
        File.Exists(_pointerFile) ? File.ReadAllText(_pointerFile).Trim() : _appFolder;

    public string DatabasePath => Path.Combine(DatabaseFolder, DatabaseFileName);

    public bool DatabaseFolderExists => Directory.Exists(DatabaseFolder);

    public long DatabaseSize => File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;

    public void UseDefaultDatabaseFolder() => File.Delete(_pointerFile);

    /// <summary>Moves the database and returns a problem to show, or null if it worked.</summary>
    public string? MoveDatabase(string newFolder)
    {
        var oldPath = DatabasePath;
        var newPath = Path.Combine(newFolder, DatabaseFileName);
        if (File.Exists(newPath)) return "That folder already has an Agent Orange database.";

        try
        {
            SqliteConnection.ClearAllPools();
            File.Copy(oldPath, newPath);
            File.WriteAllText(_pointerFile, newFolder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Couldn't move the database to that folder.";
        }

        try
        {
            File.Delete(oldPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The move worked. This only leaves an old copy behind.
        }
        return null;
    }

    public string DownloadsFolder(LocalDatabase database) =>
        database.GetSetting(DownloadsSetting) ?? Path.Combine(_appFolder, "downloads");

    public void SetDownloadsFolder(LocalDatabase database, string folder) =>
        database.SetSetting(DownloadsSetting, folder);

    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024)} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
    };
}
