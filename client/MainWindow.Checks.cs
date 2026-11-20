#if DEBUG
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentOrange;

/// <summary>
/// Dev builds only, for headless checks: a big made-up project to try the pages on, and a way to
/// open a project's page without asking GitHub or Git anything, as checks mustn't reach anything real.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Makes a project with this many tasks (spread over the milestones, a third of them done),
    /// assets (with types, tags, owners, statuses in turn, a deliverable on every fourth, a day to
    /// deliver some by, and every eighth and the one before it with no status, as older assets have),
    /// and a few characters and levels for them to belong to.
    /// </summary>
    internal string SeedForChecks(string name, int tasks, int milestones, int assets, string engine = "Godot")
    {
        var projectId = _database.CreateProject(name, engine, "2D", $"https://github.com/example/{GitTools.RepoName(name)}");
        var folder = Path.Combine(Headless.DataFolder, "projects", GitTools.RepoName(name));
        Directory.CreateDirectory(folder);
        _database.SetProjectFolder(projectId, folder);
        var stage = _database.ProjectStage(projectId);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var owners = new List<string>();
        for (var i = 1; i <= 8; i++) owners.Add(_database.AddCard(projectId, stage, "characters", $"Character {i}").Id);
        for (var i = 1; i <= 4; i++) owners.Add(_database.AddCard(projectId, stage, "levels", $"Level {i}").Id);

        var tags = new[] { "Forest", "Town", "Night", "UI", "Sound", "Hero" }.Select(tag => _database.AddAssetTag(projectId, tag).Id).ToArray();
        var milestoneIds = Enumerable.Range(1, milestones).Select(i => _database.SaveMilestone(projectId, null, new Milestone(stage, $"Milestone {i}", $"What milestone {i} is for.", null, i))).ToList();
        for (var i = 1; i <= tasks; i++)
        {
            var milestone = milestoneIds.Count > 0 && i % 10 != 0 ? milestoneIds[(i - 1) % milestoneIds.Count] : null;
            var due = i % 3 == 0 ? today.AddDays(i % 40 - 10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            _database.SaveTask(projectId, null, new ProjectTask(stage, $"Task {i}", i % 4 == 0 ? $"Notes for task {i}" : null, due, i % 3 == 1, milestone, i, i % 7 == 0 ? owners[i % owners.Count] : null));
        }

        var types = AssetTypes.SelectMany(group => group.Types).Select(type => type.Id).ToArray();
        var picture = SeedPicture();
        for (var i = 1; i <= assets; i++)
        {
            var card = _database.AddCard(projectId, stage, AssetsPart, $"Asset {i}");
            var due = i % 5 == 0 ? today.AddDays(i % 30 - 5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            // Some saved as assets were before they had a status: delivered ones, and ones not started.
            var status = i % 8 is 0 or 7 ? null : (i % 4) switch { 0 => AssetStatus.Delivered, 1 => AssetStatus.ToDo, 2 => AssetStatus.InProgress, _ => AssetStatus.NeedsReview };
            _database.SetAssetDetails(card.Id, new AssetDetails(types[i % types.Length], owners[i % owners.Count], [tags[i % tags.Length]], due, status));
            if (i % 4 == 0) _database.SetAssetDeliverable(card.Id, new AssetDeliverable($"assets/asset-{i}.png", 1000 + i, new string('0', 64), DateTimeOffset.UtcNow));
            if (i % 10 == 0 && picture is not null) _database.SetCardImage(card.Id, picture);
        }
        RefreshProjects();
        return projectId;
    }

    // A small picture, so some cards have one to show.
    private static byte[]? SeedPicture()
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new LinearGradientBrush(Colors.SeaGreen, Colors.SteelBlue, 45), null, new Rect(0, 0, 200, 200));
        }
        var bitmap = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Calls one of the MCP tools agents get, in this app, as an agent's session would through the
    /// relay, which a check can't use as it would reach whichever copy of the app is running.
    /// </summary>
    internal async Task<(string Text, bool Refused)> CallMcpForChecks(string projectName, string tool, IReadOnlyList<string> pairs)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var given = new System.Text.Json.Nodes.JsonObject();
        for (var i = 0; i + 1 < pairs.Count; i += 2) given[pairs[i]] = pairs[i + 1];
        var result = await McpCallAsync(project.Id, tool, given);
        var text = string.Join("\n", (result["content"] as System.Text.Json.Nodes.JsonArray ?? []).Select(part => part?["text"]?.GetValue<string>()).OfType<string>());
        return (text, result["isError"]?.GetValue<bool>() == true);
    }

    /// <summary>
    /// Sets an engine or tool up as Settings › Editors would, with its program and the API reference
    /// the person chose (blank for where it usually keeps one), each in the check's data folder.
    /// </summary>
    internal void SetUpToolForChecks(string id, string program, string reference)
    {
        var editor = EditorMonitor.Definitions.FirstOrDefault(each => each.Id == id) ?? throw new InvalidOperationException($"There's no tool called {id}.");
        string InData(string path) => path.Length > 0 ? Path.Combine(Headless.DataFolder, path) : "";
        Settings.Editors.Save(editor, new EditorSettings(InData(program), editor.DefaultKind, editor.DefaultAddress, InData(reference)));
    }

    /// <summary>
    /// What a project's agent sessions would be given, without starting one: the briefing's text, and
    /// each skill's SKILL.md in the session's Claude Code plugin, by name.
    /// </summary>
    internal (string Briefing, Dictionary<string, string> Skills) BriefForChecks(string projectName)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var agentFolder = Settings.Folders.AgentFolder;
        var (others, references) = SessionSkills(project.Id, ProjectFolder(project.Id));
        var briefing = File.ReadAllText(AgentBriefing.Write(Settings.Editors, agentFolder, project.Id, others, references));
        var plugin = Path.Combine(AgentSkills.ClaudePlugin(agentFolder, project.Id, others), "skills");
        var skills = Directory.EnumerateDirectories(plugin).Where(folder => File.Exists(Path.Combine(folder, "SKILL.md")))
            .ToDictionary(folder => Path.GetFileName(folder), folder => File.ReadAllText(Path.Combine(folder, "SKILL.md")));
        return (briefing, skills);
    }

    internal int GiveAssetsAStatusForChecks()
    {
        var given = _database.GiveAssetsAStatus();
        if (_openProject is { } open) RenderProject(open);
        return given;
    }

    /// <summary>Opens a project's page as if its check had found everything in order, without running one.</summary>
    internal void OpenForChecks(string name)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == name) ?? throw new InvalidOperationException($"There's no project called {name}.");
        var folder = _database.GetProjectFolder(project.Id);
        _navigation++;
        _openProject = project;
        _openSection = null;
        _openCard = null;
        _pageProblem = null;
        _pageCheck = new PageCheck(new RepoCheck(RepoAccess.Allowed), folder, null, folder is not null && Directory.Exists(folder), null, false, false, null, null, null);
        ProjectSelectView.Visibility = Visibility.Collapsed;
        ProjectView.Visibility = Visibility.Visible;
        RenderProject(project);
    }
}
#endif
