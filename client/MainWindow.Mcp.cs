using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// The tools Agent Orange's MCP server gives agents (<see cref="McpServer"/>): reading the project,
/// its cards' documents and its tasks, searching them, and writing documents and tasks. Each works on
/// the project the agent's session is for, in the stage it's at. Nothing here delivers an asset:
/// that's only ever the person's to do.
/// </summary>
public partial class MainWindow
{
    private McpServer? _mcp;

    private void StartMcpServer()
    {
        _mcp = new McpServer((projectId, method, parameters) => Dispatcher.InvokeAsync(() => McpAnswerAsync(projectId, method, parameters)).Task.Unwrap());
        _mcp.Start();
    }

    private const string McpInstructions =
        "Agent Orange holds the whole design of the game: there is no separate design document. Start with get_project, "
        + "then read the cards that matter to your work with read_card (a card's document is its design, in Markdown), and the tasks with list_tasks. "
        + "Keep it current as you work: tick tasks you finish with update_task, add tasks you find with add_task, add cards the design needs with add_card (such as the assets a slice needs), and write what you settle into the card's document with write_card. "
        + "Settle nothing that's the person's to decide: ask them, then write it down. Never mark an asset as delivered.";

    private async Task<JsonNode?> McpAnswerAsync(string projectId, string method, JsonNode? parameters) => method switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "agent-orange", ["version"] = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0" },
            ["instructions"] = McpInstructions,
        },
        "ping" => new JsonObject(),
        "tools/list" => new JsonObject { ["tools"] = new JsonArray([.. McpTools.Select(tool => (JsonNode)tool.Describe())]) },
        "tools/call" => await McpCallAsync(projectId, parameters?["name"]?.GetValue<string>() ?? "", parameters?["arguments"] as JsonObject ?? []),
        _ => throw new McpException(-32601, $"Agent Orange doesn't know {method}."),
    };

    private sealed record McpTool(string Name, string Description, JsonObject Properties, string[] Required, Func<Project, JsonObject, object> Run, Func<Project, JsonObject, Task<object>>? RunAsync = null)
    {
        public JsonObject Describe() => new()
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = Properties.DeepClone(), ["required"] = new JsonArray([.. Required.Select(each => (JsonNode)each)]) },
        };
    }

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject Number(string description) => new() { ["type"] = "integer", ["description"] = description };

    private const string PageNote = "The page: Overview, Story, Characters, Levels, Mechanics or Assets.";
    private const string CardNote = "The card's name, or its id. Leave out for the Overview.";

    private List<McpTool> McpTools =>
    [
        new("get_project", "Start here: the project at a glance. Its engine, stage, folder and game settings, every page's cards, the milestones and how far the tasks are, and the project's own documents.", [], [], (project, _) => DescribeProject(project)),
        new("list_cards", "The cards on a page, with how much is written in each. For assets, also their type, what they belong to, when they're due and whether they're delivered.",
            new JsonObject { ["page"] = Text(PageNote) }, ["page"], (project, given) => ListCardsFor(project, Page(given))),
        new("read_card", "A card's document, which is its design, in Markdown, with the tasks about it. Or the Overview's document, with page Overview.",
            new JsonObject { ["page"] = Text(PageNote), ["card"] = Text(CardNote) }, ["page"], (project, given) => ReadCard(project, given)),
        new("write_card", "Writes a card's document, or the Overview's, in Markdown. mode replace swaps the whole document for this; append adds this at its end. Write what's been settled, in the document's own style, with ## and ### headings.",
            new JsonObject { ["page"] = Text(PageNote), ["card"] = Text(CardNote), ["markdown"] = Text("The Markdown."), ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("replace", "append"), ["description"] = "replace or append. append when not given." } },
            ["page", "markdown"], (project, given) => WriteCard(project, given)),
        new("add_card", "Adds a card to a page: a story beat, character, level, mechanic or asset, as the page's Add form does, with its document if given. An asset can also be given its type, the card it belongs to and a day to deliver it by. Never fill an asset's deliverable: only the person does, once they've judged the result.",
            new JsonObject
            {
                ["page"] = Text("Story, Characters, Levels, Mechanics or Assets."),
                ["name"] = Text("The card's name."),
                ["markdown"] = Text("Its document, in Markdown, if there's something to say yet."),
                ["type"] = Text("For an asset: what it is as a file, one of " + string.Join(", ", AssetTypes.SelectMany(group => group.Types).Select(type => type.Name)) + "."),
                ["belongs_to_page"] = Text("For an asset: the page of the card it belongs to (Story, Characters, Levels or Mechanics)."),
                ["belongs_to"] = Text("For an asset: the card it belongs to, by name or id."),
                ["due"] = Text("For an asset: a day to deliver it by, as yyyy-mm-dd. Makes it a task."),
            },
            ["page", "name"], (project, given) => AddCardFor(project, given)),
        new("list_tasks", "The milestones, each with what it's for and when it's done, and their tasks with ids, ticks, dates and the cards they're about.",
            new JsonObject { ["milestone"] = Text("Only this milestone, by its name or the start of it."), ["open_only"] = Flag("Leave out tasks that are done.") }, [], (project, given) => ListTasksFor(project, given)),
        new("add_task", "Adds a task, such as one found while working.",
            new JsonObject { ["title"] = Text("What's to be done."), ["notes"] = Text("Anything more about it."), ["milestone"] = Text("Its milestone, by name or the start of it."), ["page"] = Text("The page of the card it's about."), ["card"] = Text("The card it's about, by name or id."), ["due"] = Text("A day to have it done by, as yyyy-mm-dd.") },
            ["title"], (project, given) => AddTaskFor(project, given)),
        new("update_task", "Changes a task: ticks it done (or not), or changes what it says, its notes, date, milestone, card or place in the list.",
            new JsonObject { ["id"] = Text("The task's id, from list_tasks."), ["done"] = Flag("Whether it's done."), ["title"] = Text("What's to be done."), ["notes"] = Text("Its notes, replacing the old ones."), ["due"] = Text("yyyy-mm-dd, or empty for none."), ["milestone"] = Text("Its milestone, by name, or empty for none."), ["page"] = Text("The page of the card it's about."), ["card"] = Text("The card it's about, or empty for none."), ["position"] = Number("Where it goes among its milestone's tasks, 1 for first; they're listed in this order.") },
            ["id"], (project, given) => UpdateTaskFor(project, given)),
        new("search", "Finds words anywhere in the design: every document, card name and task, with where each is.",
            new JsonObject { ["text"] = Text("What to find. Not case-sensitive.") }, ["text"], (project, given) => Search(project, Required(given, "text"))),
        new("read_picture", "A picture to look at: one from a document, given as ![](picture:id) in its Markdown, or a card's own picture, such as its concept art, given by read_card as picture:card-id.",
            new JsonObject { ["id"] = Text("What follows picture: in the document or in read_card.") }, ["id"], (project, given) => ReadPicture(project, Required(given, "id"))),

        // The creative tools, such as Blender, Aseprite, ArmorPaint and Material Maker.
        new("list_tools", "The creative tools set up on this PC, such as Blender, Aseprite, ArmorPaint and Material Maker, and the game's engine, such as Unreal Engine, and whether each is open and its MCP answers.",
            [], [], (_, _) => "", (project, _) => ListToolsForAgentAsync(project)),
        new("open_tool", "Opens a creative tool in the project's folder, or the game's engine with its project, if it isn't open, and waits until its MCP answers. Then tool_tools and use_tool can use it.",
            new JsonObject { ["tool"] = Text("The tool's name, such as Blender or Aseprite.") }, ["tool"], (_, _) => "", OpenToolForAgentAsync),
        new("tool_tools", "What an open creative tool or engine can do: its own tools, with their arguments, for use_tool.",
            new JsonObject { ["tool"] = Text("The tool's name.") }, ["tool"], (_, _) => "", ToolToolsAsync),
        new("use_tool", "Uses one of an open creative tool's or engine's own tools, as tool_tools lists them, and gives back what it gave, pictures included.",
            new JsonObject { ["tool"] = Text("The tool's name, such as Blender."), ["name"] = Text("Its tool to use, from tool_tools."), ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "That tool's arguments." } },
            ["tool", "name"], (_, _) => "", UseToolAsync),
    ];

    private async Task<JsonObject> McpCallAsync(string projectId, string name, JsonObject given)
    {
        if (McpTools.FirstOrDefault(tool => tool.Name == name) is not { } tool) throw new McpException(-32602, $"There's no tool called {name}.");
        if (_database.ListProjects().FirstOrDefault(project => project.Id == projectId) is not { } project)
        {
            return ToolResult("This session isn't for a project Agent Orange has. Start the conversation from a project.", error: true);
        }
        try
        {
            var result = tool.RunAsync is { } runAsync ? await runAsync(project, given) : tool.Run(project, given);
            return result is JsonObject made ? made : ToolResult(result.ToString() ?? "");
        }
        catch (McpException error)
        {
            return ToolResult(error.Message, error: true);
        }
    }

    private static JsonObject ToolResult(string text, bool error = false)
    {
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (error) result["isError"] = true;
        return result;
    }

    private static string? Given(JsonObject given, string name) => given[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static string Required(JsonObject given, string name) =>
        Given(given, name) is { Length: > 0 } text ? text : throw new McpException(-32602, $"Say {name}.");

    private static string Page(JsonObject given)
    {
        var asked = Required(given, "page");
        if (asked.Equals("Overview", StringComparison.OrdinalIgnoreCase)) return "Overview";
        return CardPages.Keys.FirstOrDefault(page => page.Equals(asked, StringComparison.OrdinalIgnoreCase)
            || CardPages[page].One.Equals(asked, StringComparison.OrdinalIgnoreCase)) ?? throw new McpException(-32602, $"There's no page called {asked}. {PageNote}");
    }

    private BoardCard FindCard(Project project, string page, string asked)
    {
        var cards = _database.ListCards(project.Id, _database.ProjectStage(project.Id), page.ToLowerInvariant());
        return cards.FirstOrDefault(card => SameId(card.Id, asked))
            ?? cards.FirstOrDefault(card => CardName(card, page).Equals(asked, StringComparison.OrdinalIgnoreCase))
            ?? cards.FirstOrDefault(card => CardName(card, page).StartsWith(asked, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(-32602, $"{page} has no card called {asked}. Its cards: {string.Join(", ", cards.Select(card => CardName(card, page)))}.");
    }

    // The card asked for, or the Overview, and where its document is kept.
    private (string Name, string BoardKey, BoardCard? Card) DocumentFor(Project project, JsonObject given)
    {
        var page = Page(given);
        if (page == "Overview") return ("Overview", LocalDatabase.BoardKey(_database.ProjectStage(project.Id), BoardPart("Overview")), null);
        var card = FindCard(project, page, Required(given, "card"));
        return (CardName(card, page), LocalDatabase.CardBoardKey(card), card);
    }

    // What a card on a page is called, as the pages call it.
    internal static string KindOfCard(string page) => page == "Story" ? "story beat" : CardPages[page].One;

    private static int WordsIn(string? text) => text is null ? 0 : Regex.Matches(text, @"[\p{L}\p{N}]+").Count;

    // Ids as agents see them: without the project's id in front, which every one of them shares.
    private static string Short(string id) => id[(id.LastIndexOf(':') + 1)..];

    private static bool SameId(string id, string asked) => id == asked || Short(id) == asked.Trim();

    // A task as agents are told it, everything about it, so a change can be seen to have happened.
    private string TaskSummary(Project project, string id, ProjectTask task)
    {
        var stage = _database.ProjectStage(project.Id);
        var said = new StringBuilder($"[{(task.Done ? "x" : " ")}] {task.Title} (task {Short(id)})");
        if (task.Milestone is { } milestoneId && _database.ListMilestones(project.Id, stage).FirstOrDefault(each => each.Id == milestoneId) is { Milestone: not null } milestone) said.Append($", in {milestone.Milestone.Name}");
        if (task.Due is { } due) said.Append($", by {due}");
        if (task.Card is { } cardId && AssetOwnerChoices(_database, project.Id, stage).FirstOrDefault(choice => choice.Card.Id == cardId) is { Card: not null } belongs) said.Append($", about the {KindOfCard(belongs.Page)} {CardName(belongs.Card, belongs.Page)}");
        if (task.Notes is { Length: > 0 } notes) said.Append($". Notes: {notes.Replace("\n", " ")}");
        return said.ToString();
    }

    private string DescribeProject(Project project)
    {
        var stage = _database.ProjectStage(project.Id);
        var said = new StringBuilder();
        said.AppendLine($"# {project.Name}");
        said.AppendLine();
        said.AppendLine($"Made with {project.Engine}{(project.EngineVersion.Length > 0 ? $" {project.EngineVersion}" : "")}, a {project.GameType} game, at its {_database.ProjectStages(project.Id).FirstOrDefault(each => each.Id == stage)?.Name ?? stage} stage.");
        if (ProjectFolder(project.Id) is { } folder) said.AppendLine($"Its folder on this PC, a Git repo: {folder}");

        var settings = _database.GetGameSettings(project.Id).Named().ToList();
        said.AppendLine();
        said.AppendLine("## Game settings");
        foreach (var (name, value) in settings) said.AppendLine($"- {name}: {(value.Length > 0 ? value : "not decided yet")}");

        said.AppendLine();
        said.AppendLine("## Pages");
        var overview = _database.GetDocument(project.Id, LocalDatabase.BoardKey(stage, BoardPart("Overview")));
        said.AppendLine($"- Overview: the game as a whole, {WordsIn(overview)} words. Read it with read_card page Overview.");
        foreach (var page in CardPages.Keys)
        {
            var cards = _database.ListCards(project.Id, stage, page.ToLowerInvariant());
            said.AppendLine($"- {page} ({cards.Count}): {(cards.Count == 0 ? "none yet" : string.Join(", ", cards.Select(card => CardName(card, page))))}");
        }

        said.AppendLine();
        said.AppendLine("## Tasks");
        var tasks = _database.ListTasks(project.Id, stage);
        said.AppendLine($"{tasks.Count(each => each.Task.Done)} of {tasks.Count} done.");
        foreach (var (id, milestone) in _database.ListMilestones(project.Id, stage))
        {
            var inIt = tasks.Where(each => each.Task.Milestone == id).ToList();
            said.AppendLine($"- {milestone.Name}: {inIt.Count(each => each.Task.Done)} of {inIt.Count}");
        }

        if (ProjectFolder(project.Id) is { } inFolder && Directory.Exists(Path.Combine(inFolder, "docs")))
        {
            var documents = Directory.EnumerateFiles(Path.Combine(inFolder, "docs"), "*.md", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(inFolder, file).Replace('\\', '/')).ToList();
            if (documents.Count > 0)
            {
                said.AppendLine();
                said.AppendLine("## Documents in the folder");
                said.AppendLine("Read these as files. Documents link to them as doc:path.");
                foreach (var document in documents) said.AppendLine($"- {document}");
            }
        }
        return said.ToString();
    }

    private string ListCardsFor(Project project, string page)
    {
        if (page == "Overview") return "The Overview isn't a page of cards. Read it with read_card page Overview.";
        var stage = _database.ProjectStage(project.Id);
        var cards = _database.ListCards(project.Id, stage, page.ToLowerInvariant());
        if (cards.Count == 0) return $"{page} has no cards yet.";
        var said = new StringBuilder();
        foreach (var card in cards)
        {
            said.Append($"- {CardName(card, page)} (id {Short(card.Id)}): {WordsIn(_database.GetDocument(project.Id, LocalDatabase.CardBoardKey(card)))} words");
            if (_database.GetCardImage(card.Id) is not null) said.Append(", has a picture");
            if (page == AssetsPage)
            {
                var details = _database.GetAssetDetails(card.Id);
                said.Append($"; {AssetTypeName(details.Type) ?? "no type"}");
                if (AssetOwnerChoices(_database, project.Id, stage).FirstOrDefault(choice => choice.Card.Id == details.Owner) is { Card: not null } owner) said.Append($", belongs to {CardName(owner.Card, owner.Page)}");
                if (DueOf(details) is { } due) said.Append($", due {due:yyyy-MM-dd}");
                said.Append(_database.GetAssetDeliverable(card.Id) is { } given ? $", delivered as {given.Path}" : ", not delivered");
            }
            said.AppendLine();
        }
        return said.ToString();
    }

    private string ReadCard(Project project, JsonObject given)
    {
        var (name, boardKey, card) = DocumentFor(project, given);
        var said = new StringBuilder();
        said.AppendLine($"# {name}");
        if (card is not null) said.AppendLine($"({Page(given)} card, id {Short(card.Id)})");
        if (card is not null && _database.GetCardImage(card.Id) is not null) said.AppendLine($"Its picture, such as its concept art: picture:card-{Short(card.Id)}. Look at it with read_picture.");
        said.AppendLine();
        var document = _database.GetDocument(project.Id, boardKey);
        said.AppendLine(document is { Length: > 0 } ? document.Trim() : "Nothing written yet.");
        if (card is not null)
        {
            var tasks = _database.ListTasks(project.Id, _database.ProjectStage(project.Id)).Where(each => each.Task.Card == card.Id).ToList();
            if (tasks.Count > 0)
            {
                said.AppendLine();
                said.AppendLine("## Tasks about it");
                foreach (var (id, task) in tasks) said.AppendLine($"- [{(task.Done ? "x" : " ")}] {task.Title} (task {Short(id)})");
            }
            if (Page(given) == AssetsPage) said.AppendLine().AppendLine(DescribeAsset(project, card));
        }
        return said.ToString();
    }

    private string AddCardFor(Project project, JsonObject given)
    {
        var page = Page(given);
        if (page == "Overview") throw new McpException(-32602, "The Overview isn't a page of cards.");
        var name = Required(given, "name");
        var stage = _database.ProjectStage(project.Id);
        var part = page == AssetsPage ? AssetsPart : page.ToLowerInvariant();
        if (_database.ListCards(project.Id, stage, part).FirstOrDefault(card => CardName(card, page).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } there)
        {
            throw new McpException(-32602, $"{page} already has a card called {CardName(there, page)} (id {Short(there.Id)}). Write to it with write_card.");
        }

        // An asset's details are checked before anything is added, so a mistake adds nothing.
        AssetDetails? details = null;
        if (page == AssetsPage)
        {
            string? type = null;
            if (Given(given, "type") is { Length: > 0 } asked)
            {
                type = AssetTypes.SelectMany(group => group.Types).FirstOrDefault(each => each.Id.Equals(asked, StringComparison.OrdinalIgnoreCase) || each.Name.Equals(asked, StringComparison.OrdinalIgnoreCase)).Id
                    ?? throw new McpException(-32602, $"There's no asset type called {asked}. The types: {string.Join(", ", AssetTypes.SelectMany(group => group.Types).Select(each => each.Name))}.");
            }
            string? owner = null;
            if (Given(given, "belongs_to") is { Length: > 0 } ownerName)
            {
                var ownerPage = Given(given, "belongs_to_page") is { Length: > 0 } askedPage
                    ? AssetOwnerPages.FirstOrDefault(each => each.Equals(askedPage, StringComparison.OrdinalIgnoreCase) || CardPages[each].One.Equals(askedPage, StringComparison.OrdinalIgnoreCase)) ?? throw new McpException(-32602, $"An asset can belong to a card on {string.Join(", ", AssetOwnerPages)}.")
                    : throw new McpException(-32602, "Say belongs_to_page too: Story, Characters, Levels or Mechanics.");
                owner = FindCard(project, ownerPage, ownerName).Id;
            }
            var due = Given(given, "due") is { Length: > 0 } day
                ? (DateOnly.TryParseExact(day, "yyyy-MM-dd", out _) ? day : throw new McpException(-32602, "Give due as yyyy-mm-dd."))
                : null;
            details = new AssetDetails(Type: type, Owner: owner, Due: due);
        }
        else if (Given(given, "type") is { Length: > 0 } || Given(given, "belongs_to") is { Length: > 0 } || Given(given, "due") is { Length: > 0 })
        {
            throw new McpException(-32602, "type, belongs_to and due are for assets only.");
        }

        var added = _database.AddCard(project.Id, stage, part, name);
        if (details is not null) _database.SetAssetDetails(added.Id, details);
        if (Given(given, "markdown") is { Length: > 0 } markdown) _database.SetDocument(project.Id, LocalDatabase.CardBoardKey(added), markdown.Trim() + Environment.NewLine);
        McpChanged(project);
        return $"Added the {KindOfCard(page)} {name} (id {Short(added.Id)}) to {page}." + (details is null ? "" : $" {DescribeAsset(project, added)}");
    }

    private string WriteCard(Project project, JsonObject given)
    {
        var (name, boardKey, _) = DocumentFor(project, given);
        var markdown = Required(given, "markdown");
        var replace = Given(given, "mode") is "replace";
        var now = _database.GetDocument(project.Id, boardKey) ?? "";
        var written = replace ? markdown.Trim() + "\n" : (now.Trim().Length > 0 ? now.TrimEnd() + "\n\n" : "") + markdown.Trim() + "\n";
        _database.SetDocument(project.Id, boardKey, written);
        McpChanged(project);
        return $"{(replace ? "Replaced" : "Added to")} the document of {name}. It has {WordsIn(written)} words now.";
    }

    // Shown at once if it's open, unless someone's writing in it, and sent to other PCs.
    private void McpChanged(Project project)
    {
        _ = _sync.SyncNowAsync();
        if (_openProject?.Id == project.Id) RenderProjectKeepingFocus(_openProject);
    }

    private string ListTasksFor(Project project, JsonObject given)
    {
        var stage = _database.ProjectStage(project.Id);
        var openOnly = given["open_only"] is JsonValue flag && flag.TryGetValue<bool>(out var onlyOpen) && onlyOpen;
        var only = Given(given, "milestone");
        var tasks = _database.ListTasks(project.Id, stage);
        var milestones = _database.ListMilestones(project.Id, stage);
        var cards = AssetOwnerChoices(_database, project.Id, stage);
        var said = new StringBuilder();

        void Write(string id, ProjectTask task)
        {
            if (openOnly && task.Done) return;
            said.Append($"- [{(task.Done ? "x" : " ")}] {task.Title} (task {Short(id)})");
            if (task.Due is { } due) said.Append($", by {due}");
            if (task.Card is { } cardId && cards.FirstOrDefault(choice => choice.Card.Id == cardId) is { Card: not null } belongs) said.Append($", about the {KindOfCard(belongs.Page)} {CardName(belongs.Card, belongs.Page)}");
            said.AppendLine();
            if (task.Notes is { Length: > 0 } notes) said.AppendLine($"  Notes: {notes.Replace("\n", " ")}");
        }

        var onlyId = only is null ? null : MilestoneId(project, only);
        foreach (var (id, milestone) in milestones.Where(each => onlyId is null || each.Id == onlyId))
        {
            var inIt = tasks.Where(each => each.Task.Milestone == id).ToList();
            said.AppendLine($"## {milestone.Name} ({inIt.Count(each => each.Task.Done)} of {inIt.Count} done)");
            if (milestone.Goal is { } goal) said.AppendLine($"For: {goal}");
            if (milestone.DoneWhen is { } doneWhen) said.AppendLine($"Done when: {doneWhen}");
            foreach (var (taskId, task) in inIt) Write(taskId, task);
            said.AppendLine();
        }
        if (only is null)
        {
            var loose = tasks.Where(each => each.Task.Milestone is null || milestones.All(milestone => milestone.Id != each.Task.Milestone)).ToList();
            if (loose.Count > 0)
            {
                said.AppendLine("## Other tasks");
                foreach (var (taskId, task) in loose) Write(taskId, task);
            }
        }
        return said.Length > 0 ? said.ToString() : "No tasks match.";
    }

    private string? MilestoneId(Project project, string? asked)
    {
        if (asked is null) return null;
        if (asked.Length == 0) return "";
        // Exactly, then up to a word's end, so "Priority 1" isn't Priority 10, then any start.
        var milestones = _database.ListMilestones(project.Id, _database.ProjectStage(project.Id));
        return (milestones.FirstOrDefault(each => each.Id == asked || each.Milestone.Name.Equals(asked, StringComparison.OrdinalIgnoreCase)).Id
            ?? milestones.FirstOrDefault(each => Regex.IsMatch(each.Milestone.Name, $@"^{Regex.Escape(asked)}\b", RegexOptions.IgnoreCase)).Id
            ?? milestones.FirstOrDefault(each => each.Milestone.Name.StartsWith(asked, StringComparison.OrdinalIgnoreCase)).Id)
            ?? throw new McpException(-32602, $"There's no milestone called {asked}. They are: {string.Join("; ", milestones.Select(each => each.Milestone.Name))}.");
    }

    private string? CardId(Project project, JsonObject given)
    {
        if (Given(given, "card") is not { } asked) return null;
        if (asked.Length == 0) return "";
        return FindCard(project, Page(given), asked).Id;
    }

    private static string? Day(string? asked)
    {
        if (asked is null) return null;
        if (asked.Length == 0) return "";
        return DateOnly.TryParseExact(asked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? asked : throw new McpException(-32602, "Give the day as yyyy-mm-dd.");
    }

    private string AddTaskFor(Project project, JsonObject given)
    {
        var stage = _database.ProjectStage(project.Id);
        var milestone = MilestoneId(project, Given(given, "milestone")) is { Length: > 0 } chosen ? chosen : null;
        var order = _database.ListTasks(project.Id, stage).Where(each => each.Task.Milestone == milestone).Select(each => each.Task.Order).DefaultIfEmpty(0).Max() + 1;
        var card = CardId(project, given) is { Length: > 0 } cardId ? cardId : null;
        var due = Day(Given(given, "due")) is { Length: > 0 } day ? day : null;
        var id = _database.SaveTask(project.Id, null, new ProjectTask(stage, Required(given, "title"), Given(given, "notes") is { Length: > 0 } notes ? notes : null, due, false, milestone, order, card));
        McpChanged(project);
        return $"Added: {TaskSummary(project, id, _database.ListTasks(project.Id, stage).First(each => each.Id == id).Task)}";
    }

    private string UpdateTaskFor(Project project, JsonObject given)
    {
        var id = Required(given, "id");
        var tasks = _database.ListTasks(project.Id, _database.ProjectStage(project.Id));
        if (tasks.FirstOrDefault(each => SameId(each.Id, id)) is not { Task: not null } found) throw new McpException(-32602, $"There's no task {id}. list_tasks gives their ids.");
        var task = found.Task;
        if (given["done"] is JsonValue done && done.TryGetValue<bool>(out var isDone)) task = task with { Done = isDone };
        if (Given(given, "title") is { Length: > 0 } title) task = task with { Title = title };
        if (Given(given, "notes") is { } notes) task = task with { Notes = notes.Length > 0 ? notes : null };
        if (Day(Given(given, "due")) is { } due) task = task with { Due = due.Length > 0 ? due : null };
        if (MilestoneId(project, Given(given, "milestone")) is { } milestone) task = task with { Milestone = milestone.Length > 0 ? milestone : null };
        if (CardId(project, given) is { } card) task = task with { Card = card.Length > 0 ? card : null };
        if (given["position"] is JsonValue position && position.TryGetValue<int>(out var place))
        {
            // Between the two it now goes between, so nothing else needs moving.
            var others = tasks.Where(each => each.Task.Milestone == task.Milestone && !SameId(each.Id, found.Id)).Select(each => each.Task.Order).ToList();
            var at = Math.Clamp(place, 1, others.Count + 1) - 1;
            var before = at > 0 ? others[at - 1] : (others.Count > 0 ? others[0] - 2 : 0);
            var after = at < others.Count ? others[at] : before + 2;
            task = task with { Order = (before + after) / 2 };
        }
        _database.SaveTask(project.Id, found.Id, task);
        McpChanged(project);
        return $"Changed. Now: {TaskSummary(project, found.Id, task)}";
    }

    private string Search(Project project, string text)
    {
        var stage = _database.ProjectStage(project.Id);
        var found = new List<string>();
        // Each line it's in, under the heading it comes after, if it has one.
        void Look(string where, string? words)
        {
            if (words is null) return;
            string? heading = null;
            foreach (var line in words.Split('\n'))
            {
                var trimmed = line.Trim();
                var isHeading = Regex.Match(trimmed, @"^#{1,6}\s+(.*)$");
                if (isHeading.Success) heading = isHeading.Groups[1].Value.Trim();
                if (found.Count >= 40 || !line.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                var section = heading is not null && !isHeading.Success ? $" › {heading}" : "";
                found.Add($"- {where}{section}: \"{(trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed)}\"");
            }
        }

        Look("Overview", _database.GetDocument(project.Id, LocalDatabase.BoardKey(stage, BoardPart("Overview"))));
        foreach (var page in CardPages.Keys)
        {
            foreach (var card in _database.ListCards(project.Id, stage, page.ToLowerInvariant()))
            {
                var name = CardName(card, page);
                if (name.Contains(text, StringComparison.OrdinalIgnoreCase)) found.Add($"- {page}: the card {name}");
                Look($"{page} › {name}", _database.GetDocument(project.Id, LocalDatabase.CardBoardKey(card)));
            }
        }
        var milestones = _database.ListMilestones(project.Id, stage).ToDictionary(each => each.Id, each => each.Milestone.Name);
        foreach (var (id, task) in _database.ListTasks(project.Id, stage))
        {
            var where = $"Task {Short(id)}{(task.Milestone is { } milestone && milestones.TryGetValue(milestone, out var name) ? $" in {name}" : "")}";
            Look(where, task.Title);
            Look($"{where}, its notes", task.Notes);
        }
        return found.Count == 0 ? $"Nothing has \"{text}\" in it." : string.Join("\n", found) + (found.Count >= 40 ? "\n(The first 40.)" : "");
    }

    private JsonObject ReadPicture(Project project, string id)
    {
        if (id.StartsWith("picture:", StringComparison.Ordinal)) id = id["picture:".Length..];
        string? stored;
        if (id.StartsWith("card-", StringComparison.Ordinal))
        {
            // A card's own picture, found on whichever page the card is.
            var stage = _database.ProjectStage(project.Id);
            var card = CardPages.Keys.SelectMany(page => _database.ListCards(project.Id, stage, page == AssetsPage ? AssetsPart : page.ToLowerInvariant())).FirstOrDefault(each => SameId(each.Id, id["card-".Length..]));
            stored = card is null ? null : _database.GetCardImage(card.Id);
        }
        else stored = _database.GetDocumentPicture(id);
        if (stored is null) throw new McpException(-32602, $"There's no picture {id}.");
        var bytes = Convert.FromBase64String(stored);
        var type = bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png" : "image/jpeg";
        return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = stored, ["mimeType"] = type }) };
    }
}
