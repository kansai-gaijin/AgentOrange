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
    internal Task<(string Text, bool Refused)> CallMcpForChecks(string projectName, string tool, IReadOnlyList<string> pairs, string? asConversation = null)
    {
        var given = new System.Text.Json.Nodes.JsonObject();
        for (var i = 0; i + 1 < pairs.Count; i += 2) given[pairs[i]] = pairs[i + 1];
        return CallMcpForChecks(projectName, tool, given, asConversation);
    }

    /// <summary>The same, with the arguments as JSON, for lists such as add_cards' cards, and as the conversation with that title calls it, when one is named.</summary>
    internal async Task<(string Text, bool Refused)> CallMcpForChecks(string projectName, string tool, System.Text.Json.Nodes.JsonObject given, string? asConversation = null)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        McpCaller.Value = asConversation is null ? null : ConversationTitled(asConversation).Id;
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
    /// A made-up tool set up as the engine or tool given (<see cref="FakeTool"/>), with its MCP served in
    /// the app and its program running: faketool godot, then faketool godot 404 3 to turn the next 3
    /// requests away, busy 2 for the next 2 calls to say busy, program to start its program again, or
    /// cancelled to say how many calls it was told were cancelled.
    /// </summary>
    internal string FakeToolForChecks(string id, string[] how)
    {
        var editor = EditorMonitor.Definitions.FirstOrDefault(each => each.Id == id) ?? throw new InvalidOperationException($"There's no tool called {id}.");
        var tool = FakeTool.Start(Path.Combine(Headless.DataFolder, "programs", "aofaketool.exe"));
        if (Settings.Editors.SettingsOf(editor).Address != tool.Address) Settings.Editors.Save(editor, new EditorSettings(tool.ProgramPath, McpKind.Http, tool.Address));
        switch (how.FirstOrDefault())
        {
            case "404":
                tool.TurnAway(int.Parse(how[1], CultureInfo.InvariantCulture));
                break;
            case "busy":
                tool.Busy(int.Parse(how[1], CultureInfo.InvariantCulture));
                break;
            case "cancelled":
                lock (tool.Cancelled) return $"Told of {tool.Cancelled.Count} cancelled calls.";
            default:
                tool.StartProgram();
                break;
        }
        return $"{editor.Name} is made up, at {tool.Address}, its program {(tool.Program is { HasExited: false } ? "running" : "not running")}.";
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

    /// <summary>
    /// A Claude login whose program is a script in the data folder: its version, then for its login
    /// status whatever programs/folder/status.json says, and nothing at all when asked its usage.
    /// </summary>
    internal void AddLoginForChecks(string label, string folder)
    {
        var place = Path.Combine(Headless.DataFolder, "programs", folder);
        Directory.CreateDirectory(place);
        var program = Path.Combine(place, "claude.cmd");
        File.WriteAllText(program, "@echo off\r\nif \"%~1\"==\"--version\" (echo 9.9.9 ^(Claude Code^)& exit /b 0)\r\nif \"%~1\"==\"auth\" (type \"%~dp0status.json\"& exit /b 0)\r\nexit /b 0\r\n");
        Settings.Providers.Save(new AgentProvider(Guid.NewGuid().ToString("N"), ProviderKind.Claude, label, program, Settings.Providers.NewLoginFolder(ProviderKind.Claude), true));
    }

    /// <summary>What an agent session of the project is given, written as a session's are, without a login to start one.</summary>
    internal string AgentSetupForChecks(string projectName)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var agentFolder = Settings.Folders.AgentFolder;
        var mcp = AgentMcpConfig.Write(Settings.Editors, agentFolder, project.Id);
        var helpers = WriteHelperBriefing(agentFolder, project.Id);
        var settings = WriteClaudeSettings(mcp, AgentSkills.List(agentFolder), [], agentFolder, helpers);
        var briefing = AgentBriefing.Write(Settings.Editors, agentFolder, project.Id, [], projectNotes: ProjectBriefingNotes(project.Id));

        // How its program would be started, by a made-up login that's never run.
        var login = new AgentProvider("check", ProviderKind.Claude, "Check", "claude", Path.Combine(agentFolder, "check-login"), true);
        var start = AgentProviders.SessionStartInfo(login, "-p", ProjectFolder(project.Id) ?? agentFolder, Ripgrep.Folder(agentFolder));
        var started = $"Started as: {start.FileName} {start.Arguments}\n" + string.Join("\n", new[] { "PYTHONUTF8", "PYTHONIOENCODING", "VSLANG", "DOTNET_CLI_UI_LANGUAGE", "LANG", "PATH" }
            .Select(name => $"{name}={(start.Environment.TryGetValue(name, out var value) ? value : "")}"));
        // And a made-up Codex login's: its own skills switched off for the session (#89).
        var codexLogin = new AgentProvider("check-codex", ProviderKind.Codex, "Check Codex", "codex", Path.Combine(agentFolder, "check-codex-login"), true);
        started += $"\nCodex started as: {AgentProviders.SessionStartInfo(codexLogin, $"app-server{AgentConversation.LoginSkillsOff(codexLogin.LoginFolder)}", ProjectFolder(project.Id) ?? agentFolder).Arguments}";
        // What a helper agent is told as it starts, through the hook, as Claude Code would run it.
        using var helperSaid = new StringWriter();
        ProjectLessons.RunHelperHook(helpers, helperSaid);
        return string.Join("\n", new[] { settings, mcp, briefing }.Select(file => $"{Path.GetFileName(file)}:\n{File.ReadAllText(file)}")) + "\n" + started
            + $"\nA helper agent is given: {(helperSaid.ToString() is { Length: > 0 } given ? given : "nothing")}";
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

    /// <summary>One of the project's documents, by its key, as a person's edit on its page saves it.</summary>
    internal void WriteDocumentForChecks(string projectName, string key, string markdown)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        _database.SetDocument(project.Id, key, markdown);
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
    }

    /// <summary>
    /// A conversation about the project, working on a task through AO's tools, that a usage limit
    /// stops mid-task, read as Claude Code says it, for Agent Orange to write its handoff note.
    /// </summary>
    internal string LimitStopForChecks(string projectName, string taskId, string? loginLabel = null)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        _ = Conversations; // Read from the agent files folder first, as opening the agents window does.
        var conversation = NewConversation(project);
        _checkConversation = conversation;
        if (loginLabel is not null) conversation.ProviderId = AllAgentLogins().FirstOrDefault(each => each.Label == loginLabel)?.Id ?? throw new InvalidOperationException($"There's no login called {loginLabel}.");
        conversation.Title = "Light the cave";
        conversation.Items.Add(new AgentItem { Id = "asked", Kind = AgentItemKind.User, Text = "Light the cave, then add its sound." });
        conversation.ReadForChecks($$$"""{"type":"assistant","message":{"id":"m1","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"TodoWrite","input":{"todos":[{"content":"Place the lights","status":"completed"},{"content":"Bake the lighting","status":"in_progress"},{"content":"Add the sound","status":"pending"}]}},{"type":"tool_use","id":"t2","name":"mcp__agent-orange__update_task","input":{"id":"{{{taskId}}}","notes":"Lights placed."}}]}}""");
        conversation.ReadForChecks($$$"""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"Changed. Now: [ ] Light the cave (task {{{taskId}}})"}]}}""");
        conversation.ReadForChecks("""{"type":"assistant","message":{"id":"m2","role":"assistant","content":[{"type":"text","text":"The lights are placed, and baking them is next."}]}}""");
        conversation.ReadForChecks("""{"type":"result","is_error":true,"result":"Claude AI usage limit reached|1900000000"}""");
        ConversationChanged(conversation);
        return $"Stopped by a limit: {conversation.IsLimited}. Its note written: {conversation.LimitHandoffWritten}.";
    }

    /// <summary>Whether settling a conversation asks its agent for a handoff note first, in the cases that decide it.</summary>
    internal async Task<string> SettleAsksForChecks(string projectName)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        _ = Conversations;
        AgentConversation Made(params AgentItem[] items)
        {
            var made = NewConversation(project);
            made.Items.AddRange(items);
            return made;
        }
        var asked = Made(new AgentItem { Id = "a", Kind = AgentItemKind.User, Text = "Light the cave." });
        var wrote = Made(new AgentItem { Id = "a", Kind = AgentItemKind.User, Text = "Light the cave." }, new AgentItem { Id = "b", Kind = AgentItemKind.Tool, Tool = "mcp__agent-orange__write_handoff" });
        var onlyAgentOrange = Made(new AgentItem { Id = "a", Kind = AgentItemKind.User, Text = "Continue where you left off.", Automatic = true });
        var said = $"Given work: {WantsHandoff(asked)}. After its note: {WantsHandoff(wrote)}. Only Agent Orange's messages: {WantsHandoff(onlyAgentOrange)}.";
        // Without a login to ask with, it's settled straight away.
        var settled = await SettleConversationAsync(asked);
        return $"{said} Without a login, settled straight away: {settled && asked.Settled && !asked.SettlesAfterHandoff}.";
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

    private AgentConversation? _checkConversation;

    /// <summary>
    /// A conversation about a project with a message sent, as Claude Code or Codex would have it, but
    /// with no program behind it: what it says back is fed in from recorded lines. Shown in the agents
    /// window when it's open.
    /// </summary>
    internal void AgentForChecks(string projectName, string text, bool codex)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        _checkConversation = NewConversation(project);
        _checkConversation.SentForChecks(text, codex);
        _agentsWindow?.ShowForChecks(_checkConversation);
    }

    internal AgentConversation CheckConversation => _checkConversation ?? throw new InvalidOperationException("Start a conversation with agent first.");

    /// <summary>The check's conversation as it stands: its login, and each thing in it, a line each.</summary>
    internal string ConversationItemsForChecks()
    {
        var conversation = CheckConversation;
        string Login(string? id) => AllAgentLogins().FirstOrDefault(each => each.Id == id)?.Label ?? id ?? "none";
        var said = new System.Text.StringBuilder($"Login: {Login(conversation.ProviderId)}. Limited: {conversation.IsLimited}. Working: {conversation.IsWorking}.\n");
        foreach (var item in conversation.Items)
        {
            said.AppendLine(item.Kind == AgentItemKind.Handoff
                ? $"Handoff: {Login(item.From?.ProviderId)} → {Login(item.To?.ProviderId)}"
                : $"{item.Kind}{(item.Automatic ? " (Agent Orange)" : "")}: {item.Text}");
        }
        return said.ToString();
    }

    /// <summary>A setting kept on this PC, as its page would save it.</summary>
    internal void SetSettingForChecks(string key, string value) => _database.SetSetting(key, value);

    /// <summary>
    /// How long a check login's program takes over its turn, as script lines: so many seconds, or with
    /// null, until the check releases it (<see cref="ReleaseLoginForChecks"/>), so a check that looks
    /// for it working can't find it already stopped. A held one gives up after 4 minutes, so a check
    /// that fails first leaves nothing running for long.
    /// </summary>
    private static string[] TakesForChecks(string place, int? seconds) => seconds is { } wait
        ? [$"ping -n {wait + 1} 127.0.0.1 >nul"]
        : [
            "set /a held=0",
            ":hold",
            $"if exist \"{Path.Combine(place, "release")}\" goto released",
            "set /a held+=1",
            "if %held% geq 240 goto released",
            "ping -n 2 127.0.0.1 >nul",
            "goto hold",
            ":released",
        ];

    /// <summary>The programs of a login made with hold finish their turns, as they would have once their work was done.</summary>
    internal static void ReleaseLoginForChecks(string folder)
    {
        var place = Path.Combine(Headless.DataFolder, "programs", folder);
        if (!File.Exists(Path.Combine(place, "claude.cmd"))) throw new InvalidOperationException($"No check login's program is in programs/{folder}.");
        File.WriteAllText(Path.Combine(place, "release"), "");
    }

    /// <summary>
    /// A Claude login whose program is a script that takes a turn as Claude Code would, slowly: it reads
    /// the message, says it started, waits so many seconds (or with null, until released), then says it
    /// finished, and ends.
    /// </summary>
    internal void AddSlowLoginForChecks(string label, string folder, int? seconds)
    {
        var place = Path.Combine(Headless.DataFolder, "programs", folder);
        Directory.CreateDirectory(place);
        File.Delete(Path.Combine(place, "release"));
        var program = Path.Combine(place, "claude.cmd");
        File.WriteAllText(program, string.Join("\r\n", [
            "@echo off",
            "if \"%~1\"==\"--version\" (echo 9.9.9 ^(Claude Code^)& exit /b 0)",
            "if \"%~1\"==\"auth\" (echo {\"loggedIn\":true}& exit /b 0)",
            "set /p asked=",
            "echo {\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"slow-check\"}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Started the long job.\"}]}}",
            .. TakesForChecks(place, seconds),
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m2\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Finished the long job.\"}]}}",
            "echo {\"type\":\"result\",\"is_error\":false,\"result\":\"Finished the long job.\"}",
            ""]));
        Settings.Providers.Save(new AgentProvider(Guid.NewGuid().ToString("N"), ProviderKind.Claude, label, program, Settings.Providers.NewLoginFolder(ProviderKind.Claude), true));
    }

    /// <summary>
    /// A Claude login whose program does a part's work of a split milestone, as Claude Code would take a
    /// turn: it writes a file named after the folder it works in (and with clash, the same shared.txt
    /// every part writes, which conflicts), commits it there, waits so many seconds (or with null, until
    /// released), and finishes. Called again with the same label, its program is written again.
    /// </summary>
    internal void AddWorkLoginForChecks(string label, string folder, int? seconds, bool clash)
    {
        var place = Path.Combine(Headless.DataFolder, "programs", folder);
        Directory.CreateDirectory(place);
        File.Delete(Path.Combine(place, "release"));
        var program = Path.Combine(place, "claude.cmd");
        File.WriteAllText(program, string.Join("\r\n", [
            "@echo off",
            "if \"%~1\"==\"--version\" (echo 9.9.9 ^(Claude Code^)& exit /b 0)",
            "if \"%~1\"==\"auth\" (echo {\"loggedIn\":true}& exit /b 0)",
            "set /p asked=",
            "echo {\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"part-check\"}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Started on my part.\"}]}}",
            "for %%I in (.) do set here=%%~nxI",
            "echo %here%> \"%here%.txt\"",
            clash ? "echo %here%> shared.txt" : "rem",
            "git add -A >nul 2>&1",
            "git -c user.name=\"Agent Orange check\" -c user.email=agent-orange-check commit -q -m \"Work of %here%\" >nul 2>&1",
            .. TakesForChecks(place, seconds),
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m2\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Committed my part.\"}]}}",
            "echo {\"type\":\"result\",\"is_error\":false,\"result\":\"Committed my part.\"}",
            ""]));
        if (Settings.Providers.List().Any(each => each.Label == label)) return;
        Settings.Providers.Save(new AgentProvider(Guid.NewGuid().ToString("N"), ProviderKind.Claude, label, program, Settings.Providers.NewLoginFolder(ProviderKind.Claude), true));
    }

    /// <summary>The project's milestones split across agents, how each part stands, and its folder's files, changes and latest commits.</summary>
    internal async Task<string> ParallelForChecksAsync(string projectName)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var said = new System.Text.StringBuilder();
        foreach (var run in ParallelRunsHere.Where(each => each.ProjectId == project.Id))
        {
            said.AppendLine(PartsReport(run));
            foreach (var part in run.Parts.Where(part => part.Worktree is not null)) said.AppendLine($"Part {part.Number}'s worktree is {(Directory.Exists(part.Worktree) ? "there" : "gone")}.");
        }
        var folder = ProjectFolder(project.Id)!;
        said.AppendLine($"Files: {string.Join(", ", Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal))}");
        said.AppendLine($"Not committed: {(await GitTools.NotCommittedAsync(folder) is { } left ? left.Count == 0 ? "none" : string.Join(", ", left) : "unknown")}");
        said.Append($"Commits: {await GitTools.LogForChecksAsync(folder)}");
        return said.ToString();
    }

    /// <summary>A new conversation about the project, sent a message for real, as the person sends one: its program is started in a session of its own.</summary>
    internal async Task AgentSendForChecks(string projectName, string text)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        _ = Conversations;
        var conversation = NewConversation(project);
        _checkConversation = conversation;
        if (await AgentProblemAsync(conversation) is { } problem) throw new InvalidOperationException(problem);
        conversation.Send(text, [], ViewLabel(), DescribeView(), () => PrepareAgent(conversation));
    }

    /// <summary>The conversation the check goes on with, from those read as the app started, by its title.</summary>
    internal void PickConversationForChecks(string title) => _checkConversation = ConversationTitled(title);

    /// <summary>
    /// As if the PC restarted: the check conversation's session is ended, and the app with it, at
    /// once, without saving anything more.
    /// </summary>
    internal void CrashForChecks()
    {
        var sessions = SessionsFolder(CheckConversation);
        foreach (var host in Directory.Exists(sessions) ? Directory.EnumerateFiles(sessions, "host.json", SearchOption.AllDirectories) : [])
        {
            var id = System.Text.Json.JsonDocument.Parse(File.ReadAllText(host)).RootElement.GetProperty("HostId").GetInt32();
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        Headless.Say("done");
        Environment.Exit(0);
    }

    /// <summary>
    /// Opens a project's page with its real check, except that GitHub's and Git's answer about access
    /// is made up and comes after a wait, and nothing else asks GitHub. The page check's time limit
    /// can be made shorter, and the project can be one this PC has never worked on.
    /// </summary>
    internal void OpenWithCheckForChecks(string name, int waitMs, string access, int? limitMs, bool newHere)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == name) ?? throw new InvalidOperationException($"There's no project called {name}.");
        _madeUpRepoCheck = (TimeSpan.FromMilliseconds(waitMs), Enum.Parse<RepoAccess>(access));
        if (limitMs is { } limit) RepoCheckLimit = TimeSpan.FromMilliseconds(limit);
        _database.SetSetting(VerifiedSetting(project.Id), newHere ? "" : GitTools.RepoKey(project.RepoUrl));
        _openSection = null;
        _openCard = null;
        _ = OpenProjectAsync(project);
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
