using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentOrange;

public sealed record Project(string Id, string Name, string Engine, string EngineVersion, string GameType, string RepoUrl, DateTimeOffset CreatedAt)
{
    public string EngineLabel => EngineVersion.Length > 0 ? $"{Engine} {EngineVersion}" : Engine;

    public string Summary => GameType == "Other" ? EngineLabel : $"{EngineLabel} · {GameType}";
}

public sealed record SyncChange(string Collection, string Id, string Data, bool Deleted);

/// <summary>
/// This PC's own database, so the app works without the server. Everything that
/// syncs is a record of JSON text in a named collection, so new kinds of data
/// and changed shapes don't need database changes. Saving a record also queues
/// it to be sent to the server.
/// </summary>
public sealed class LocalDatabase
{
    private const string ProjectsCollection = "projects";
    private const string SettingsCollection = "settings";
    private const string ImagesCollection = "project_images";
    private const string EnginesCollection = "project_engines";
    private const string ProjectOrderRecord = "project_order";
    private const string UnknownChoice = "Other";

    private sealed record ProjectData(string Name, DateTimeOffset CreatedAt, string? Engine = null, string? RepoUrl = null, string? EngineVersion = null, string? GameType = null);

    // Its own record, because PCs read this by themselves, with no one editing anything. If it
    // were part of the project, a PC with an old copy would republish all of it. It names its
    // engine, so a version never shows beside a different engine.
    private sealed record EngineRecord(string Engine, string Version);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly Dictionary<string, Project> _reservedFolders = new(StringComparer.OrdinalIgnoreCase);

    public LocalDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS records (
                collection TEXT NOT NULL,
                id TEXT NOT NULL,
                data TEXT NOT NULL,
                deleted INTEGER NOT NULL,
                PRIMARY KEY (collection, id)
            );
            CREATE TABLE IF NOT EXISTS outbox (
                collection TEXT NOT NULL,
                id TEXT NOT NULL,
                data TEXT NOT NULL,
                deleted INTEGER NOT NULL,
                PRIMARY KEY (collection, id)
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        MoveOldProjectsTable(connection);
    }

    public List<Project> ListProjects()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, data FROM records WHERE collection = $collection AND deleted = 0";
        command.Parameters.AddWithValue("$collection", ProjectsCollection);

        var engines = ReadEngineRecords(connection);
        var projects = new List<Project>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // Records arrive from other PCs, so one bad record must not stop the app.
            ProjectData? data;
            try
            {
                data = JsonSerializer.Deserialize<ProjectData>(reader.GetString(1), JsonOptions);
            }
            catch (JsonException error)
            {
                AppLog.Write($"Skipped an unreadable project: {error.Message}");
                continue;
            }
            if (data?.Name is null) continue;

            var id = reader.GetString(0);
            var engine = data.Engine ?? UnknownChoice;
            var version = engines.TryGetValue(id, out var found) ? (found.Engine == engine ? found.Version : "") : data.EngineVersion ?? "";

            projects.Add(new Project(
                id,
                data.Name,
                engine,
                version,
                data.GameType ?? UnknownChoice,
                data.RepoUrl ?? "",
                data.CreatedAt));
        }

        // Projects the saved order doesn't mention yet, such as new ones, go first.
        var order = ProjectOrder();
        return projects
            .OrderBy(project => order.IndexOf(project.Id) is var index and >= 0 ? index : -1)
            .ThenByDescending(project => project.CreatedAt)
            .ToList();
    }

    public string CreateProject(string name, string engine, string gameType, string repoUrl)
    {
        var project = new Project(Guid.NewGuid().ToString(), name, engine, "", gameType, repoUrl, DateTimeOffset.UtcNow);
        SaveProject(project);
        return project.Id;
    }

    public void SaveProject(Project project)
    {
        var data = new ProjectData(project.Name, project.CreatedAt, project.Engine, project.RepoUrl, null, project.GameType);
        SaveRecord(ProjectsCollection, project.Id, JsonSerializer.Serialize(data, JsonOptions));
    }

    /// <summary>What the project's own files say. Only ever written when it has changed.</summary>
    public void SetEngineVersion(string projectId, string engine, string version)
    {
        var data = JsonSerializer.Serialize(new EngineRecord(engine, version), JsonOptions);
        if (GetRecord(EnginesCollection, projectId) != data) SaveRecord(EnginesCollection, projectId, data);
    }

    private static Dictionary<string, EngineRecord> ReadEngineRecords(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, data FROM records WHERE collection = $collection AND deleted = 0";
        command.Parameters.AddWithValue("$collection", EnginesCollection);

        var engines = new Dictionary<string, EngineRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<EngineRecord>(reader.GetString(1), JsonOptions) is { Engine: not null, Version: not null } record)
                {
                    engines[reader.GetString(0)] = record;
                }
            }
            catch (JsonException error)
            {
                AppLog.Write($"Skipped an unreadable engine version: {error.Message}");
            }
        }
        return engines;
    }

    /// <summary>Removes a project from every PC. Its folder and repo are left alone.</summary>
    public void DeleteProject(string projectId)
    {
        SaveRecord(ProjectsCollection, projectId, "{}", deleted: true);
        foreach (var kind in Enum.GetValues<ProjectImageKind>()) SetProjectImage(projectId, kind, null);
        SaveRecord(EnginesCollection, projectId, "", deleted: true);

        var order = ProjectOrder();
        if (order.Remove(projectId)) SaveProjectOrder(order);

        SetSetting($"project_folder:{projectId}", "");
        SetSetting($"project_verified:{projectId}", "");
    }

    /// <summary>
    /// A project deleted on another PC leaves things behind here: its folder link, and any
    /// image that was on its way when it was deleted, which would stay on the server forever.
    /// </summary>
    public void CleanUpDeletedProjects()
    {
        var deleted = new List<string>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM records WHERE collection = $collection AND deleted = 1";
            command.Parameters.AddWithValue("$collection", ProjectsCollection);
            using var reader = command.ExecuteReader();
            while (reader.Read()) deleted.Add(reader.GetString(0));
        }

        foreach (var projectId in deleted)
        {
            foreach (var kind in Enum.GetValues<ProjectImageKind>())
            {
                if (GetRecord(ImagesCollection, ImageId(projectId, kind)) is not null) SetProjectImage(projectId, kind, null);
            }
            if (GetRecord(EnginesCollection, projectId) is not null) SaveRecord(EnginesCollection, projectId, "", deleted: true);
            foreach (var setting in new[] { $"project_folder:{projectId}", $"project_verified:{projectId}" })
            {
                if (!string.IsNullOrEmpty(GetSetting(setting))) SetSetting(setting, "");
            }
        }
    }

    /// <summary>Holds a folder for a project during slow work, so nothing else can claim it before it's linked.</summary>
    public void ReserveFolder(string folder, Project project) => _reservedFolders[folder] = project;

    public void ReleaseFolder(string folder) => _reservedFolders.Remove(folder);

    /// <summary>A folder can only be one project, so this finds who already has it, by any path.</summary>
    public async Task<Project?> ProjectUsingFolderAsync(string folder, string? exceptProjectId = null)
    {
        var linked = ListProjects()
            .Where(project => project.Id != exceptProjectId)
            .Select(project => (Project: project, Folder: GetProjectFolder(project.Id)))
            .Where(link => link.Folder is not null)
            .Concat(_reservedFolders
                .Where(reserved => reserved.Value.Id != exceptProjectId)
                .Select(reserved => (Project: reserved.Value, Folder: (string?)reserved.Key)))
            .ToList();

        return await Task.Run(() => linked.FirstOrDefault(link => FolderIdentity.Same(link.Folder!, folder)).Project);
    }

    public void SaveProjectOrder(IEnumerable<string> projectIds) =>
        SaveRecord(SettingsCollection, ProjectOrderRecord, JsonSerializer.Serialize(projectIds, JsonOptions));

    private List<string> ProjectOrder()
    {
        var stored = GetRecord(SettingsCollection, ProjectOrderRecord);
        if (string.IsNullOrEmpty(stored)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(stored, JsonOptions) ?? [];
        }
        catch (JsonException error)
        {
            AppLog.Write($"Skipped an unreadable project order: {error.Message}");
            return [];
        }
    }

    /// <summary>Folders differ between PCs, so they stay on this one and never sync.</summary>
    public string? GetProjectFolder(string projectId)
    {
        var folder = GetSetting($"project_folder:{projectId}");
        return string.IsNullOrEmpty(folder) ? null : folder;
    }

    public void SetProjectFolder(string projectId, string folder) => SetSetting($"project_folder:{projectId}", folder);

    public byte[]? GetProjectImage(string projectId, ProjectImageKind kind)
    {
        var stored = GetRecord(ImagesCollection, ImageId(projectId, kind));
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            return Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            AppLog.Write("Skipped an unreadable project image.");
            return null;
        }
    }

    public void SetProjectImage(string projectId, ProjectImageKind kind, byte[]? image) =>
        SaveRecord(ImagesCollection, ImageId(projectId, kind), image is null ? "" : Convert.ToBase64String(image), deleted: image is null);

    private static string ImageId(string projectId, ProjectImageKind kind) => $"{projectId}:{kind}";

    public void SaveRecord(string collection, string id, string data, bool deleted = false)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Upsert(connection, "records", new SyncChange(collection, id, data, deleted));
        Upsert(connection, "outbox", new SyncChange(collection, id, data, deleted));
        transaction.Commit();
    }

    public string? GetRecord(string collection, string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM records WHERE collection = $collection AND id = $id AND deleted = 0";
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Reads queued changes, stopping at a size the server accepts in one request.</summary>
    public List<SyncChange> ReadOutbox(int limit, int maxBytes)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT collection, id, data, deleted FROM outbox LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var changes = new List<SyncChange>();
        var total = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var data = reader.GetString(2);
            var size = JsonEncodedText.Encode(data).EncodedUtf8Bytes.Length; // What it takes up once it's sent.
            if (changes.Count > 0 && total + size > maxBytes) break;
            total += size;
            changes.Add(new SyncChange(reader.GetString(0), reader.GetString(1), data, reader.GetInt64(3) != 0));
        }
        return changes;
    }

    public int CountOutbox()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbox";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Clears changes the server now has, unless they were edited again while sending.</summary>
    public void RemoveFromOutbox(IEnumerable<SyncChange> sent)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var change in sent)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM outbox
                WHERE collection = $collection AND id = $id AND data = $data AND deleted = $deleted
                """;
            AddChange(command, change);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Saves changes from the server. Local edits still waiting to be sent win for now.</summary>
    public int ApplyFromServer(IEnumerable<SyncChange> changes)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var applied = 0;
        foreach (var change in changes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO records (collection, id, data, deleted)
                SELECT $collection, $id, $data, $deleted
                WHERE NOT EXISTS (SELECT 1 FROM outbox WHERE collection = $collection AND id = $id)
                  AND NOT EXISTS (SELECT 1 FROM records WHERE collection = $collection AND id = $id AND data = $data AND deleted = $deleted)
                ON CONFLICT (collection, id) DO UPDATE SET data = excluded.data, deleted = excluded.deleted
                """;
            AddChange(command, change);
            applied += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return applied;
    }

    /// <summary>Queues everything, for the first sync with a server that may not have it.</summary>
    public void QueueAllRecords()
    {
        using var connection = Open();
        Execute(connection, "INSERT OR REPLACE INTO outbox SELECT collection, id, data, deleted FROM records");
    }

    /// <summary>
    /// A server that lost its data gets everything sent again. All in one step, because
    /// if only the new epoch were saved, this would never be noticed again after a crash.
    /// </summary>
    public void StartOverWithServer(string epochSetting, string epoch, string cursorSetting)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, "INSERT OR REPLACE INTO outbox SELECT collection, id, data, deleted FROM records");
        WriteSetting(connection, epochSetting, epoch);
        WriteSetting(connection, cursorSetting, "0");
        transaction.Commit();
    }

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        WriteSetting(connection, key, value);
    }

    private static void WriteSetting(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    // Projects used to have a table of their own.
    private static void MoveOldProjectsTable(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'projects'";
        if (check.ExecuteScalar() is null) return;

        using var transaction = connection.BeginTransaction();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, name, created_at FROM projects";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var data = new ProjectData(reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)));
                Upsert(connection, "records", new SyncChange(ProjectsCollection, reader.GetString(0), JsonSerializer.Serialize(data, JsonOptions), false));
            }
        }
        Execute(connection, "DROP TABLE projects");
        transaction.Commit();
    }

    private static void Upsert(SqliteConnection connection, string table, SyncChange change)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {table} (collection, id, data, deleted) VALUES ($collection, $id, $data, $deleted)
            ON CONFLICT (collection, id) DO UPDATE SET data = excluded.data, deleted = excluded.deleted
            """;
        AddChange(command, change);
        command.ExecuteNonQuery();
    }

    private static void AddChange(SqliteCommand command, SyncChange change)
    {
        command.Parameters.AddWithValue("$collection", change.Collection);
        command.Parameters.AddWithValue("$id", change.Id);
        command.Parameters.AddWithValue("$data", change.Data);
        command.Parameters.AddWithValue("$deleted", change.Deleted ? 1 : 0);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
