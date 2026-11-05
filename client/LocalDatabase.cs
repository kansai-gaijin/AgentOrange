using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace AgentOrange;

public sealed record Project(string Id, string Name, DateTimeOffset CreatedAt)
{
    public string CreatedLabel =>
        "Created " + CreatedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
}

/// <summary>This PC's own database, so the app works without the server.</summary>
public sealed class LocalDatabase
{
    private readonly string _connectionString;

    public LocalDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS projects (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public List<Project> ListProjects()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_at FROM projects ORDER BY created_at DESC";

        var projects = new List<Project>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            projects.Add(new Project(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2))));
        }
        return projects;
    }

    public Project CreateProject(string name)
    {
        var project = new Project(Guid.NewGuid().ToString(), name, DateTimeOffset.UtcNow);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO projects (id, name, created_at) VALUES ($id, $name, $createdAt)";
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$createdAt", project.CreatedAt.ToString("O"));
        command.ExecuteNonQuery();
        return project;
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

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
