using System.Text;

namespace AgentOrange;

/// <summary>
/// What the person is looking at in the main window, in words, given to an agent with each message
/// so "this", "here" and "the selected one" mean what the person sees.
/// </summary>
public partial class MainWindow
{
    internal string DescribeView()
    {
        var said = new StringBuilder();
        if (_openProject is not { } project)
        {
            said.AppendLine("Agent Orange's main window shows the list of projects. None is open.");
            var projects = _database.ListProjects();
            if (projects.Count > 0) said.AppendLine($"Projects: {string.Join(", ", projects.Select(each => $"\"{each.Name}\""))}.");
            return said.ToString().TrimEnd();
        }

        var stageId = _database.ProjectStage(project.Id);
        var stage = _database.ProjectStages(project.Id).FirstOrDefault(each => each.Id == stageId)?.Name ?? stageId;
        said.AppendLine($"Project: \"{project.Name}\" (id {project.Id}), made with {project.Engine}{(project.GameType.Length > 0 ? $", a {project.GameType} game" : "")}, at its {stage} stage.");
        if (_database.GetProjectFolder(project.Id) is { } folder) said.AppendLine($"Its folder on this PC: {folder}");
        var settings = _database.GetGameSettings(project.Id).Named().Where(each => each.Value.Length > 0).ToList();
        if (settings.Count > 0) said.AppendLine($"Game settings (in its project settings): {string.Join("; ", settings.Select(each => $"{each.Name}: {each.Value}"))}.");

        if (_openSection is not { } section || section.ProjectId != project.Id)
        {
            said.AppendLine("Page: the project's overview, with its progress and a card for each part of the game.");
            return said.ToString().TrimEnd();
        }

        if (OpenCardOf(project) is { } card && CardPages.TryGetValue(section.Name, out var page))
        {
            said.AppendLine($"Page: {section.Name}, on the {page.One} card \"{CardName(card, section.Name)}\" (id {card.Id}), looking at its own board.");
            if (section.Name == AssetsPage) said.AppendLine(DescribeAsset(project, card));
            var forCard = _database.ListTasks(project.Id, stageId).Where(each => each.Task.Card == card.Id).ToList();
            if (forCard.Count > 0) said.AppendLine($"Tasks about it, on the Tasks page: {string.Join("; ", forCard.Select(each => $"[{(each.Task.Done ? "x" : " ")}] {each.Task.Title}"))}.");
        }
        else if (CardPages.ContainsKey(section.Name))
        {
            var cards = _database.ListCards(project.Id, stageId, section.Name.ToLowerInvariant());
            said.AppendLine($"Page: {section.Name}, the page of its cards, in order: {(cards.Count == 0 ? "none yet" : string.Join(", ", cards.Select(each => $"\"{CardName(each, section.Name)}\"")))}.");
        }
        else
        {
            if (section.Name == TasksPage)
            {
                said.AppendLine("Page: Tasks, in the stage the project is at: tasks people made, by milestone, and assets with a date to deliver them by.");
                var stageNow = _database.ProjectStage(project.Id);
                var tasks = _database.ListTasks(project.Id, stageNow);
                foreach (var (milestoneId, milestone) in _database.ListMilestones(project.Id, stageNow))
                {
                    said.AppendLine($"Milestone \"{milestone.Name}\"{(milestone.Goal is { } goal ? $", for: {goal}" : "")}{(milestone.DoneWhen is { } doneWhen ? $"; done when: {doneWhen}" : "")}");
                    foreach (var (_, task) in tasks.Where(each => each.Task.Milestone == milestoneId)) said.AppendLine($"  [{(task.Done ? "x" : " ")}] {TaskLine(project, task)}");
                }
                foreach (var (_, task) in tasks.Where(each => each.Task.Milestone is null)) said.AppendLine($"  [{(task.Done ? "x" : " ")}] {TaskLine(project, task)}");
            }
            else
            {
                said.AppendLine($"Page: {section.Name}, a board.");
            }
        }

        // Its document is its main view, and its board is shown instead only when the person asks.
        if (OpenBoard(project) is { } board)
        {
            if (_boardShown.Contains(board.BoardKey) || _openDocument is not { IsVisible: true } document)
            {
                said.AppendLine($"On the board (board key {board.BoardKey}):\n{board.Describe()}");
            }
            else
            {
                var markdown = document.Markdown;
                const int Longest = 30_000;
                said.AppendLine($"Its document, in Markdown (board key {board.BoardKey}){(board.NodeCount > 0 ? $", with {board.NodeCount} nodes on its board too" : "")}:");
                said.AppendLine(markdown.Length > Longest ? markdown[..Longest] + "\n[The rest is cut off here.]" : markdown);
            }
        }
        return said.ToString().TrimEnd();
    }

    // A task as agents are told it: what it is, when by, and the card it's about.
    private string TaskLine(Project project, ProjectTask task)
    {
        var line = task.Title;
        if (task.Due is { } due) line += $" (by {due})";
        if (task.Card is { } cardId && AssetOwnerChoices(_database, project.Id, task.Stage).FirstOrDefault(choice => choice.Card.Id == cardId) is { Card: not null } belongs)
        {
            line += $", about the {KindOfCard(belongs.Page)} \"{CardName(belongs.Card, belongs.Page)}\"";
        }
        return line;
    }

    private string DescribeAsset(Project project, BoardCard card)
    {
        var details = _database.GetAssetDetails(card.Id);
        var status = AssetStatus.Of(details, _database.GetAssetDeliverable(card.Id) is not null);
        var parts = new List<string> { $"status {AssetStatus.NameOf(status)}", $"type {AssetTypeName(details.Type) ?? "not set"}" };
        var owner = AssetOwnerChoices(_database, project.Id, card.Board.Split('/')[0]).FirstOrDefault(choice => choice.Card.Id == details.Owner);
        if (owner.Card is not null) parts.Add($"belongs to the {CardPages[owner.Page].One} \"{CardName(owner.Card, owner.Page)}\"");
        if (DueOf(details) is { } due) parts.Add($"to be delivered by {due:yyyy-MM-dd}");
        if (TagNames(project, details) is { Count: > 0 } tagged) parts.Add($"tagged {string.Join(", ", tagged)}");
        parts.Add(_database.GetAssetDeliverable(card.Id) is { } given ? $"delivered as {given.Path}" : "not delivered yet");
        return $"The asset: {string.Join(", ", parts)}.";
    }
}
