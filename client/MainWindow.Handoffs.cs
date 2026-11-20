using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// Handoff notes: where a conversation left off, kept on the milestone or task it was working on, for
/// the next conversation's briefing. Each new conversation used to spend 10 to 20 calls re-reading
/// notes, tasks and reviews before working, and a usage limit stopping one mid-task meant doing that
/// again.
/// </summary>
public partial class MainWindow
{
    // The newest this many go in a briefing; list_tasks gives the rest.
    private const int HandoffsInBriefing = 3;
    private const int LongestHandoff = 3000;

    private static string HandoffWhen(HandoffNote note) =>
        WhenOf(note) is { } at ? at.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : note.At;

    private static DateTimeOffset? WhenOf(HandoffNote note) =>
        DateTimeOffset.TryParse(note.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    // One line, as list_tasks gives it.
    private static string HandoffLine(HandoffNote note) =>
        $"Handoff ({HandoffWhen(note)}{(note.From is { Length: > 0 } from ? $", from {from}" : "")}): {note.Text.Replace("\r", "").Replace("\n", " ").Trim()}";

    private string WriteHandoffFor(Project project, JsonObject given)
    {
        var text = Required(given, "note");
        var stage = _database.ProjectStage(project.Id);
        // Who it's from: the conversation working on the project, when only one is.
        var working = _conversations?.Where(each => each.ProjectId == project.Id && each.IsWorking).ToList() ?? [];
        var note = new HandoffNote(text.Length > LongestHandoff ? text[..LongestHandoff] : text, DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture), working.Count == 1 && working[0].Title.Length > 0 ? working[0].Title : null);

        if (Given(given, "task") is { Length: > 0 } taskId)
        {
            if (_database.ListTasks(project.Id, stage).FirstOrDefault(each => SameId(each.Id, taskId)) is not { Task: not null } found)
                throw new McpException(-32602, $"task: there's no task {taskId}. list_tasks gives each task's id, after the word task, such as (task 3f2a9c). Or give milestone instead.");
            _database.SaveTask(project.Id, found.Id, found.Task with { Handoff = note });
            McpChanged(project);
            return $"Left the handoff note on the task {found.Task.Title}, replacing any it had. The next conversation's briefing starts from it.";
        }
        if (Given(given, "milestone") is not { Length: > 0 } asked)
        {
            var milestones = _database.ListMilestones(project.Id, stage);
            throw new McpException(-32602, milestones.Count == 0
                ? "write_handoff needs milestone or task: where the note goes. The stage has no milestones, so give the task you were on, by its id from list_tasks."
                : $"write_handoff needs milestone or task: where the note goes. The milestones: {string.Join("; ", milestones.Take(ListedNames).Select(each => each.Milestone.Name))}. Or give the task you were on, by its id from list_tasks.");
        }
        var id = MilestoneId(project, asked)!;
        var milestone = _database.ListMilestones(project.Id, stage).First(each => each.Id == id).Milestone;
        _database.SaveMilestone(project.Id, id, milestone with { Handoff = note });
        McpChanged(project);
        return $"Left the handoff note on the milestone {milestone.Name}, replacing any it had. The next conversation's briefing starts from it.";
    }

    /// <summary>
    /// Whether to ask its agent for a handoff note as it's settled: it was given work by the person,
    /// hasn't written a note since, and the project has a milestone or task to keep one on.
    /// </summary>
    private bool WantsHandoff(AgentConversation conversation)
    {
        if (conversation.IsLimited) return false;
        var asked = conversation.Items.FindLastIndex(item => item.Kind == AgentItemKind.User && !item.Automatic);
        if (asked < 0) return false;
        var written = conversation.Items.FindLastIndex(item => item.Kind == AgentItemKind.Tool && item.Tool?.EndsWith("write_handoff", StringComparison.Ordinal) == true);
        if (written > asked) return false;
        var stage = _database.ProjectStage(conversation.ProjectId);
        return _database.ListMilestones(conversation.ProjectId, stage).Count > 0 || _database.ListTasks(conversation.ProjectId, stage).Count > 0;
    }

    /// <summary>
    /// Settles a conversation, first asking its agent for a handoff note when it has work to hand
    /// over: it's settled once it answers. False when it can't be settled now, as it works or waits
    /// on the person.
    /// </summary>
    internal async Task<bool> SettleConversationAsync(AgentConversation conversation)
    {
        if (conversation.IsWorking || conversation.IsWaiting) return false;
        if (conversation.Settled || !WantsHandoff(conversation) || await AgentProblemAsync(conversation) is not null) return conversation.Settle(true);
        conversation.SettlesAfterHandoff = true;
        conversation.Send(AgentConversation.HandoffRequest, [], ViewLabel(), DescribeView(), () => PrepareAgent(conversation), automatic: true);
        return true;
    }

    /// <summary>
    /// A usage limit stopped the conversation mid-task, so it can't write its own note: Agent Orange
    /// writes one from what it has (its plan, the last it said, and what it was asked), on the task it
    /// was last working on through Agent Orange's tools, or else that task's milestone or the first
    /// milestone with work left.
    /// </summary>
    private void WriteLimitHandoff(AgentConversation conversation)
    {
        if (FindProject(conversation.ProjectId) is not { } project) return;
        var stage = _database.ProjectStage(project.Id);
        var tasks = _database.ListTasks(project.Id, stage);
        var milestones = _database.ListMilestones(project.Id, stage);

        // Agent Orange's tools say each task they touch as (task id).
        (string Id, ProjectTask Task)? last = null;
        foreach (var item in conversation.Items.AsEnumerable().Reverse().Where(item => item.Kind == AgentItemKind.Tool && item.Detail is not null))
        {
            last = Regex.Matches(item.Detail!, @"\(task (\w+)\)").Reverse()
                .Select(match => tasks.FirstOrDefault(each => SameId(each.Id, match.Groups[1].Value)))
                .FirstOrDefault(found => found.Task is not null);
            if (last?.Task is not null) break;
            last = null;
        }

        var note = new HandoffNote(LimitHandoffText(conversation), DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture), "Agent Orange, as a usage limit stopped the conversation");
        if (last is { } task && !task.Task.Done)
        {
            _database.SaveTask(project.Id, task.Id, task.Task with { Handoff = note });
        }
        else
        {
            var milestoneId = last?.Task.Milestone is { } inMilestone && milestones.Any(each => each.Id == inMilestone) ? inMilestone : null;
            milestoneId ??= milestones.Select(each => each.Id).FirstOrDefault(id => tasks.Where(each => each.Task.Milestone == id).ToList() is var inIt && (inIt.Count == 0 || inIt.Any(each => !each.Task.Done)));
            if (milestoneId is null) return; // Nowhere to keep it.
            _database.SaveMilestone(project.Id, milestoneId, milestones.First(each => each.Id == milestoneId).Milestone with { Handoff = note });
        }
        _ = _sync.SyncNowAsync();
        if (_openProject?.Id == project.Id) RedrawProject(_openProject);
    }

    private string LimitHandoffText(AgentConversation conversation)
    {
        static string Cut(string text, int most) => Regex.Replace(text, @"\s+", " ").Trim() is var line && line.Length > most ? line[..most].TrimEnd() + "…" : line;
        var said = new StringBuilder();
        var resets = ResumesAt(conversation) is { } at ? $", until about {at.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}" : "";
        said.AppendLine($"A usage limit stopped the conversation \"{conversation.Title}\" mid-task{resets}, before it could write its own note, so Agent Orange wrote this from what it had.");
        if (conversation.Steps is { Count: > 0 } steps)
        {
            string Of(string status) => string.Join("; ", steps.Where(step => step.Status == status).Select(step => step.Content));
            var plan = new[] { ("done", Of("completed")), ("in progress", Of("in_progress")), ("still to do", Of("pending")) }.Where(each => each.Item2.Length > 0).Select(each => $"{each.Item1}: {each.Item2}");
            said.AppendLine($"Its plan when it stopped: {string.Join(". ", plan)}.");
        }
        if (conversation.Items.LastOrDefault(item => item.Kind == AgentItemKind.Assistant && item.Text.Trim().Length > 0) is { } reply) said.AppendLine($"The last it said: \"{Cut(reply.Text, 600)}\"");
        if (conversation.Items.LastOrDefault(item => item.Kind == AgentItemKind.User && !item.Automatic) is { } asked) said.AppendLine($"What it was last asked: \"{Cut(asked.Text, 400)}\"");
        said.Append("Check the work is as these say before going on.");
        return said.ToString();
    }

    /// <summary>
    /// The briefing's section on where the last conversations left off: the newest handoff notes on
    /// milestones and tasks still open in the stage, or null when there are none.
    /// </summary>
    private string? HandoffsForBriefing(string projectId)
    {
        var stage = _database.ProjectStage(projectId);
        var tasks = _database.ListTasks(projectId, stage);
        var notes = new List<(string Where, HandoffNote Note)>();
        foreach (var (id, milestone) in _database.ListMilestones(projectId, stage))
        {
            var inIt = tasks.Where(each => each.Task.Milestone == id).ToList();
            if (milestone.Handoff is { } note && (inIt.Count == 0 || inIt.Any(each => !each.Task.Done))) notes.Add(($"the milestone {milestone.Name}", note));
        }
        foreach (var (id, task) in tasks.Where(each => each.Task.Handoff is not null && !each.Task.Done)) notes.Add(($"the task {task.Title} (task {Short(id)})", task.Handoff!));
        if (notes.Count == 0) return null;

        var said = new StringBuilder();
        said.AppendLine("## Where the last conversations left off");
        said.AppendLine();
        said.AppendLine("Handoff notes earlier conversations left on the work still open, newest first. Start from these rather than reading everything again: check what they say still holds, then go on from their next step." + (notes.Count > HandoffsInBriefing ? $" There are {notes.Count}; list_tasks with fields handoff gives every one." : ""));
        foreach (var (where, note) in notes.OrderByDescending(each => WhenOf(each.Note) ?? DateTimeOffset.MinValue).Take(HandoffsInBriefing))
        {
            said.AppendLine();
            said.AppendLine($"### On {where}, {HandoffWhen(note)}{(note.From is { Length: > 0 } from ? $", from {from}" : "")}");
            said.AppendLine();
            said.AppendLine(note.Text.Length > LongestHandoff ? note.Text[..LongestHandoff].TrimEnd() + "…" : note.Text.Trim());
        }
        return said.ToString().TrimEnd();
    }
}
