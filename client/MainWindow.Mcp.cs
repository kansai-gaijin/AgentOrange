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
        + "Keep it current as you work: tick tasks you finish with update_task, add tasks you find with add_task, add cards the design needs with add_card (such as the assets a slice needs), set an asset's status with update_asset as you work on it (In progress, then Needs review when it's ready for the person to judge), and write what you settle into the card's document with write_card. "
        + "Settle nothing that's the person's to decide: ask them, then write it down. Never mark an asset as delivered. "
        + "To wait for a file, a log line, a port or a process, use wait_for rather than sleep.";

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
            // Claude Code's own way for a server to have a tool always loaded, as the session's config also asks.
            ["_meta"] = new JsonObject { ["anthropic/alwaysLoad"] = true },
        };
    }

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject Number(string description) => new() { ["type"] = "integer", ["description"] = description };

    private const string PageNote = "The page: Overview, Story, Characters, Levels, Mechanics or Assets.";

    private static string StatusNote(string start) =>
        start + string.Join(", ", AssetStatus.All.Select(each => each.Name)) + ". Delivered is only ever set by the person.";

    // A status by its name or id. An agent can't make an asset delivered, which is the person's to judge.
    private static string StatusId(string asked, bool forAgent)
    {
        var status = AssetStatus.All.FirstOrDefault(each => each.Id.Equals(asked.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase) || each.Name.Equals(asked, StringComparison.OrdinalIgnoreCase)).Id
            ?? throw new McpException(-32602, $"status: there's no status called {asked}. The statuses: {string.Join(", ", AssetStatus.All.Select(each => each.Name))}.");
        if (forAgent && status == AssetStatus.Delivered)
            throw new McpException(-32602, "status: only the person marks an asset delivered, once they've judged it. Set it to Needs review when it's ready for them.");
        return status;
    }
    private const string CardNote = "The card's name, or its id. Leave out for the Overview.";

    private List<McpTool> McpTools => WithBulkTools(
    [
        new("get_project", "Start here: the project at a glance. Its engine, stage, folder and game settings, every page's cards, the milestones and how far the tasks are, and the project's own documents.", [], [], (project, _) => DescribeProject(project)),
        new("list_cards", "The cards on a page, with how much is written in each. For assets, also their status, type, what they belong to, when they're due and their deliverable once they have one. For assets, status leaves out all but one status.",
            new JsonObject { ["page"] = Text(PageNote), ["status"] = Text(StatusNote("Only assets with this status: ")) }, ["page"], (project, given) => ListCardsFor(project, Page(given), Given(given, "status") is { Length: > 0 } status ? StatusId(status, forAgent: false) : null)),
        new("read_card", "A card's document, which is its design, in Markdown, with the tasks about it. Or the Overview's document, with page Overview.",
            new JsonObject { ["page"] = Text(PageNote), ["card"] = Text(CardNote) }, ["page"], (project, given) => ReadCard(project, given)),
        new("write_card", "Writes a card's document, or the Overview's, in Markdown. mode replace swaps the whole document for this; append adds this at its end. Write what's been settled, in the document's own style, with ## and ### headings.",
            new JsonObject { ["page"] = Text(PageNote), ["card"] = Text(CardNote), ["markdown"] = Text("The Markdown."), ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("replace", "append"), ["description"] = "replace or append. append when not given." } },
            ["page", "markdown"], (project, given) => WriteCard(project, given)),
        new("add_card", "Adds a card to a page: a story beat, character, level, mechanic or asset, as the page's Add form does, with its document if given. An asset can also be given its type, the card it belongs to, its tags and a day to deliver it by. Never fill an asset's deliverable: only the person does, once they've judged the result.",
            new JsonObject
            {
                ["page"] = Text("Story, Characters, Levels, Mechanics or Assets."),
                ["name"] = Text("The card's name."),
                ["markdown"] = Text("Its document, in Markdown, if there's something to say yet."),
                ["type"] = Text("For an asset: what it is as a file, one of " + string.Join(", ", AssetTypes.SelectMany(group => group.Types).Select(type => type.Name)) + "."),
                ["belongs_to_page"] = Text("For an asset: the page of the card it belongs to (Story, Characters, Levels or Mechanics)."),
                ["belongs_to"] = Text("For an asset: the card it belongs to, by name or id."),
                ["due"] = Text("For an asset: a day to deliver it by, as yyyy-mm-dd. Makes it a task."),
                ["tags"] = Text("For an asset: its tags, by name, separated by commas, such as Forest, Sound. Use the ones list_cards Assets shows when they fit; new ones are made."),
                ["status"] = Text(StatusNote("For an asset: where it is in being made, To do if not given. ")),
            },
            ["page", "name"], (project, given) => AddCardFor(project, given)),
        new("update_asset", "Changes an asset's status, type, the card it belongs to, its tags or its day to deliver it by. Only what's given changes. Never its deliverable, and never Delivered: only the person does those.",
            new JsonObject
            {
                ["card"] = Text("The asset, by name or id."),
                ["status"] = Text(StatusNote("Where it is in being made: ")),
                ["type"] = Text("What it is as a file, one of " + string.Join(", ", AssetTypes.SelectMany(group => group.Types).Select(type => type.Name)) + "."),
                ["belongs_to_page"] = Text("The page of the card it belongs to (Story, Characters, Levels or Mechanics)."),
                ["belongs_to"] = Text("The card it belongs to, by name or id, or empty for none."),
                ["tags"] = Text("Its tags, by name, separated by commas, replacing the ones it has; empty for none. Use the ones list_cards Assets shows when they fit; new ones are made."),
                ["due"] = Text("A day to deliver it by, as yyyy-mm-dd, or empty for none."),
            },
            ["card"], (project, given) => UpdateAssetFor(project, given)),
        new("remove_card", "Deletes a card from a page, with its document, picture and board, as the page's Delete does. It can't be undone, so only when the person asked for it or the card is being replaced by others. A delivered asset, or a card that assets belong to, isn't deleted.",
            new JsonObject { ["page"] = Text("Story, Characters, Levels, Mechanics or Assets."), ["card"] = Text("The card's whole name, or its id.") },
            ["page", "card"], (project, given) => RemoveCardFor(project, given)),
        new("list_tasks", "The milestones, each with what it's for, when it's done and its notes, and their tasks with ids, ticks, dates and the cards they're about. milestone, status and fields keep it short.",
            new JsonObject
            {
                ["milestone"] = Text("Only this milestone, by its name or the start of it; empty for only the tasks with no milestone."),
                ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("open", "done", "all"), ["description"] = "open, done or all: only the tasks not done yet, only those done, or every one. all when not given." },
                ["fields"] = Text($"What to give for each task, separated by commas, from: {string.Join(", ", TaskFields)}. Its id is always given. All of them when not given; for a short list, such as title, done."),
                ["open_only"] = Flag("Leave out tasks that are done, as status open does."),
            }, [], (project, given) => ListTasksFor(project, given)),
        new("add_task", "Adds a task, such as one found while working.",
            new JsonObject { ["title"] = Text("What's to be done."), ["notes"] = Text("Anything more about it."), ["milestone"] = Text("Its milestone, by name or the start of it."), ["page"] = Text("The page of the card it's about."), ["card"] = Text("The card it's about, by name or id."), ["due"] = Text("A day to have it done by, as yyyy-mm-dd."), ["evidence"] = EvidenceArgument() },
            ["title"], (project, given) => AddTaskFor(project, given)),
        new("update_task", "Changes a task: ticks it done (or not), or changes what it says, its notes, date, milestone, card or place in the list, or adds evidence of its work.",
            new JsonObject { ["id"] = Text("The task's id, from list_tasks."), ["done"] = Flag("Whether it's done."), ["title"] = Text("What's to be done."), ["notes"] = Text("Its notes, replacing the old ones."), ["due"] = Text("yyyy-mm-dd, or empty for none."), ["milestone"] = Text("Its milestone, by name, or empty for none."), ["page"] = Text("The page of the card it's about."), ["card"] = Text("The card it's about, or empty for none."), ["position"] = Number("Where it goes among its milestone's tasks, 1 for first; they're listed in this order."), ["evidence"] = EvidenceArgument() },
            ["id"], (project, given) => UpdateTaskFor(project, given)),
        new("update_milestone", "Changes a milestone's notes: what's worth knowing about it as a whole, such as how its parts fit, what was tried, or what it waits on. Its name, what it's for and when it's done are the person's to change.",
            new JsonObject { ["milestone"] = Text("The milestone, by its name or the start of it."), ["notes"] = Text("Its notes, in Markdown, replacing the old ones; empty for none."), ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("replace", "append"), ["description"] = "replace or append: swap the notes for these, or add these at their end. replace when not given." } },
            ["milestone"], (project, given) => UpdateMilestoneFor(project, given)),
        new("search", "Finds words anywhere in the design: every document, card name and task, with where each is.",
            new JsonObject { ["text"] = Text("What to find. Not case-sensitive.") }, ["text"], (project, given) => Search(project, Required(given, "text"))),
        new("read_picture", "A picture to look at: one from a document, given as ![](picture:id) in its Markdown, or a card's own picture, such as its concept art, given by read_card as picture:card-id.",
            new JsonObject { ["id"] = Text("What follows picture: in the document or in read_card.") }, ["id"], (project, given) => ReadPicture(project, Required(given, "id"))),

        // The creative tools, such as Blender, Aseprite, ArmorPaint and Material Maker.
        new("list_tools", "The creative tools set up on this PC, such as Blender, Aseprite, ArmorPaint and Material Maker, and the game's engine, such as Unreal Engine, and whether each is open and its MCP answers.",
            [], [], (_, _) => "", (project, _) => ListToolsForAgentAsync(project)),
        new("open_tool", "Opens a creative tool in the project's folder, or the game's engine with its project, if it isn't open, and waits until its MCP answers. Then tool_tools and use_tool can use it.",
            new JsonObject { ["tool"] = Text("The tool's name, such as Blender or Aseprite."), ["in_front"] = Flag("Bring its window in front once it's open, as focus_tool does.") }, ["tool"], (_, _) => "", OpenToolForAgentAsync),
        new("focus_tool", "Brings an open creative tool's or engine's window in front, and says whether it now is. For timed work, such as a play test or a frame-rate check: some tools slow right down while their window is behind others.",
            new JsonObject { ["tool"] = Text("The tool's name, such as Unreal Engine or Blender.") }, ["tool"], (_, _) => "", FocusToolForAgentAsync),
        new("tool_tools", "What an open creative tool or engine can do: its own tools, with their arguments, for use_tool.",
            new JsonObject { ["tool"] = Text("The tool's name.") }, ["tool"], (_, _) => "", ToolToolsAsync),
        new("use_tool", "Uses one of an open creative tool's or engine's own tools, as tool_tools lists them, and gives back what it gave, pictures included.",
            new JsonObject { ["tool"] = Text("The tool's name, such as Blender."), ["name"] = Text("Its tool to use, from tool_tools."), ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "That tool's arguments." } },
            ["tool", "name"], (_, _) => "", UseToolAsync),

        // Waiting, instead of sleep.
        WaitForTool(),
    ]);

    private async Task<JsonObject> McpCallAsync(string projectId, string name, JsonObject given)
    {
        var tools = McpTools;
        if (tools.FirstOrDefault(tool => tool.Name == name) is not { } tool)
        {
            throw new McpException(-32602, $"Agent Orange has no tool called {name}{(Closest(name, tools.Select(each => each.Name)) is { } close ? $" (did you mean {close}?)" : "")}. Its tools: {string.Join(", ", tools.Select(each => each.Name))}.");
        }
        if (_database.ListProjects().FirstOrDefault(project => project.Id == projectId) is not { } project)
        {
            return ToolResult("This session isn't for a project Agent Orange has. Start the conversation from a project.", error: true);
        }
        try
        {
            given = CheckedArguments(tool, given);
            var result = tool.RunAsync is { } runAsync ? await runAsync(project, given) : tool.Run(project, given);
            return result is JsonObject made ? made : ToolResult(result.ToString() ?? "");
        }
        catch (McpMissingArgument missing) when (tool.Properties.ContainsKey(missing.Name))
        {
            return ToolResult(Needs(tool, missing.Name), error: true);
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

    // Said in full by the call, from the tool's own description of the argument.
    private static string Required(JsonObject given, string name) =>
        Given(given, name) is { Length: > 0 } text ? text : throw new McpMissingArgument(name);

    private static string Page(JsonObject given)
    {
        var asked = Required(given, "page");
        if (asked.Equals("Overview", StringComparison.OrdinalIgnoreCase)) return "Overview";
        return CardPages.Keys.FirstOrDefault(page => page.Equals(asked, StringComparison.OrdinalIgnoreCase)
            || CardPages[page].One.Equals(asked, StringComparison.OrdinalIgnoreCase)) ?? throw new McpException(-32602, $"page: there's no page called {asked}. The pages: Overview, {string.Join(", ", CardPages.Keys)}.");
    }

    // The most names a refusal lists; past that, it says how to find one.
    private const int ListedNames = 30;

    private BoardCard FindCard(Project project, string page, string asked, string argument = "card")
    {
        var cards = _database.ListCards(project.Id, _database.ProjectStage(project.Id), page.ToLowerInvariant());
        return cards.FirstOrDefault(card => SameId(card.Id, asked))
            ?? cards.FirstOrDefault(card => CardName(card, page).Equals(asked, StringComparison.OrdinalIgnoreCase))
            ?? cards.FirstOrDefault(card => CardName(card, page).StartsWith(asked, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(-32602, $"{argument}: {page} has no card called {asked}. " + NamesOrHowToFind(page, [.. cards.Select(card => CardName(card, page))], asked));
    }

    // A page's card names, when there are few enough to list, or the ones like what was asked and how to find the rest.
    private static string NamesOrHowToFind(string page, List<string> names, string asked)
    {
        if (names.Count == 0) return $"{page} has no cards yet.";
        if (names.Count <= ListedNames) return $"Its cards: {string.Join(", ", names)}.";
        var like = names.Where(name => name.Contains(asked, StringComparison.OrdinalIgnoreCase) || asked.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        return $"It has {names.Count} cards{(like.Count > 0 ? $", such as {string.Join(", ", like)}" : "")}. Find one with search, or list_cards page {page}.";
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
        if (task.Evidence is { Length: > 0 } evidence) said.Append($". Evidence: {EvidenceList(evidence)}");
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

        // Counts, and names only for pages short enough to read at a glance: a project of hundreds
        // of assets once made this answer 37 KB, most of it one line of asset names.
        said.AppendLine();
        said.AppendLine("## Pages");
        var overview = _database.GetDocument(project.Id, LocalDatabase.BoardKey(stage, BoardPart("Overview")));
        said.AppendLine($"- Overview: the game as a whole, {WordsIn(overview)} words. Read it with read_card page Overview.");
        foreach (var page in CardPages.Keys)
        {
            var cards = _database.ListCards(project.Id, stage, page == AssetsPage ? AssetsPart : page.ToLowerInvariant());
            var counted = page == AssetsPage && cards.Count > 0 ? $" ({AssetStatusCounts(project, cards)})" : "";
            if (cards.Count == 0) said.AppendLine($"- {page}: none yet");
            else if (cards.Count <= ListedNames) said.AppendLine($"- {page}, {cards.Count}{counted}: {string.Join(", ", cards.Select(card => CardName(card, page)))}");
            else said.AppendLine($"- {page}, {cards.Count}{counted}. Find them with search, or list_cards page {page}{(page == AssetsPage ? " (with status for one status)" : "")}.");
        }

        said.AppendLine();
        said.AppendLine("## Tasks");
        var tasks = _database.ListTasks(project.Id, stage);
        said.AppendLine($"{tasks.Count(each => each.Task.Done)} of {tasks.Count} done.");
        var milestones = _database.ListMilestones(project.Id, stage);
        foreach (var (id, milestone) in milestones.Take(ListedMilestones))
        {
            var inIt = tasks.Where(each => each.Task.Milestone == id).ToList();
            said.AppendLine($"- {milestone.Name}: {inIt.Count(each => each.Task.Done)} of {inIt.Count}");
        }
        if (milestones.Count > ListedMilestones) said.AppendLine($"- and {milestones.Count - ListedMilestones} more milestones, which list_tasks shows.");

        // The newest first, as those are the ones being worked on.
        if (ProjectFolder(project.Id) is { } inFolder && Directory.Exists(Path.Combine(inFolder, "docs")))
        {
            var documents = new DirectoryInfo(Path.Combine(inFolder, "docs")).EnumerateFiles("*.md", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc).Select(file => Path.GetRelativePath(inFolder, file.FullName).Replace('\\', '/')).ToList();
            if (documents.Count > 0)
            {
                said.AppendLine();
                said.AppendLine("## Documents in the folder");
                said.AppendLine(documents.Count <= ListedDocuments
                    ? "Read these as files. Documents link to them as doc:path."
                    : $"{documents.Count} in docs, the {ListedDocuments} changed last first. Read them as files; documents link to them as doc:path.");
                foreach (var document in documents.Take(ListedDocuments)) said.AppendLine($"- {document}");
                if (documents.Count > ListedDocuments) said.AppendLine($"- and {documents.Count - ListedDocuments} more in docs.");
            }
        }

        // Whatever the project's size: names and settings can still run long.
        var whole = said.ToString();
        if (Encoding.UTF8.GetByteCount(whole) <= LongestProjectAnswer) return whole;
        var cut = whole[..Math.Min(whole.Length, LongestProjectAnswer - 200)];
        return cut[..Math.Max(0, cut.LastIndexOf('\n'))] + "\n\n(Cut short, to stay small. search, list_cards, list_tasks and read_card give the rest.)\n";
    }

    private const int ListedMilestones = 20;
    private const int ListedDocuments = 15;
    private const int LongestProjectAnswer = 8000;

    // How many assets are at each status, such as "12 to do, 40 in progress".
    private string AssetStatusCounts(Project project, List<BoardCard> cards)
    {
        var details = _database.ListAssetDetails(project.Id);
        var delivered = _database.DeliveredAssets(project.Id);
        var statuses = cards.Select(card => AssetStatus.Of(details.GetValueOrDefault(card.Id) ?? new AssetDetails(), delivered.Contains(card.Id))).ToList();
        return string.Join(", ", AssetStatus.All.Select(status => (status.Name, Count: statuses.Count(each => each == status.Id))).Where(each => each.Count > 0)
            .Select(each => $"{each.Count} {each.Name.ToLowerInvariant()}"));
    }

    private string ListCardsFor(Project project, string page, string? status)
    {
        if (page == "Overview") return "The Overview isn't a page of cards. Read it with read_card page Overview.";
        if (status is not null && page != AssetsPage) throw new McpException(-32602, $"status is for page Assets only, and page was {page}.");
        var stage = _database.ProjectStage(project.Id);
        var cards = _database.ListCards(project.Id, stage, page.ToLowerInvariant());
        if (cards.Count == 0) return $"{page} has no cards yet.";
        var said = new StringBuilder();
        foreach (var card in cards)
        {
            var details = page == AssetsPage ? _database.GetAssetDetails(card.Id) : new AssetDetails();
            var cardStatus = page == AssetsPage ? AssetStatus.Of(details, _database.GetAssetDeliverable(card.Id) is not null) : AssetStatus.ToDo;
            if (status is not null && cardStatus != status) continue;
            said.Append($"- {CardName(card, page)} (id {Short(card.Id)}): {WordsIn(_database.GetDocument(project.Id, LocalDatabase.CardBoardKey(card)))} words");
            if (_database.GetCardImage(card.Id) is not null) said.Append(", has a picture");
            if (page == AssetsPage)
            {
                said.Append($"; {AssetStatus.NameOf(cardStatus)}, {AssetTypeName(details.Type) ?? "no type"}");
                if (AssetOwnerChoices(_database, project.Id, stage).FirstOrDefault(choice => choice.Card.Id == details.Owner) is { Card: not null } owner) said.Append($", belongs to {CardName(owner.Card, owner.Page)}");
                if (DueOf(details) is { } due) said.Append($", due {due:yyyy-MM-dd}");
                if (TagNames(project, details) is { Count: > 0 } tagged) said.Append($", tagged {string.Join(", ", tagged)}");
                if (_database.GetAssetDeliverable(card.Id) is { } given) said.Append($", its deliverable is {given.Path}");
            }
            said.AppendLine();
        }
        if (said.Length == 0) said.AppendLine($"No assets are {AssetStatus.NameOf(status!)}.");
        if (page == AssetsPage) said.AppendLine(_database.ListAssetTags(project.Id) is { Count: > 0 } all ? $"The project's tags: {string.Join(", ", all.Select(tag => tag.Name))}." : "The project has no tags yet.");
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
        if (page == "Overview") throw new McpException(-32602, $"page: the Overview isn't a page of cards, so it can't be added to. The pages of cards: {string.Join(", ", CardPages.Keys)}.");
        var name = Required(given, "name");
        var stage = _database.ProjectStage(project.Id);
        var part = page == AssetsPage ? AssetsPart : page.ToLowerInvariant();
        if (_database.ListCards(project.Id, stage, part).FirstOrDefault(card => CardName(card, page).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } there)
        {
            throw new McpException(-32602, $"name: {page} already has a card called {CardName(there, page)} (id {Short(there.Id)}). Write to it with write_card, or give another name.");
        }

        // An asset's details are checked before anything is added, so a mistake adds nothing.
        AssetDetails? details = null;
        if (page == AssetsPage)
        {
            var type = Given(given, "type") is { Length: > 0 } asked ? AssetTypeId(asked) : null;
            var owner = Given(given, "belongs_to") is { Length: > 0 } ownerName ? OwnerId(project, given, ownerName) : null;
            var due = Day(Given(given, "due"), "due") is { Length: > 0 } day ? day : null;
            var status = Given(given, "status") is { Length: > 0 } askedStatus ? StatusId(askedStatus, forAgent: true) : AssetStatus.ToDo;
            details = new AssetDetails(Type: type, Owner: owner, Tags: TagIds(project, Given(given, "tags")), Due: due, Status: status);
        }
        else if (new[] { "type", "belongs_to", "belongs_to_page", "due", "tags", "status" }.Where(each => Given(given, each) is { Length: > 0 }).ToList() is { Count: > 0 } onlyAssets)
        {
            throw new McpException(-32602, $"{string.Join(" and ", onlyAssets)} {(onlyAssets.Count == 1 ? "is" : "are")} for assets only, and page was {page}. Leave {(onlyAssets.Count == 1 ? "it" : "them")} out, or add the card to Assets.");
        }

        var added = _database.AddCard(project.Id, stage, part, name);
        if (details is not null) _database.SetAssetDetails(added.Id, details);
        if (Given(given, "markdown") is { Length: > 0 } markdown) _database.SetDocument(project.Id, LocalDatabase.CardBoardKey(added), markdown.Trim() + Environment.NewLine);
        McpChanged(project);
        return $"Added the {KindOfCard(page)} {name} (id {Short(added.Id)}) to {page}." + (details is null ? "" : $" {DescribeAsset(project, added)}");
    }

    private string RemoveCardFor(Project project, JsonObject given)
    {
        var page = Page(given);
        if (page == "Overview") throw new McpException(-32602, $"page: the Overview isn't a card, so it can't be removed. The pages of cards: {string.Join(", ", CardPages.Keys)}.");
        var asked = Required(given, "card");

        // Its whole name or id only: a deletion never guesses from the start of a name, as finding one to read does.
        var cards = _database.ListCards(project.Id, _database.ProjectStage(project.Id), page.ToLowerInvariant());
        var card = cards.FirstOrDefault(each => SameId(each.Id, asked))
            ?? cards.SingleOrDefault(each => CardName(each, page).Equals(asked, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(-32602, cards.Count(each => CardName(each, page).Equals(asked, StringComparison.OrdinalIgnoreCase)) > 1
                ? $"card: {page} has more than one card called {asked}. Give its id: list_cards shows them."
                : $"card: {page} has no card called {asked}. Give its whole name or id; a removal never guesses from the start of a name. " + NamesOrHowToFind(page, [.. cards.Select(each => CardName(each, page))], asked));
        var name = CardName(card, page);
        if (page == AssetsPage && _database.GetAssetDeliverable(card.Id) is { } delivered)
            throw new McpException(-32602, $"card: {name} is delivered as {delivered.Path}, so it isn't deleted. The person can delete it on the Assets page.");
        if (page != AssetsPage)
        {
            var owned = _database.ListCards(project.Id, _database.ProjectStage(project.Id), AssetsPage.ToLowerInvariant())
                .Where(asset => _database.GetAssetDetails(asset.Id).Owner == card.Id).Select(asset => CardName(asset, AssetsPage)).ToList();
            if (owned.Count > 0) throw new McpException(-32602, $"card: {(owned.Count == 1 ? "this asset belongs" : $"these {owned.Count} assets belong")} to {name}: {string.Join(", ", owned.Take(ListedNames))}{(owned.Count > ListedNames ? ", and more" : "")}. Change what they belong to with update_asset, or remove them, first.");
        }
        _database.DeleteCard(project.Id, card);
        ForgetCardPicture(card.Id);
        McpChanged(project);
        return $"Deleted {name} from {page}.";
    }

    private string UpdateAssetFor(Project project, JsonObject given)
    {
        var card = FindCard(project, AssetsPage, Required(given, "card"));
        var details = _database.GetAssetDetails(card.Id);

        // Everything is checked before anything is saved, so a mistake changes nothing.
        if (Given(given, "status") is { Length: > 0 } askedStatus)
        {
            var status = StatusId(askedStatus, forAgent: true);
            if (AssetStatus.Of(details, _database.GetAssetDeliverable(card.Id) is not null) == AssetStatus.Delivered && status != AssetStatus.Delivered)
                throw new McpException(-32602, $"status: {CardName(card, AssetsPage)} is delivered, which the person decided. Ask them before it's worked on again; they can change its status on the Assets page.");
            details = details with { Status = status };
        }
        if (Given(given, "type") is { Length: > 0 } asked) details = details with { Type = AssetTypeId(asked) };
        if (Given(given, "belongs_to") is { } ownerName) details = details with { Owner = ownerName.Length == 0 ? null : OwnerId(project, given, ownerName) };
        if (Day(Given(given, "due"), "due") is { } day) details = details with { Due = day.Length == 0 ? null : day };
        if (Given(given, "tags") is { } tags) details = details with { Tags = TagIds(project, tags) };
        _database.SetAssetDetails(card.Id, details);
        McpChanged(project);
        return $"Changed {CardName(card, AssetsPage)}. {DescribeAsset(project, card)}";
    }

    private static string AssetTypeId(string asked) =>
        AssetTypes.SelectMany(group => group.Types).FirstOrDefault(each => each.Id.Equals(asked, StringComparison.OrdinalIgnoreCase) || each.Name.Equals(asked, StringComparison.OrdinalIgnoreCase)).Id
            ?? throw new McpException(-32602, $"type: there's no asset type called {asked}. The types: {string.Join(", ", AssetTypes.SelectMany(group => group.Types).Select(each => each.Name))}.");

    // The card an asset belongs to, on the page belongs_to_page names.
    private string OwnerId(Project project, JsonObject given, string ownerName)
    {
        var pages = string.Join(", ", AssetOwnerPages);
        var askedPage = Given(given, "belongs_to_page") is { Length: > 0 } page
            ? page
            : throw new McpException(-32602, $"belongs_to needs belongs_to_page too: the page the card {ownerName} is on, one of {pages}.");
        var ownerPage = AssetOwnerPages.FirstOrDefault(each => each.Equals(askedPage, StringComparison.OrdinalIgnoreCase) || CardPages[each].One.Equals(askedPage, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(-32602, $"belongs_to_page: an asset can belong to a card on {pages}, not {askedPage}.");
        return FindCard(project, ownerPage, ownerName, "belongs_to").Id;
    }

    // Tags named, as the project's own, made when there's none of that name yet. All are checked
    // before any is made.
    private string[]? TagIds(Project project, string? named)
    {
        if (named is null) return null;
        var names = named.Split(',').Select(LocalDatabase.TidyTagName).Where(name => name.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var have = _database.ListAssetTags(project.Id);
        foreach (var name in names)
        {
            if (have.Any(tag => tag.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) continue;
            if (_database.AssetTagProblem(project.Id, name) is { } problem) throw new McpException(-32602, $"tags: the tag {name} can't be made: {problem}");
        }
        return [.. names.Select(name => (have.FirstOrDefault(tag => tag.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) ?? _database.AddAssetTag(project.Id, name)).Id)];
    }

    private List<string> TagNames(Project project, AssetDetails details) =>
        [.. _database.ListAssetTags(project.Id).Where(tag => details.Tags?.Contains(tag.Id) == true).Select(tag => tag.Name)];

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

    // Shown at once if it's open, unless someone's writing in it, and sent to other PCs. A call that
    // makes many changes at once shows and sends them once, at its end.
    private void McpChanged(Project project)
    {
        if (_mcpChangesHeld > 0) return;
        _ = _sync.SyncNowAsync();
        if (_openProject?.Id == project.Id) RenderProjectKeepingFocus(_openProject);
    }

    // What list_tasks can give for each task, besides its id.
    private static readonly string[] TaskFields = ["title", "done", "due", "card", "notes", "evidence", "handoff"];

    private string ListTasksFor(Project project, JsonObject given)
    {
        var stage = _database.ProjectStage(project.Id);
        var openOnly = given["open_only"] is JsonValue flag && flag.TryGetValue<bool>(out var onlyOpen) && onlyOpen;
        var status = Given(given, "status") is { Length: > 0 } asked ? asked : openOnly ? "open" : "all";
        if (openOnly && status == "done") throw new McpException(-32602, "open_only: it leaves out the tasks that are done, and status done asks for only those. Give one of them.");
        var fields = Given(given, "fields") is { Length: > 0 } named ? FieldsAsked(named) : [.. TaskFields];
        var only = Given(given, "milestone");
        var tasks = _database.ListTasks(project.Id, stage);
        var milestones = _database.ListMilestones(project.Id, stage);
        var cards = AssetOwnerChoices(_database, project.Id, stage);
        var said = new StringBuilder();
        bool Shown(ProjectTask task) => status switch { "open" => !task.Done, "done" => task.Done, _ => true };

        void Write(string id, ProjectTask task)
        {
            said.Append("- ");
            if (fields.Contains("done")) said.Append($"[{(task.Done ? "x" : " ")}] ");
            said.Append(fields.Contains("title") ? $"{task.Title} (task {Short(id)})" : $"task {Short(id)}");
            if (fields.Contains("due") && task.Due is { } due) said.Append($", by {due}");
            if (fields.Contains("card") && task.Card is { } cardId && cards.FirstOrDefault(choice => choice.Card.Id == cardId) is { Card: not null } belongs) said.Append($", about the {KindOfCard(belongs.Page)} {CardName(belongs.Card, belongs.Page)}");
            said.AppendLine();
            if (fields.Contains("notes") && task.Notes is { Length: > 0 } notes) said.AppendLine($"  Notes: {notes.Replace("\n", " ")}");
            if (fields.Contains("evidence") && task.Evidence is { Length: > 0 } evidence) said.AppendLine($"  Evidence: {EvidenceList(evidence)}");
            if (fields.Contains("handoff") && task.Handoff is { } handoff) said.AppendLine($"  {HandoffLine(handoff)}");
        }

        var onlyId = only is null ? null : MilestoneId(project, only);
        foreach (var (id, milestone) in milestones.Where(each => onlyId is null || each.Id == onlyId))
        {
            var inIt = tasks.Where(each => each.Task.Milestone == id).ToList();
            said.AppendLine($"## {milestone.Name} ({inIt.Count(each => each.Task.Done)} of {inIt.Count} done)");
            if (milestone.Goal is { } goal) said.AppendLine($"For: {goal}");
            if (milestone.DoneWhen is { } doneWhen) said.AppendLine($"Done when: {doneWhen}");
            if (fields.Contains("notes") && milestone.Notes is { Length: > 0 } milestoneNotes) said.AppendLine($"Notes: {milestoneNotes.Trim()}");
            if (fields.Contains("handoff") && milestone.Handoff is { } handoff) said.AppendLine(HandoffLine(handoff));
            foreach (var (taskId, task) in inIt.Where(each => Shown(each.Task))) Write(taskId, task);
            said.AppendLine();
        }
        if (only is null or "")
        {
            var loose = tasks.Where(each => each.Task.Milestone is null || milestones.All(milestone => milestone.Id != each.Task.Milestone)).Where(each => Shown(each.Task)).ToList();
            if (loose.Count > 0)
            {
                said.AppendLine(only is null ? "## Other tasks" : "## Tasks with no milestone");
                foreach (var (taskId, task) in loose) Write(taskId, task);
            }
        }
        return said.Length > 0 ? said.ToString() : "No tasks match.";
    }

    // The fields named, each one list_tasks has.
    private static HashSet<string> FieldsAsked(string named)
    {
        var asked = named.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(each => each.Trim().ToLowerInvariant()).ToList();
        foreach (var field in asked.Where(field => !TaskFields.Contains(field)))
        {
            throw new McpException(-32602, $"fields: there's no field called {field}{(Closest(field, TaskFields) is { } close ? $" (did you mean {close}?)" : "")}. The fields: {string.Join(", ", TaskFields)}. The id is always given.");
        }
        return [.. asked];
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
            ?? throw new McpException(-32602, milestones.Count == 0
                ? $"milestone: there's no milestone called {asked}, as the stage has none yet. Leave it out, or ask the person to add one."
                : $"milestone: there's no milestone called {asked}. They are: {string.Join("; ", milestones.Take(ListedNames).Select(each => each.Milestone.Name))}{(milestones.Count > ListedNames ? "; and more, which list_tasks shows" : "")}.");
    }

    private string? CardId(Project project, JsonObject given)
    {
        if (Given(given, "card") is not { } asked) return null;
        if (asked.Length == 0) return "";
        if (Given(given, "page") is not { Length: > 0 })
            throw new McpException(-32602, $"card needs page too: the page the card {asked} is on, one of {string.Join(", ", CardPages.Keys)}.");
        return FindCard(project, Page(given), asked).Id;
    }

    private static string? Day(string? asked, string argument)
    {
        if (asked is null) return null;
        if (asked.Length == 0) return "";
        return DateOnly.TryParseExact(asked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? asked
            : throw new McpException(-32602, $"{argument} is a day as yyyy-mm-dd, such as {DateTime.Today.AddDays(7):yyyy-MM-dd}; it was {asked}.");
    }

    private string AddTaskFor(Project project, JsonObject given)
    {
        var stage = _database.ProjectStage(project.Id);
        var milestone = MilestoneId(project, Given(given, "milestone")) is { Length: > 0 } chosen ? chosen : null;
        var order = _database.ListTasks(project.Id, stage).Where(each => each.Task.Milestone == milestone).Select(each => each.Task.Order).DefaultIfEmpty(0).Max() + 1;
        var card = CardId(project, given) is { Length: > 0 } cardId ? cardId : null;
        var due = Day(Given(given, "due"), "due") is { Length: > 0 } day ? day : null;
        var title = Required(given, "title");
        var evidence = EvidenceFiles(project, given["evidence"]) is { Count: > 0 } files ? KeepEvidence(project, files) : null;
        var id = _database.SaveTask(project.Id, null, new ProjectTask(stage, title, Given(given, "notes") is { Length: > 0 } notes ? notes : null, due, false, milestone, order, card, evidence));
        McpChanged(project);
        return $"Added: {TaskSummary(project, id, _database.ListTasks(project.Id, stage).First(each => each.Id == id).Task)}";
    }

    private string UpdateTaskFor(Project project, JsonObject given)
    {
        var id = Required(given, "id");
        var tasks = _database.ListTasks(project.Id, _database.ProjectStage(project.Id));
        if (tasks.FirstOrDefault(each => SameId(each.Id, id)) is not { Task: not null } found) throw new McpException(-32602, $"id: there's no task {id}. list_tasks gives each task's id, after the word task, such as (task 3f2a9c).");
        var task = found.Task;
        if (given["done"] is JsonValue done && done.TryGetValue<bool>(out var isDone)) task = task with { Done = isDone };
        if (Given(given, "title") is { Length: > 0 } title) task = task with { Title = title };
        if (Given(given, "notes") is { } notes) task = task with { Notes = notes.Length > 0 ? notes : null };
        if (Day(Given(given, "due"), "due") is { } due) task = task with { Due = due.Length > 0 ? due : null };
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

        // Checked with the rest before any picture is kept. An empty list takes all it had off.
        if (EvidenceFiles(project, given["evidence"]) is { } files)
        {
            if (files.Count == 0)
            {
                foreach (var picture in (task.Evidence ?? []).Select(each => each.Picture).OfType<string>()) _database.DeleteDocumentPicture(picture);
                task = task with { Evidence = null };
            }
            else
            {
                if ((task.Evidence?.Length ?? 0) + files.Count > MostEvidence)
                    throw new McpException(-32602, $"evidence: a task keeps up to {MostEvidence}, and it has {task.Evidence?.Length ?? 0}. Take them off with evidence as an empty list, then give the ones that matter.");
                task = task with { Evidence = [.. task.Evidence ?? [], .. KeepEvidence(project, files)] };
            }
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
        if (stored is null) throw new McpException(-32602, $"id: there's no picture {id}. Give what follows picture: in a document's ![](picture:...), or card- and a card's id, as read_card gives it.");
        var bytes = Convert.FromBase64String(stored);
        var type = bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png" : "image/jpeg";
        return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = stored, ["mimeType"] = type }) };
    }
}
