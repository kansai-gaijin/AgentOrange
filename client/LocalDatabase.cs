using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentOrange;

public sealed record Project(string Id, string Name, string Engine, DateTimeOffset CreatedAt, string RepoUrl)
{
    public string Summary =>
        $"{Engine} · Created {CreatedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";
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
    private const string UnknownEngine = "Other";
    private sealed record ProjectData(string Name, DateTimeOffset CreatedAt, string? Engine = null, string? RepoUrl = null);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;

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

        var projects = new List<Project>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var data = JsonSerializer.Deserialize<ProjectData>(reader.GetString(1), JsonOptions)!;
            projects.Add(new Project(reader.GetString(0), data.Name, data.Engine ?? UnknownEngine, data.CreatedAt, data.RepoUrl ?? ""));
        }
        return projects.OrderByDescending(project => project.CreatedAt).ToList();
    }

    public string CreateProject(string name, string engine, string repoUrl)
    {
        var id = Guid.NewGuid().ToString();
        var data = new ProjectData(name, DateTimeOffset.UtcNow, engine, repoUrl);
        SaveRecord(ProjectsCollection, id, JsonSerializer.Serialize(data, JsonOptions));
        return id;
    }

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

    public List<SyncChange> ReadOutbox(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT collection, id, data, deleted FROM outbox LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var changes = new List<SyncChange>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            changes.Add(new SyncChange(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
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
