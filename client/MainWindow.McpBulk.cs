using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// AO's tools that do many of one thing at once, as one call: add_cards, add_tasks, update_tasks and
/// remove_cards. One asset pass once took 637 add_card calls, about 8 seconds each. Each item is what
/// the tool for one takes, checked the same way, and done as that tool does it; the page is drawn and
/// the changes sent to other PCs once, at the end. Also the evidence a task can be given, and a
/// milestone's notes.
/// </summary>
public partial class MainWindow
{
    // While above 0, changes are shown and sent once at the end of the call making them.
    private int _mcpChangesHeld;

    private const int MostInOneCall = 200;

    // The most a task keeps, so its listing stays readable.
    private const int MostEvidence = 20;

    // Each tool that does many at once: its name, the one it does many of, what the list is called, and what more to say about it.
    private static readonly (string Name, string Of, string List, string More)[] BulkTools =
    [
        ("add_cards", "add_card", "cards", "Such as every asset a slice needs: [{\"page\":\"Assets\",\"name\":\"Pine tree\",\"type\":\"3D model\"}, ...]."),
        ("remove_cards", "remove_card", "cards", "Only when the person asked for it or the cards are being replaced, as with remove_card."),
        ("add_tasks", "add_task", "tasks", "Such as a plan's tasks, in their order: [{\"title\":\"Block out the level\",\"milestone\":\"Slice 1\"}, ...]."),
        ("update_tasks", "update_task", "tasks", "Including their order: give each its position in turn, 1, 2, 3 and so on, to lay a milestone's tasks out in that order."),
    ];

    private List<McpTool> WithBulkTools(List<McpTool> tools)
    {
        // Each listed straight after the tool it does many of.
        foreach (var (name, of, list, more) in BulkTools)
        {
            var one = tools.First(tool => tool.Name == of);
            var items = new JsonObject { ["type"] = "object", ["properties"] = one.Properties.DeepClone(), ["required"] = new JsonArray([.. one.Required.Select(each => (JsonNode)each)]) };
            var properties = new JsonObject { [list] = new JsonObject { ["type"] = "array", ["items"] = items, ["description"] = $"Up to {MostInOneCall}, each with {of}'s arguments." } };
            var description = $"Does what {of} does for many {list} at once, in one call. {more} Each is checked first, and a wrong or missing argument in any changes nothing at all. Then each is done in turn, as {of} would do it, and one refused then, such as a name already taken, is said with why while the rest are done.";
            tools.Insert(tools.IndexOf(one) + 1, new McpTool(name, description, properties, [list], (project, given) => RunBulk(project, one, list, given)));
        }
        return tools;
    }

    private JsonObject RunBulk(Project project, McpTool one, string list, JsonObject given)
    {
        if (given[list] is not JsonArray items)
            throw new McpException(-32602, $"{list} is a list, of up to {MostInOneCall}, each with {one.Name}'s arguments; it was {given[list]?.ToJsonString() ?? "nothing"}. {ArgumentsOf(one)}");
        if (items.Count == 0) throw new McpException(-32602, $"{list}: the list is empty. Give each one's {one.Name} arguments in it.");
        if (items.Count > MostInOneCall) throw new McpException(-32602, $"{list}: up to {MostInOneCall} in one call, and it had {items.Count}. Send them in parts.");

        // Every one's arguments first, so a wrong name or a missing one changes nothing.
        var checkedItems = new List<JsonObject>();
        for (var at = 0; at < items.Count; at++)
        {
            if (items[at] is not JsonObject item)
                throw new McpException(-32602, $"{list} number {at + 1} isn't a set of {one.Name}'s arguments, such as {{\"{one.Required.FirstOrDefault() ?? "name"}\": \"...\"}}; it was {items[at]?.ToJsonString() ?? "null"}. Nothing was changed.");
            try
            {
                checkedItems.Add(CheckedArguments(one, item));
            }
            catch (McpException refused)
            {
                throw new McpException(-32602, $"{list} number {at + 1}: {refused.Message} Nothing was changed.");
            }
        }

        var said = new StringBuilder();
        var done = 0;
        _mcpChangesHeld++;
        try
        {
            for (var at = 0; at < checkedItems.Count; at++)
            {
                try
                {
                    said.AppendLine($"{at + 1}. {Shortened(one.Run(project, checkedItems[at]).ToString() ?? "")}");
                    done++;
                }
                catch (McpMissingArgument missing) when (one.Properties.ContainsKey(missing.Name))
                {
                    said.AppendLine($"{at + 1}. Refused: {Needs(one, missing.Name)}");
                }
                catch (McpException refused)
                {
                    said.AppendLine($"{at + 1}. Refused: {refused.Message}");
                }
            }
        }
        finally
        {
            _mcpChangesHeld--;
        }
        if (done > 0) McpChanged(project);

        var refusedCount = checkedItems.Count - done;
        var heading = refusedCount == 0
            ? $"All {done} done."
            : $"{done} of {checkedItems.Count} done. {refusedCount} {(refusedCount == 1 ? "was" : "were")} refused, which changed nothing for {(refusedCount == 1 ? "it" : "them")}: give {(refusedCount == 1 ? "it" : "those")} again once right.";
        return ToolResult($"{heading}\n{said}", error: done == 0);
    }

    // One line each, as a long answer for each of hundreds is too much to read.
    private static string Shortened(string answer)
    {
        var line = answer.Replace("\r", "").Replace("\n", " ").Trim();
        return line.Length > 300 ? line[..299].TrimEnd() + "…" : line;
    }

    private static JsonObject EvidenceArgument() => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = "Files that show the work, such as a screenshot or a log, by full path or a path in the project's folder. A picture is kept in Agent Orange and shown on the task, on every PC; another file is kept by its path, so put it in the project's folder to have it travel with the repo. Added to what the task has; an empty list takes all it has off.",
    };

    /// <summary>The files given as evidence, each found, or null when none was given. Empty to take them all off.</summary>
    private List<string>? EvidenceFiles(Project project, JsonNode? given)
    {
        if (given is null) return null;
        List<string> paths;
        if (given is JsonArray list)
        {
            paths = [.. list.Select(each => each is JsonValue value && value.TryGetValue<string>(out var text)
                ? text.Trim()
                : throw new McpException(-32602, $"evidence is a list of paths, each as text; one was {each?.ToJsonString() ?? "null"}."))];
        }
        else if (given is JsonValue value && value.TryGetValue<string>(out var one))
        {
            // One path as text, or a list written out as text.
            one = one.Trim();
            if (one.StartsWith('[') && TryList(one) is { } written) paths = written;
            else paths = one.Length > 0 ? [one] : [];
        }
        else throw new McpException(-32602, $"evidence is a list of paths; it was {given.ToJsonString()}.");

        paths = [.. paths.Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (paths.Count > MostEvidence) throw new McpException(-32602, $"evidence: a task keeps up to {MostEvidence}, and {paths.Count} were given. Give the ones that show the work best.");
        var folder = ProjectFolder(project.Id);
        var found = new List<string>();
        foreach (var path in paths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.IsPathRooted(path) || folder is null ? path : Path.Combine(folder, path));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new McpException(-32602, $"evidence: {path} isn't a path to a file. Give its full path, or one inside the project's folder.");
            }
            if (!File.Exists(full))
                throw new McpException(-32602, $"evidence: there's no file at {path}{(Path.IsPathRooted(path) || folder is null ? "" : $" in the project's folder, {folder}")}. Give its full path, or one inside the project's folder.");
            found.Add(full);
        }
        return found;

        static List<string>? TryList(string text)
        {
            try
            {
                return JsonNode.Parse(text) is JsonArray parsed ? [.. parsed.Select(each => each?.GetValue<string>()?.Trim() ?? "")] : null;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
    }

    private static bool IsPicture(string file) => DocumentPage.PictureEndings.Contains(Path.GetExtension(file).ToLowerInvariant());

    /// <summary>
    /// The files as a task's evidence: each picture made small enough to sync and kept with the
    /// project's pictures, every one read before any is kept, so a bad one keeps nothing.
    /// </summary>
    private TaskEvidence[] KeepEvidence(Project project, List<string> files)
    {
        var at = DateTimeOffset.Now.ToString("o");
        var folder = ProjectFolder(project.Id);
        var prepared = files.Select(file => (File: file, Picture: IsPicture(file)
            ? ProjectImages.PrepareForBoard(file) ?? throw new McpException(-32602, $"evidence: {file} couldn't be read as a picture. Give a PNG or JPEG, or another file by its path.")
            : null)).ToList();
        return [.. prepared.Select(each => new TaskEvidence(at, each.Picture is { } bytes ? _database.AddDocumentPicture(project.Id, bytes) : null, InFolderOrWhole(folder, each.File)))];
    }

    // Inside the project's folder, by where it is in it, which is the same on every PC.
    private static string InFolderOrWhole(string? folder, string file)
    {
        if (folder is null) return file;
        var inside = Path.GetRelativePath(folder, file);
        return inside.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(inside) ? file : inside.Replace('\\', '/');
    }

    // As agents are told it: a picture they can look at with read_picture, or a file by its path.
    private static string EvidenceList(IEnumerable<TaskEvidence> evidence) =>
        string.Join(", ", evidence.Select(each => each.Picture is { } picture ? $"picture:{picture}{(each.Path is { } from ? $" (from {from})" : "")}" : $"the file {each.Path}"));

    private string UpdateMilestoneFor(Project project, JsonObject given)
    {
        var id = MilestoneId(project, Required(given, "milestone"))!;
        var notes = Given(given, "notes") ?? throw new McpMissingArgument("notes");
        var stage = _database.ProjectStage(project.Id);
        var milestone = _database.ListMilestones(project.Id, stage).First(each => each.Id == id).Milestone;
        var written = Given(given, "mode") is "append" && milestone.Notes is { Length: > 0 } old && notes.Length > 0 ? $"{old.TrimEnd()}\n\n{notes}" : notes;
        _database.SaveMilestone(project.Id, id, milestone with { Notes = written.Length > 0 ? written : null });
        McpChanged(project);
        return written.Length > 0 ? $"Changed the notes of {milestone.Name}. They're {WordsIn(written)} words now." : $"Took the notes off {milestone.Name}.";
    }
}
