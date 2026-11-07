using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AgentOrange;

public sealed record Project(string Id, string Name, string Engine, string EngineVersion, string GameType, string RepoUrl, DateTimeOffset CreatedAt)
{
    public string EngineLabel => EngineVersion.Length > 0 ? $"{Engine} {EngineVersion}" : Engine;

    public string Summary => GameType == "Other" ? EngineLabel : $"{EngineLabel} · {GameType}";
}

/// <summary>
/// ChangedAt is when this copy was made, in milliseconds. Resend means it's only being sent again
/// because a server lost its data, so it mustn't replace anything newer.
/// </summary>
public sealed record SyncChange(string Collection, string Id, string Data, bool Deleted, long ChangedAt = 0, bool Resend = false);

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

    // Edits to a project, one record per field, so two PCs changing different things both
    // count. The project's own record is only written to create or delete it, so an edit made
    // offline can't bring back a project that was deleted meanwhile.
    private const string FieldsCollection = "project_fields";
    private const string NameField = "name";
    private const string EngineField = "engine";
    private const string GameTypeField = "gameType";
    private const string RepoUrlField = "repoUrl";
    private static readonly string[] ProjectFields = [NameField, EngineField, GameTypeField, RepoUrlField];
    private const string ProjectOrderRecord = "project_order";
    private const string UnknownChoice = "Other";

    private sealed record ProjectData(string Name, DateTimeOffset CreatedAt, string? Engine = null, string? RepoUrl = null, string? EngineVersion = null, string? GameType = null);

    // Its own record, because PCs read this by themselves, with no one editing anything. If it
    // were part of the project, a PC with an old copy would republish all of it. It names its
    // engine, so a version never shows beside a different engine.
    private sealed record EngineRecord(string Engine, string Version);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly string _claimTurnName;
    private readonly Dictionary<string, (string Token, Mutex Alive, string ProjectId)> _holds = new(StringComparer.OrdinalIgnoreCase);

    private const int DamagedErrorCode = 11;
    private const int NotADatabaseErrorCode = 26;

    public static bool MeansDamaged(SqliteException error) => error.SqliteErrorCode is DamagedErrorCode or NotADatabaseErrorCode;

    /// <summary>Moves a damaged database out of the way, under a name that says so. Nothing is deleted, as it can still hold changes that hadn't synced.</summary>
    public static string SetAside(string databasePath)
    {
        SqliteConnection.ClearAllPools(); // The failed attempt can still be holding the file.
        var kept = $"{databasePath}.damaged-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        File.Move(databasePath, kept);
        foreach (var part in new[] { "-journal", "-wal", "-shm" })
        {
            if (File.Exists(databasePath + part)) File.Move(databasePath + part, kept + part);
        }
        return kept;
    }

    public LocalDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _claimTurnName = $@"Local\AgentOrangeClaims-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(databasePath).ToLowerInvariant())))[..32]}";

        using var connection = Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check(1)";
            if (check.ExecuteScalar() as string is not "ok") throw new SqliteException("The database is damaged.", DamagedErrorCode);
        }

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
        AddColumn(connection, "records", "changed_at INTEGER NOT NULL DEFAULT 0");
        AddColumn(connection, "outbox", "changed_at INTEGER NOT NULL DEFAULT 0");
        AddColumn(connection, "outbox", "resend INTEGER NOT NULL DEFAULT 0");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS refused (collection TEXT NOT NULL, id TEXT NOT NULL, PRIMARY KEY (collection, id));
            CREATE INDEX IF NOT EXISTS records_changed_at ON records (changed_at);

            CREATE TABLE IF NOT EXISTS holds (token TEXT PRIMARY KEY, kind TEXT NOT NULL, target TEXT NOT NULL, project_id TEXT NOT NULL, project_name TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS revisions (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            INSERT OR IGNORE INTO revisions (name, value) VALUES ('records', 0);
            CREATE TRIGGER IF NOT EXISTS records_added AFTER INSERT ON records BEGIN UPDATE revisions SET value = value + 1 WHERE name = 'records'; END;
            CREATE TRIGGER IF NOT EXISTS records_changed AFTER UPDATE ON records BEGIN UPDATE revisions SET value = value + 1 WHERE name = 'records'; END;
            CREATE TRIGGER IF NOT EXISTS records_removed AFTER DELETE ON records BEGIN UPDATE revisions SET value = value + 1 WHERE name = 'records'; END;
            """);
        MoveOldProjectsTable(connection);
        MoveFolderKeys(connection);
    }

    // Approvals and what a folder's been seen to share, kept under the path as it was written. One
    // already under the new key stays. Two old spellings of one folder keep whichever comes first,
    // which may not be the newer, and the worst of that is an approval being asked for again.
    private static void MoveFolderKeys(SqliteConnection connection)
    {
        var old = new List<(string Key, string Value)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT key, value FROM settings WHERE key LIKE 'folder_settings_allowed:%' OR key LIKE 'folder_shares_repo:%'";
            using var reader = read.ExecuteReader();
            while (reader.Read()) old.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (key, value) in old)
        {
            // An approval for finishing a download names the folder, then what it's for.
            var split = key.IndexOf(':');
            var rest = key[(split + 1)..];
            var forFinishing = rest.EndsWith("|finish", StringComparison.Ordinal);
            var moved = $"{key[..split]}:{SameFolderKey(forFinishing ? rest[..^"|finish".Length] : rest)}{(forFinishing ? "|finish" : "")}";
            if (moved == key) continue;

            using var move = connection.CreateCommand();
            move.CommandText = "INSERT OR IGNORE INTO settings (key, value) VALUES ($moved, $value); DELETE FROM settings WHERE key = $key";
            move.Parameters.AddWithValue("$moved", moved);
            move.Parameters.AddWithValue("$value", value);
            move.Parameters.AddWithValue("$key", key);
            move.ExecuteNonQuery();
        }
    }

    public List<Project> ListProjects()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, data FROM records WHERE collection = $collection AND deleted = 0";
        command.Parameters.AddWithValue("$collection", ProjectsCollection);

        var engines = ReadEngineRecords(connection);
        var enginesHere = ReadEnginesHere(connection);
        var fields = ReadProjectFields(connection);
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
            string Latest(string field, string? original) => fields.GetValueOrDefault((id, field)) ?? original ?? "";

            var engine = Latest(EngineField, data.Engine ?? UnknownChoice);

            // What this PC's own copy says comes first, as other PCs can be on other branches.
            var version = enginesHere.TryGetValue(id, out var here) && here.Engine == engine ? here.Version
                : engines.TryGetValue(id, out var found) ? (found.Engine == engine ? found.Version : "")
                : data.EngineVersion ?? "";

            projects.Add(new Project(
                id,
                Latest(NameField, data.Name),
                engine,
                version,
                Latest(GameTypeField, data.GameType ?? UnknownChoice),
                Latest(RepoUrlField, data.RepoUrl),
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
        var id = Guid.NewGuid().ToString();
        var data = new ProjectData(name, DateTimeOffset.UtcNow, engine, repoUrl, null, gameType);
        SaveRecord(ProjectsCollection, id, JsonSerializer.Serialize(data, JsonOptions));
        return id;
    }

    /// <summary>
    /// Saves only what's changed, made to the project as it is now, as another window or a sync can
    /// have changed the rest since it was last read. A project that's been deleted stays deleted.
    /// </summary>
    public void UpdateProject(string projectId, Func<Project, Project> change)
    {
        if (ListProjects().FirstOrDefault(current => current.Id == projectId) is not { } current) return;
        var project = change(current);

        void Save(string field, string before, string after)
        {
            if (before != after) SaveRecord(FieldsCollection, FieldId(project.Id, field), after);
        }
        Save(NameField, current.Name, project.Name);
        Save(EngineField, current.Engine, project.Engine);
        Save(GameTypeField, current.GameType, project.GameType);
        Save(RepoUrlField, current.RepoUrl, project.RepoUrl);
    }

    private static string FieldId(string projectId, string field) => $"{projectId}:{field}";

    private static Dictionary<(string ProjectId, string Field), string> ReadProjectFields(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, data FROM records WHERE collection = $collection AND deleted = 0";
        command.Parameters.AddWithValue("$collection", FieldsCollection);

        var fields = new Dictionary<(string, string), string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var split = id.LastIndexOf(':');
            if (split > 0) fields[(id[..split], id[(split + 1)..])] = reader.GetString(1);
        }
        return fields;
    }

    /// <summary>
    /// What the project's own files say on this PC, which it shows for itself. PCs without a copy
    /// see the newest any PC has found, so two PCs on different branches don't keep overwriting
    /// each other's answer.
    /// </summary>
    public void SetEngineVersion(string projectId, string engine, string version)
    {
        SetSetting(EngineHereKey(projectId), JsonSerializer.Serialize(new EngineRecord(engine, version), JsonOptions));

        var synced = GetRecord(EnginesCollection, projectId) is { Length: > 0 } stored ? ParseEngineRecord(stored) : null;
        if (synced is null || synced.Engine != engine || IsNewerVersion(version, synced.Version))
        {
            SaveRecord(EnginesCollection, projectId, JsonSerializer.Serialize(new EngineRecord(engine, version), JsonOptions));
        }
    }

    /// <summary>True if there was one to forget.</summary>
    public bool ForgetEngineVersionHere(string projectId)
    {
        if (string.IsNullOrEmpty(GetSetting(EngineHereKey(projectId)))) return false;
        SetSetting(EngineHereKey(projectId), "");
        return true;
    }

    private static string EngineHereKey(string projectId) => $"engine_here:{projectId}";

    private static EngineRecord? ParseEngineRecord(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<EngineRecord>(text, JsonOptions) is { Engine: not null, Version: not null } record ? record : null;
        }
        catch (JsonException error)
        {
            AppLog.Write($"Skipped an unreadable engine version: {error.Message}");
            return null;
        }
    }

    // A version with no numbers, like a custom build, can't be said to be newer than a real one.
    private static bool IsNewerVersion(string version, string than)
    {
        if (than.Length == 0) return version.Length > 0;
        if (ReadVersion(version) is not { } a) return false;

        // A project that's moved off a custom build to a real version shouldn't stay stuck on it.
        return ReadVersion(than) is not { } b || a.CompareTo(b) > 0;
    }

    private static (Version Numbers, int Stage, int Release)? ReadVersion(string text)
    {
        var parts = VersionParts.Match(text);
        if (!parts.Success) return null;

        var numbers = parts.Groups[1].Value;
        if (!Version.TryParse(numbers.Contains('.') ? numbers : $"{numbers}.0", out var parsed)) return null;
        var version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0)); // So 5.3 and 5.3.0 are the same.

        var release = 0;
        if (parts.Groups[3].Success && !int.TryParse(parts.Groups[3].Value, out release)) return null;
        return (version, parts.Groups[2].Value switch { "x" => -1, "a" => 0, "b" => 1, "p" => 3, _ => 2 }, release);
    }

    private static readonly Regex VersionParts = new(@"^(\d+(?:\.\d+){0,3})(?:([xabfp])(\d+)(?:c\d+)?)?$", RegexOptions.CultureInvariant);

    private static Dictionary<string, EngineRecord> ReadEnginesHere(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings WHERE key LIKE 'engine_here:%' AND value <> ''";

        var engines = new Dictionary<string, EngineRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (ParseEngineRecord(reader.GetString(1)) is { } record) engines[reader.GetString(0)["engine_here:".Length..]] = record;
        }
        return engines;
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
            if (ParseEngineRecord(reader.GetString(1)) is { } record) engines[reader.GetString(0)] = record;
        }
        return engines;
    }

    /// <summary>Removes a project from every PC. Its folder and repo are left alone.</summary>
    public void DeleteProject(string projectId)
    {
        SaveRecord(ProjectsCollection, projectId, "{}", deleted: true);
        foreach (var kind in Enum.GetValues<ProjectImageKind>()) SetProjectImage(projectId, kind, null);
        SaveRecord(EnginesCollection, projectId, "", deleted: true);
        foreach (var field in ProjectFields)
        {
            if (GetRecord(FieldsCollection, FieldId(projectId, field)) is not null) SaveRecord(FieldsCollection, FieldId(projectId, field), "", deleted: true);
        }

        // The library's order stays as it is. Nobody reordered anything, so saving it would look like
        // a newer choice than one made on another PC, and a project that's gone is ignored in it.
        SetProjectFolder(projectId, "");
        SetSetting($"project_verified:{projectId}", "");
        SetSetting($"project_created_here:{projectId}", "");
    }

    /// <summary>
    /// A project deleted on another PC leaves things behind here: its folder link, and any
    /// image that was on its way when it was deleted, which would stay on the server forever.
    /// </summary>
    public void CleanUpDeletedProjects()
    {
        foreach (var projectId in DeletedProjectIds())
        {
            foreach (var kind in Enum.GetValues<ProjectImageKind>())
            {
                if (GetRecord(ImagesCollection, ImageId(projectId, kind)) is not null) SetProjectImage(projectId, kind, null);
            }
            if (GetRecord(EnginesCollection, projectId) is not null) SaveRecord(EnginesCollection, projectId, "", deleted: true);

            // An edit made elsewhere can still arrive after the delete, which wins.
            foreach (var field in ProjectFields)
            {
                if (GetRecord(FieldsCollection, FieldId(projectId, field)) is not null) SaveRecord(FieldsCollection, FieldId(projectId, field), "", deleted: true);
            }
            if (GetProjectFolder(projectId) is not null) SetProjectFolder(projectId, "");
            if (!string.IsNullOrEmpty(GetSetting($"project_verified:{projectId}"))) SetSetting($"project_verified:{projectId}", "");
            if (!string.IsNullOrEmpty(GetSetting($"project_created_here:{projectId}"))) SetSetting($"project_created_here:{projectId}", "");
        }
    }

    private const string FolderHold = "folder";
    private const string RepoHold = "repo";
    private static readonly TimeSpan LongestClaimWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Holds a folder for a project until ReleaseFolder, once it's linked or that's given up on.
    /// One claim runs at a time across every window, so two can't both find it free. Returns the
    /// project that already has it, in which case nothing is held.
    /// </summary>
    public Task<Project?> ClaimFolderAsync(string folder, Project claimant) =>
        ClaimAsync(FolderHold, folder, claimant, () => FolderOwner(folder, claimant.Id));

    public void ReleaseFolder(string folder) => Release(FolderHold, folder);

    /// <summary>Hands this window's hold on a folder to another project, such as one the wizard just made.</summary>
    public void TransferFolderHold(string folder, Project to)
    {
        string token;
        lock (_holds)
        {
            if (!_holds.TryGetValue(HoldKey(FolderHold, folder), out var held)) return;
            _holds[HoldKey(FolderHold, folder)] = (held.Token, held.Alive, to.Id);
            token = held.Token;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE holds SET project_id = $id, project_name = $name WHERE token = $token";
        command.Parameters.AddWithValue("$id", to.Id);
        command.Parameters.AddWithValue("$name", to.Name);
        command.Parameters.AddWithValue("$token", token);
        command.ExecuteNonQuery();
    }

    /// <summary>The same for a repo, until ReleaseRepo, once the project says it uses it.</summary>
    public Task<Project?> ClaimRepoAsync(string url, Project claimant) =>
        ClaimAsync(RepoHold, url, claimant, () => RepoOwner(url, claimant.Id));

    public void ReleaseRepo(string url) => Release(RepoHold, url);

    /// <summary>
    /// A folder can only be one project, so this finds who already has it, by any path. A folder
    /// inside or around another project's counts too, since their repos and engine files would mix.
    /// </summary>
    public Task<Project?> ProjectUsingFolderAsync(string folder, string? exceptProjectId = null) =>
        Task.Run(() => FolderOwner(folder, exceptProjectId));

    private Project? FolderOwner(string folder, string? exceptProjectId)
    {
        var linked = ListProjects()
            .Where(project => project.Id != exceptProjectId)
            .Select(project => (Project: project, Folder: GetProjectFolder(project.Id)))
            .Where(link => link.Folder is not null)
            .Concat(LiveHolds(FolderHold, exceptProjectId, true).Select(hold => (Project: hold.Holder, Folder: (string?)hold.Target)))
            .ToList();

        return linked.FirstOrDefault(link => FolderIdentity.Overlap(folder, link.Folder!)).Project;
    }

    private Project? RepoOwner(string url, string? exceptProjectId) =>
        ListProjects().FirstOrDefault(project => project.Id != exceptProjectId && GitTools.SameRepo(project.RepoUrl, url))
        ?? LiveHolds(RepoHold, exceptProjectId, false).FirstOrDefault(hold => GitTools.SameRepo(hold.Target, url)).Holder;

    private Task<Project?> ClaimAsync(string kind, string target, Project claimant, Func<Project?> findOwner) => Task.Run(() =>
    {
        using var turn = TakeClaimTurn();
        if (turn is null) return TurnNotAvailable;

        var owner = findOwner();
        if (owner is null) Hold(kind, target, claimant);
        return owner;
    });

    private static readonly Project AnotherWindow = new("", "Another window of the app", "", "", "", "", DateTimeOffset.MinValue);
    private static readonly Project TurnNotAvailable = new("", "", "", "", "", "", DateTimeOffset.MinValue);

    /// <summary>What a refused claim should say, as not getting a turn doesn't mean anyone has it.</summary>
    public static string RefusalFor(Project owner, string taken) =>
        ReferenceEquals(owner, TurnNotAvailable) ? "Another window of the app is busy with a slow folder, so this couldn't go ahead. Try again in a moment." : taken;

    public static bool MeansNoTurn(Project owner) => ReferenceEquals(owner, TurnNotAvailable);

    /// <summary>Already held by this window for this project, so nothing needs waiting for.</summary>
    public bool IsHoldingFolder(string folder, string projectId)
    {
        lock (_holds) return _holds.TryGetValue(HoldKey(FolderHold, folder), out var held) && held.ProjectId == projectId;
    }

    // Null if it can't be had in that time, and then the claim is refused, as two windows could
    // otherwise both find the same place free.
    private ClaimTurn? TakeClaimTurn()
    {
        var giveUpAt = DateTimeOffset.UtcNow + LongestClaimWait;
        while (true)
        {
            try
            {
                var turn = new Mutex(false, _claimTurnName);
                try
                {
                    if (turn.WaitOne(LongestClaimWait)) return new ClaimTurn(turn);
                }
                catch (AbandonedMutexException)
                {
                    return new ClaimTurn(turn); // A window closed partway through, and it's this one's now.
                }
                turn.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                // A window running as administrator is taking its turn, which only lasts a moment.
                if (DateTimeOffset.UtcNow > giveUpAt) return null;
                Thread.Sleep(200);
            }
        }
    }

    private sealed class ClaimTurn(Mutex turn) : IDisposable
    {
        public void Dispose()
        {
            turn.ReleaseMutex();
            turn.Dispose();
        }
    }

    private static string HoldHandleName(string token) => $@"Local\AgentOrangeHold-{token}";

    // Each has a handle that only stays while its window does, so one left by a window that's gone
    // can be told apart, and doesn't hold anything up.
    private void Hold(string kind, string target, Project claimant)
    {
        Release(kind, target);

        var token = Guid.NewGuid().ToString("N");
        var alive = new Mutex(false, HoldHandleName(token));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO holds (token, kind, target, project_id, project_name) VALUES ($token, $kind, $target, $id, $name)";
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$id", claimant.Id);
        command.Parameters.AddWithValue("$name", claimant.Name);
        command.ExecuteNonQuery();
        lock (_holds) _holds[HoldKey(kind, target)] = (token, alive, claimant.Id);
    }

    private void Release(string kind, string target)
    {
        (string Token, Mutex Alive, string ProjectId) held;
        lock (_holds)
        {
            if (!_holds.Remove(HoldKey(kind, target), out held)) return;
        }
        held.Alive.Dispose();

        try
        {
            RemoveHold(held.Token);
        }
        catch (SqliteException error)
        {
            AppLog.Write($"Couldn't clear a hold, which clears itself next time it's looked at: {error.Message}");
        }
    }

    private void RemoveHold(string token)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM holds WHERE token = $token";
        command.Parameters.AddWithValue("$token", token);
        command.ExecuteNonQuery();
    }

    // A window's own holds for that project never count. Its other windows' do if asked, as two
    // windows saving one repo to one project is harmless, and two cloning into one folder isn't.
    private List<(string Target, Project Holder)> LiveHolds(string kind, string? exceptProjectId, bool otherWindowsCount)
    {
        var found = new List<(string Token, string Target, string Id, string Name)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT token, target, project_id, project_name FROM holds WHERE kind = $kind";
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            while (reader.Read()) found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        var live = new List<(string Target, Project Holder)>();
        foreach (var hold in found)
        {
            if (!HoldIsAlive(hold.Token))
            {
                RemoveHold(hold.Token);
                continue;
            }
            if (hold.Id != exceptProjectId) live.Add((hold.Target, new Project(hold.Id, hold.Name, "", "", "", "", DateTimeOffset.UtcNow)));
            else if (otherWindowsCount && !IsThisWindows(hold.Token)) live.Add((hold.Target, AnotherWindow));
        }
        return live;
    }

    private bool IsThisWindows(string token)
    {
        lock (_holds) return _holds.Values.Any(held => held.Token == token);
    }

    private static bool HoldIsAlive(string token)
    {
        try
        {
            if (!Mutex.TryOpenExisting(HoldHandleName(token), out var hold)) return false;
            hold.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // There is one, and it belongs to a window running as administrator.
        }
    }

    /// <summary>Projects this PC hasn't received yet keep their place in line, behind these.</summary>
    public void SaveProjectOrder(IEnumerable<string> projectIds)
    {
        var order = projectIds.ToList();
        order.AddRange(ProjectOrder().Except(order));

        // Ones this PC hasn't received yet stay in it, and ones it knows are gone don't.
        var gone = DeletedProjectIds();
        SaveRecord(SettingsCollection, ProjectOrderRecord, JsonSerializer.Serialize(order.Where(id => !gone.Contains(id)), JsonOptions));
    }

    private HashSet<string> DeletedProjectIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM records WHERE collection = $collection AND deleted = 1";
        command.Parameters.AddWithValue("$collection", ProjectsCollection);
        var deleted = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) deleted.Add(reader.GetString(0));
        return deleted;
    }

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

    /// <summary>Changes whenever a synced record, or this PC's folder or engine version for a project, does, including from another window of the app.</summary>
    public string RecordsSignature()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT value FROM revisions WHERE name = 'records') || ':' || COALESCE((SELECT group_concat(value, '|') FROM settings WHERE key LIKE 'project_folder:%' OR key LIKE 'engine_here:%'), '')";
        return command.ExecuteScalar() as string ?? "";
    }

    /// <summary>
    /// Says one PC is the same PC at each of a server's addresses, so it's one device there. Not
    /// a login: it gets nothing without the pairing key, and the server only keeps a hash of it.
    /// </summary>
    public string InstallId()
    {
        const string setting = "install_id";
        if (GetSetting(setting) is { Length: > 0 } known) return known;
        var id = Guid.NewGuid().ToString("N");
        SetSetting(setting, id);
        return id;
    }

    /// <summary>A server's data started over, and its other addresses are still that same server.</summary>
    public void ReplaceEpoch(string old, string latest)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE settings SET value = $latest WHERE key LIKE 'sync_epoch:%' AND value = $old";
        command.Parameters.AddWithValue("$latest", latest);
        command.Parameters.AddWithValue("$old", old);
        command.ExecuteNonQuery();
    }

    /// <summary>Every server address this PC has been paired with, whichever it's using now.</summary>
    public List<string> AddressesWithLogin()
    {
        const string prefix = "device_token:";
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key FROM settings WHERE key LIKE 'device_token:%' AND value <> ''";
        var addresses = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) addresses.Add(reader.GetString(0)[prefix.Length..]);
        return addresses;
    }

    /// <summary>Folders differ between PCs, so they stay on this one and never sync.</summary>
    public string? GetProjectFolder(string projectId)
    {
        var folder = GetSetting($"project_folder:{projectId}");
        return string.IsNullOrEmpty(folder) ? null : folder;
    }

    public void SetProjectFolder(string projectId, string folder)
    {
        // A folder connected again later gets looked at again, including its own Git settings.
        if (GetProjectFolder(projectId) is { } old && SameFolderKey(old) != SameFolderKey(folder))
        {
            SetSetting(AllowedSettingsKey(old), "");
            SetSetting(AllowedSettingsKey(GitTools.FinishApprovalKey(old)), "");
            SetSetting(SharesRepoKey(old), "");
            SetSetting(EngineHereKey(projectId), "");
        }
        SetSetting($"project_folder:{projectId}", folder);
    }

    /// <summary>A clone whose large files or submodules may not all have arrived. By folder, as it may not be linked to anything yet.</summary>
    public bool DownloadIsUnfinished(string folder) => GetSetting(UnfinishedDownloadKey(folder)) == "1";

    public void SetDownloadUnfinished(string folder, bool unfinished) => SetSetting(UnfinishedDownloadKey(folder), unfinished ? "1" : "");

    /// <summary>What only a clone from this PC has in its folder, so it can be recognised after being moved.</summary>
    public bool IsUnfinishedNoteFromHere(string token) => GetSetting($"download_note:{token}") == "1";

    public void RememberUnfinishedNote(string token, bool unfinished) => SetSetting($"download_note:{token}", unfinished ? "1" : "");

    private static string UnfinishedDownloadKey(string folder) => $"download_unfinished:{SameFolderKey(folder)}";

    /// <summary>Whether this folder has been seen to share a commit with this repo, which stands until it's disconnected.</summary>
    public bool FolderSharesRepo(string folder, string repoKey) => GetSetting(SharesRepoKey(folder)) == repoKey;

    public void RememberFolderSharesRepo(string folder, string repoKey) => SetSetting(SharesRepoKey(folder), repoKey);

    /// <summary>The repo may be gone, and whatever's made at that address next is a different one.</summary>
    public void ForgetFolderSharesRepo(string folder) => SetSetting(SharesRepoKey(folder), "");

    private static string SharesRepoKey(string folder) => $"folder_shares_repo:{SameFolderKey(folder)}";

    private static string SameFolderKey(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToLowerInvariant();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.TrimEndingDirectorySeparator(folder).ToLowerInvariant();
        }
    }

    private static string HoldKey(string kind, string target) => kind == FolderHold ? $"{kind}:{SameFolderKey(target)}" : $"{kind}:{target}";

    /// <summary>The fingerprint of a folder's own Git settings that the person has allowed on this PC.</summary>
    public string? AllowedFolderSettings(string folder) => GetSetting(AllowedSettingsKey(folder));

    public void AllowFolderSettings(string folder, string fingerprint) => SetSetting(AllowedSettingsKey(folder), fingerprint);

    private static string AllowedSettingsKey(string folder) => $"folder_settings_allowed:{SameFolderKey(folder)}";

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

        // Always later than what's being replaced, so a PC whose clock runs slow can't lose an edit
        // it made after seeing the one before it.
        long previous;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT changed_at FROM records WHERE collection = $collection AND id = $id";
            read.Parameters.AddWithValue("$collection", collection);
            read.Parameters.AddWithValue("$id", id);
            previous = read.ExecuteScalar() is long known ? known : 0;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Dated by a clock that was wrong, as it was refused for it. Nothing has to come after that,
        // and starting from it would get every edit from now on refused too.
        var wasBadTime = false;
        if (previous > now + DayMilliseconds)
        {
            using var refused = connection.CreateCommand();
            refused.CommandText = "SELECT 1 FROM refused WHERE collection = $collection AND id = $id";
            refused.Parameters.AddWithValue("$collection", collection);
            refused.Parameters.AddWithValue("$id", id);
            wasBadTime = refused.ExecuteScalar() is not null;
        }
        var change = new SyncChange(collection, id, data, deleted, wasBadTime ? now : Math.Max(now, previous + 1));
        Upsert(connection, "records", change);
        Upsert(connection, "outbox", change);

        // It's a new value now, and the server gets a fresh chance to take it.
        using (var forgive = connection.CreateCommand())
        {
            forgive.CommandText = "DELETE FROM refused WHERE collection = $collection AND id = $id";
            forgive.Parameters.AddWithValue("$collection", collection);
            forgive.Parameters.AddWithValue("$id", id);
            forgive.ExecuteNonQuery();
        }
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

    private const long DayMilliseconds = 24 * 60 * 60 * 1000;

    /// <summary>Reads queued changes, stopping at a size the server accepts in one request.</summary>
    public List<SyncChange> ReadOutbox(int limit, int maxBytes)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT collection, id, data, deleted, changed_at, resend FROM outbox LIMIT $limit";
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
            changes.Add(new SyncChange(reader.GetString(0), reader.GetString(1), data, reader.GetInt64(3) != 0, reader.GetInt64(4), reader.GetInt64(5) != 0));
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
                WHERE collection = $collection AND id = $id AND data = $data AND deleted = $deleted AND changed_at = $changedAt
                """;
            AddChange(command, change);
            command.ExecuteNonQuery();
            ForgiveRefused(connection, change);
        }
        transaction.Commit();
    }

    // The server has taken it, or something newer has replaced it, so it isn't only on this PC now.
    private static void ForgiveRefused(SqliteConnection connection, SyncChange change)
    {
        using var forgive = connection.CreateCommand();
        forgive.CommandText = "DELETE FROM refused WHERE collection = $collection AND id = $id";
        AddChange(forgive, change);
        forgive.ExecuteNonQuery();
    }

    /// <summary>The server won't take this, so it stops holding up everything behind it, and stays on this PC.</summary>
    public void SetAsideRefused(SyncChange change)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var note = connection.CreateCommand())
        {
            note.CommandText = "INSERT OR REPLACE INTO refused (collection, id) VALUES ($collection, $id)";
            note.Parameters.AddWithValue("$collection", change.Collection);
            note.Parameters.AddWithValue("$id", change.Id);
            note.ExecuteNonQuery();
        }
        using (var remove = connection.CreateCommand())
        {
            remove.CommandText = "DELETE FROM outbox WHERE collection = $collection AND id = $id AND changed_at = $changedAt";
            AddChange(remove, change);
            remove.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// A project's own record is only written when it's created or deleted, so no later edit would
    /// ever send that one again, and what stopped it may be sorted out by now, such as a clock
    /// that was wrong. One that's refused again just goes back to being set aside.
    /// </summary>
    public void RetryRefused()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var retry = connection.CreateCommand();
        retry.CommandText = """
            UPDATE records SET changed_at = $now
            WHERE changed_at > $latestRealTime AND EXISTS (SELECT 1 FROM refused WHERE refused.collection = records.collection AND refused.id = records.id);
            INSERT OR IGNORE INTO outbox (collection, id, data, deleted, changed_at, resend)
            SELECT records.collection, records.id, records.data, records.deleted, records.changed_at, 0
            FROM records JOIN refused ON refused.collection = records.collection AND refused.id = records.id;
            """;
        retry.Parameters.AddWithValue("$now", now);
        retry.Parameters.AddWithValue("$latestRealTime", now + DayMilliseconds);
        retry.ExecuteNonQuery();
        transaction.Commit();
    }

    public int CountRefused()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM refused";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Saves changes from the server. Local edits still waiting to be sent win for now.</summary>
    public int ApplyFromServer(IEnumerable<SyncChange> changes)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var applied = 0;
        foreach (var change in changes)
        {
            if (KeepsHigherEngineVersion(connection, change)) continue;

            // A queued edit that's older would only be refused when it got there, and this copy
            // of it would then never be corrected. Something only being sent again gives way to
            // one that's just as new too.
            using (var superseded = connection.CreateCommand())
            {
                superseded.CommandText = """
                    DELETE FROM outbox
                    WHERE collection = $collection AND id = $id
                      AND (changed_at < $changedAt OR (resend = 1 AND changed_at = $changedAt))
                    """;
                AddChange(superseded, change);
                superseded.ExecuteNonQuery();
            }

            // The same value can arrive with a newer time, and the next edit here has to beat that.
            using (var sameValue = connection.CreateCommand())
            {
                sameValue.CommandText = """
                    UPDATE records SET changed_at = $changedAt
                    WHERE collection = $collection AND id = $id AND data = $data AND deleted = $deleted AND changed_at < $changedAt
                    """;
                AddChange(sameValue, change);
                sameValue.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO records (collection, id, data, deleted, changed_at)
                SELECT $collection, $id, $data, $deleted, $changedAt
                WHERE NOT EXISTS (SELECT 1 FROM outbox WHERE collection = $collection AND id = $id)
                  AND NOT EXISTS (SELECT 1 FROM records WHERE collection = $collection AND id = $id AND data = $data AND deleted = $deleted)
                  AND NOT EXISTS (
                      SELECT 1 FROM refused
                      JOIN records ON records.collection = refused.collection AND records.id = refused.id
                      WHERE refused.collection = $collection AND refused.id = $id
                        AND records.changed_at > $changedAt AND records.changed_at <= $latestRealTime)
                ON CONFLICT (collection, id) DO UPDATE SET data = excluded.data, deleted = excluded.deleted, changed_at = excluded.changed_at
                """;
            AddChange(command, change);
            command.Parameters.AddWithValue("$latestRealTime", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + DayMilliseconds);
            var changed = command.ExecuteNonQuery();
            if (changed > 0) ForgiveRefused(connection, change);
            applied += changed;
        }
        transaction.Commit();
        return applied;
    }

    // The synced version only goes up, but two PCs offline can each find a different one, and the
    // server goes by time. So one that's lower for the same engine doesn't land here, and this PC's
    // goes back out, which every PC then settles on.
    private static bool KeepsHigherEngineVersion(SqliteConnection connection, SyncChange change)
    {
        if (change.Collection != EnginesCollection || change.Deleted) return false;

        string? held;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT data FROM records WHERE collection = $collection AND id = $id AND deleted = 0";
            read.Parameters.AddWithValue("$collection", change.Collection);
            read.Parameters.AddWithValue("$id", change.Id);
            held = read.ExecuteScalar() as string;
        }
        if (string.IsNullOrEmpty(held) || ParseEngineRecord(held) is not { } ours || ParseEngineRecord(change.Data) is not { } theirs) return false;
        if (ours.Engine != theirs.Engine || !IsNewerVersion(ours.Version, theirs.Version)) return false;

        var changedAt = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), change.ChangedAt + 1);
        using var resend = connection.CreateCommand();
        resend.CommandText = """
            UPDATE records SET changed_at = $changedAt WHERE collection = $collection AND id = $id;
            INSERT OR REPLACE INTO outbox (collection, id, data, deleted, changed_at, resend) VALUES ($collection, $id, $data, 0, $changedAt, 0);
            """;
        resend.Parameters.AddWithValue("$collection", change.Collection);
        resend.Parameters.AddWithValue("$id", change.Id);
        resend.Parameters.AddWithValue("$data", held);
        resend.Parameters.AddWithValue("$changedAt", changedAt);
        resend.ExecuteNonQuery();
        return true;
    }

    /// <summary>Queues everything, for the first sync with a server that may not have it.</summary>
    public void QueueAllRecords()
    {
        using var connection = Open();
        Execute(connection, ResendEverything);
    }

    // Real edits already waiting stay as they are, and go through as edits.
    private const string ResendEverything = """
        INSERT OR IGNORE INTO outbox (collection, id, data, deleted, changed_at, resend)
        SELECT collection, id, data, deleted, changed_at, 1 FROM records
        """;

    /// <summary>
    /// A server that lost its data gets everything sent again. All in one step, because
    /// if only the new epoch were saved, this would never be noticed again after a crash.
    /// </summary>
    public void StartOverWithServer(string epochSetting, string oldEpoch, string epoch, string cursorSetting)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var others = connection.CreateCommand())
        {
            others.CommandText = "UPDATE settings SET value = $latest WHERE key LIKE 'sync_epoch:%' AND value = $old";
            others.Parameters.AddWithValue("$latest", epoch);
            others.Parameters.AddWithValue("$old", oldEpoch);
            others.ExecuteNonQuery();
        }
        Execute(connection, ResendEverything);
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

    /// <summary>Reads and writes a setting as one step, so another window's change to it can't be lost.</summary>
    public void ChangeSetting(string key, Func<string?, string> change)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string? current;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT value FROM settings WHERE key = $key";
            read.Parameters.AddWithValue("$key", key);
            current = read.ExecuteScalar() as string;
        }
        WriteSetting(connection, key, change(current));
        transaction.Commit();
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
        var isNowAnEdit = table == "outbox" ? ", resend = 0" : "";
        command.CommandText = $"""
            INSERT INTO {table} (collection, id, data, deleted, changed_at) VALUES ($collection, $id, $data, $deleted, $changedAt)
            ON CONFLICT (collection, id) DO UPDATE SET data = excluded.data, deleted = excluded.deleted, changed_at = excluded.changed_at{isNowAnEdit}
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
        command.Parameters.AddWithValue("$changedAt", change.ChangedAt);
    }

    private static void AddColumn(SqliteConnection connection, string table, string column)
    {
        try
        {
            Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column}");
        }
        catch (SqliteException error) when (error.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Already there, from an earlier start.
        }
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
