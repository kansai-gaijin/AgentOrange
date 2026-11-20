using System.Globalization;
using System.Text;

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
