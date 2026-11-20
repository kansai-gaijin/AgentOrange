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
    internal string SeedForChecks(string name, int tasks, int milestones, int assets)
    {
        var projectId = _database.CreateProject(name, "Godot", "2D", $"https://github.com/example/{GitTools.RepoName(name)}");
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
    internal Task<(string Text, bool Refused)> CallMcpForChecks(string projectName, string tool, IReadOnlyList<string> pairs)
    {
        var given = new System.Text.Json.Nodes.JsonObject();
        for (var i = 0; i + 1 < pairs.Count; i += 2) given[pairs[i]] = pairs[i + 1];
        return CallMcpForChecks(projectName, tool, given);
    }

    /// <summary>The same, with the arguments as JSON, for lists such as add_cards' cards.</summary>
    internal async Task<(string Text, bool Refused)> CallMcpForChecks(string projectName, string tool, System.Text.Json.Nodes.JsonObject given)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var result = await McpCallAsync(project.Id, tool, given);
        var text = string.Join("\n", (result["content"] as System.Text.Json.Nodes.JsonArray ?? []).Select(part => part?["text"]?.GetValue<string>()).OfType<string>());
        return (text, result["isError"]?.GetValue<bool>() == true);
    }

    /// <summary>What an agent session of the project is given, written as a session's are, without a login to start one.</summary>
    internal string AgentSetupForChecks(string projectName)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var agentFolder = Settings.Folders.AgentFolder;
        var mcp = AgentMcpConfig.Write(Settings.Editors, agentFolder, project.Id);
        var settings = WriteClaudeSettings(mcp, AgentSkills.List(agentFolder), agentFolder);
        var briefing = AgentBriefing.Write(Settings.Editors, agentFolder, project.Id, []);

        // How its program would be started, by a made-up login that's never run.
        var login = new AgentProvider("check", ProviderKind.Claude, "Check", "claude", Path.Combine(agentFolder, "check-login"), true);
        var start = AgentProviders.SessionStartInfo(login, "-p", ProjectFolder(project.Id) ?? agentFolder, Ripgrep.Folder(agentFolder));
        var started = $"Started as: {start.FileName} {start.Arguments}\n" + string.Join("\n", new[] { "PYTHONUTF8", "PYTHONIOENCODING", "VSLANG", "DOTNET_CLI_UI_LANGUAGE", "LANG", "PATH" }
            .Select(name => $"{name}={(start.Environment.TryGetValue(name, out var value) ? value : "")}"));
        return string.Join("\n", new[] { settings, mcp, briefing }.Select(file => $"{Path.GetFileName(file)}:\n{File.ReadAllText(file)}")) + "\n" + started;
    }

    /// <summary>Writes a file in the project's folder, by a path inside it, or in the agent files folder, for a project called "(agent files)".</summary>
    internal void WriteFileForChecks(string projectName, string path, string text)
    {
        var folder = projectName == "(agent files)"
            ? Settings.Folders.AgentFolder
            : ProjectFolder((_database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.")).Id);
        var file = Path.Combine(folder ?? throw new InvalidOperationException($"{projectName} has no folder."), path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, text);
    }

    /// <summary>A small picture in the project's folder, such as a screenshot an agent took.</summary>
    internal void WritePictureForChecks(string projectName, string path)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var file = Path.Combine(ProjectFolder(project.Id) ?? throw new InvalidOperationException($"{projectName} has no folder."), path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, SeedPicture() ?? throw new InvalidOperationException("The picture couldn't be made."));
    }

    /// <summary>A project's task, milestone and lessons records as stored, for a check to see what was kept.</summary>
    internal string StoredForChecks(string projectName)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var stage = _database.ProjectStage(project.Id);
        var said = new System.Text.StringBuilder();
        foreach (var (id, milestone) in _database.ListMilestones(project.Id, stage)) said.AppendLine($"milestone {Short(id)}: {System.Text.Json.JsonSerializer.Serialize(milestone)}");
        foreach (var (id, task) in _database.ListTasks(project.Id, stage)) said.AppendLine($"task {Short(id)}: {System.Text.Json.JsonSerializer.Serialize(task)}");
        return said.ToString();
    }

    /// <summary>Agent Orange's MCP tools as a session lists them.</summary>
    internal async Task<string> McpToolsForChecks() => (await McpAnswerAsync("", "tools/list", null))?.ToJsonString() ?? "";

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
