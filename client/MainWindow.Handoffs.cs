using System.Globalization;

namespace AgentOrange;

/// <summary>
/// Handoff notes: where a conversation left off, kept on the milestone or task it was working on, for
/// the next conversation's briefing.
/// </summary>
public partial class MainWindow
{
    private static string HandoffWhen(HandoffNote note) =>
        DateTimeOffset.TryParse(note.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : note.At;

    // One line, as list_tasks gives it.
    private static string HandoffLine(HandoffNote note) =>
        $"Handoff ({HandoffWhen(note)}{(note.From is { Length: > 0 } from ? $", from {from}" : "")}): {note.Text.Replace("\r", "").Replace("\n", " ").Trim()}";
}
