using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace AgentOrange;

public sealed record Project(string Id, string Name, DateTimeOffset CreatedAt)
{
    public string CreatedLabel =>
        "Created " + CreatedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Keeps projects in a local database, so the app works without the server.</summary>
public sealed class ProjectStore
{
    private readonly string _connectionString;

    public ProjectStore(string databasePath)
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
            )
            """;
        command.ExecuteNonQuery();
    }

    public List<Project> List()
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

    public Project Create(string name)
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

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
