using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace AgentOrange;

/// <summary>
/// The project's Lessons page (<see cref="ProjectLessons"/>): the person's rules and what agents
/// learned, edited as a document, added to with add_lesson, and given to every agent and its helpers.
/// </summary>
public partial class MainWindow
{
    private void AddLessonsPage(StackPanel body, Project project)
    {
        var markdown = _database.GetDocument(project.Id, ProjectLessons.Key) is { Length: > 0 } stored ? stored : ProjectLessons.Template;
        body.MaxWidth = double.PositiveInfinity;
        body.Margin = new Thickness(0);
        var height = Math.Max(240, ProjectView.ActualHeight - HeroHeight(project));

        // Kept per project, as its key is the same in every one.
        var kept = $"{project.Id}|{ProjectLessons.Key}";
        if (_documents.TryGetValue(kept, out var page))
        {
            (page.Parent as Panel)?.Children.Remove(page);
            page.ShowLatest(markdown, 0);
        }
        else
        {
            page = _documents[kept] = new DocumentPage(_database, project.Id, ProjectLessons.Key, markdown, null, 0, () => _ = _sync.SyncNowAsync(), link => FollowLink(project, link), () => { }, hasBoard: false);
        }
        page.Height = height;
        _openDocument = page;
        body.Children.Add(page);
    }

    private string AddLessonFor(Project project, JsonObject given)
    {
        var lesson = Required(given, "lesson");
        var tool = Given(given, "tool") is { Length: > 0 } named ? LessonToolName(project, named) : "General";
        var (markdown, added) = ProjectLessons.Add(_database.GetDocument(project.Id, ProjectLessons.Key), tool, lesson, Given(given, "evidence"), DateOnly.FromDateTime(DateTime.Today));
        if (!added) return $"The lessons already have that, under {tool}.";
        _database.SetDocument(project.Id, ProjectLessons.Key, markdown);
        McpChanged(project);
        return $"Added to the project's lessons, under {tool}. Every later conversation's briefing has it, and the person can change it on the Lessons page.";
    }

    // A tool set up here or the game's engine by the name it has in Agent Orange, so one tool's
    // lessons stay together however it's written; any other, such as Git, as it's given.
    private static string LessonToolName(Project project, string named)
    {
        var known = EditorMonitor.Definitions.SelectMany(editor => new[] { (Said: editor.Name, Name: editor.Name), (Said: editor.Id, Name: editor.Name) })
            .Append((Said: project.Engine, Name: project.Engine)).Where(each => each.Said.Length > 0);
        return known.FirstOrDefault(each => each.Said.Equals(named, StringComparison.OrdinalIgnoreCase)).Name ?? named;
    }

    /// <summary>What a session's briefing says about this project beyond what every one says: its rules and lessons, and where the last conversations left off.</summary>
    internal string? ProjectBriefingNotes(string projectId)
    {
        var parts = new[] { ProjectLessons.BriefingSection(_database.GetDocument(projectId, ProjectLessons.Key)), HandoffsForBriefing(projectId) }.OfType<string>().ToList();
        return parts.Count > 0 ? string.Join("\n\n", parts) : null;
    }

    /// <summary>The rules and lessons, for the hook that gives them to each helper agent as it starts; none when there are none.</summary>
    private string WriteHelperBriefing(string agentFolder, string projectId)
    {
        var path = Path.Combine(agentFolder, "briefings", $"{projectId}-helpers.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (ProjectLessons.ForHelpers(_database.GetDocument(projectId, ProjectLessons.Key)) is { } text) File.WriteAllText(path, text);
        else File.Delete(path);
        return path;
    }
}
