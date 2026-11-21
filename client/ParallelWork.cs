using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// One part of a milestone split across agents (#94): its tasks, worked through in order by one
/// conversation, what they use, and where it works. A part whose tasks use the game's engine works
/// in the project's own folder, as the engine opens the project there; every other part works in a
/// Git worktree of its own, on a branch of its own, so parts never step on each other's files.
/// </summary>
public sealed class ParallelPart
{
    public const string Waiting = "waiting"; // Not started yet.
    public const string Started = "started"; // Has its conversation, which may have finished its turn.
    public const string Merged = "merged";
    public const string Stopped = "stopped"; // Stopped before it started, or taken off the split.

    public int Number { get; set; }
    public List<string> Tasks { get; set; } = [];
    public List<string> Uses { get; set; } = [];

    /// <summary>The tools of its uses only one agent may use at once.</summary>
    public List<string> OneAtATime { get; set; } = [];

    /// <summary>Its branch and worktree, or null for the project's own folder, on the branch checked out there.</summary>
    public string? Branch { get; set; }
    public string? Worktree { get; set; }

    public string State { get; set; } = Waiting;
    public string? Conversation { get; set; }

    /// <summary>Why it hasn't started yet, or what stopped its merge.</summary>
    public string? Note { get; set; }

    public bool InProjectFolder => Worktree is null;
}

/// <summary>
/// A milestone split across agents: its parts, the branch and commit they started from, and who
/// started it (a person on the Tasks page, or a coordinating agent's conversation, which then waits
/// on the parts as on its background work). Kept on this PC, as its worktrees and conversations are.
/// </summary>
public sealed class ParallelRun
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Stage { get; init; }
    public required string Milestone { get; init; }
    public string MilestoneName { get; set; } = "";

    /// <summary>The branch checked out in the project's folder when it was split, which the parts merge back into, and its commit then.</summary>
    public string BaseBranch { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The conversation that split it, when an agent did, which is told once the parts are done.</summary>
    public string? Parent { get; set; }
    public bool ParentTold { get; set; }

    /// <summary>The login, model, reasoning and permission mode each part's conversation starts with.</summary>
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string PermissionMode { get; set; } = "auto";

    public List<ParallelPart> Parts { get; set; } = [];

    /// <summary>What the last merge said, such as a conflict.</summary>
    public string? MergeNote { get; set; }

    /// <summary>Every part merged or stopped: nothing of it is left.</summary>
    public bool Over => Parts.All(part => part.State is ParallelPart.Merged or ParallelPart.Stopped);
}

/// <summary>
/// How a milestone's open tasks are split (#94). Tasks that use something in common (a tool, or a
/// name of the project's own such as "save files") go in the same part, one after another, so they
/// never run at once; tasks that use the game's engine all go in the one part that works in the
/// project's own folder. Independent groups are packed into as many parts as may run at once,
/// evenly, keeping each part's tasks in the milestone's order.
/// </summary>
public static class ParallelPlan
{
    /// <summary>A task to split: its id, title and what it uses.</summary>
    public sealed record Item(string Id, string Title, string[] Uses);

    /// <summary>A part as planned: its tasks in order, what they use, its tools used one at a time, and whether it works in the project's own folder.</summary>
    public sealed record Part(List<Item> Tasks, List<string> Uses, List<string> OneAtATime, bool InProjectFolder);

    /// <summary>
    /// Splits the tasks into at most <paramref name="most"/> parts. <paramref name="tool"/> says whether
    /// a use names a tool: its name as Agent Orange calls it, whether only one agent may use it at a
    /// time, and whether it's the game's engine; null for a name of the project's own.
    /// </summary>
    public static List<Part> Split(IReadOnlyList<Item> tasks, Func<string, (string Name, bool OneAtATime, bool GameEngine)?> tool, int most)
    {
        most = Math.Max(1, most);
        var keys = tasks.Select(task => task.Uses.Select(use => Key(use, tool)).Where(key => key.Name.Length > 0).DistinctBy(key => key.Id).ToList()).ToList();

        // Tasks sharing a use are one group, joined through each use they have.
        var group = Enumerable.Range(0, tasks.Count).ToArray();
        int Find(int at) => group[at] == at ? at : group[at] = Find(group[at]);
        var firstWith = new Dictionary<string, int>();
        for (var at = 0; at < tasks.Count; at++)
        {
            foreach (var key in keys[at])
            {
                if (firstWith.TryGetValue(key.Id, out var other)) group[Find(at)] = Find(other);
                else firstWith[key.Id] = at;
            }
        }
        var groups = Enumerable.Range(0, tasks.Count).GroupBy(Find).Select(each => each.OrderBy(at => at).ToList()).OrderBy(each => each[0]).ToList();

        // The game's engine opens the project's own folder, so that group stays there, and the rest are packed beside it.
        var inFolder = groups.FirstOrDefault(each => each.Any(at => keys[at].Any(key => key.GameEngine)));
        var bins = new List<List<int>>();
        if (inFolder is not null) bins.Add([.. inFolder]);
        var others = groups.Where(each => each != inFolder).ToList();
        var room = Math.Max(0, most - bins.Count);
        if (room == 0 && bins.Count > 0)
        {
            foreach (var each in others) bins[0].AddRange(each);
        }
        else
        {
            var made = new List<List<int>>();
            foreach (var each in others)
            {
                if (made.Count < room)
                {
                    made.Add([.. each]);
                    continue;
                }
                made.OrderBy(bin => bin.Count).ThenBy(bin => made.IndexOf(bin)).First().AddRange(each);
            }
            bins.AddRange(made);
        }

        return [.. bins.Select(bin => bin.OrderBy(at => at).ToList()).OrderBy(bin => bin[0]).Select(bin =>
        {
            var used = bin.SelectMany(at => keys[at]).DistinctBy(key => key.Id).ToList();
            return new Part([.. bin.Select(at => tasks[at])], [.. used.Select(key => key.Name)], [.. used.Where(key => key.OneAtATime).Select(key => key.Name)], inFolder is not null && bin.Contains(inFolder[0]));
        })];
    }

    // A use as the split compares it: a tool by the name Agent Orange calls it, anything else by its words.
    private static (string Id, string Name, bool OneAtATime, bool GameEngine) Key(string use, Func<string, (string Name, bool OneAtATime, bool GameEngine)?> tool)
    {
        var words = Regex.Replace(use.Trim(), @"\s+", " ");
        if (tool(words) is { } known) return ($"tool:{known.Name.ToLowerInvariant()}", known.Name, known.OneAtATime, known.GameEngine);
        return (words.ToLowerInvariant().Replace('\\', '/').TrimEnd('/'), words, false, false);
    }

    /// <summary>A branch name's part for a milestone, such as "milestone-2-movement".</summary>
    public static string Slug(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
        return slug.Length > 0 ? slug : "milestone";
    }
}

/// <summary>The milestones split across agents on this PC, kept in the agent files folder.</summary>
public static class ParallelRuns
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string FileIn(string agentFolder) => Path.Combine(agentFolder, "parallel-runs.json");

    public static List<ParallelRun> Load(string agentFolder)
    {
        try
        {
            var file = FileIn(agentFolder);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<ParallelRun>>(File.ReadAllText(file), JsonOptions) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write($"Couldn't read the milestones split across agents: {error.Message}");
            return [];
        }
    }

    public static void Save(string agentFolder, List<ParallelRun> runs)
    {
        try
        {
            Directory.CreateDirectory(agentFolder);
            var file = FileIn(agentFolder);
            File.WriteAllText(file + ".new", JsonSerializer.Serialize(runs, JsonOptions));
            File.Move(file + ".new", file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't save the milestones split across agents: {error.Message}");
        }
    }
}
