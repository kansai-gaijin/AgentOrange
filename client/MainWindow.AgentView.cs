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

        if (_openSection is not { } section || section.ProjectId != project.Id)
        {
            said.AppendLine("Page: the project's overview, with its progress and a card for each part of the game.");
            return said.ToString().TrimEnd();
        }

        if (OpenCardOf(project) is { } card && CardPages.TryGetValue(section.Name, out var page))
        {
            said.AppendLine($"Page: {section.Name}, on the {page.One} card \"{CardName(card, section.Name)}\" (id {card.Id}), looking at its own board.");
            if (section.Name == AssetsPage) said.AppendLine(DescribeAsset(project, card));
        }
        else if (CardPages.ContainsKey(section.Name))
        {
            var cards = _database.ListCards(project.Id, stageId, section.Name.ToLowerInvariant());
            said.AppendLine($"Page: {section.Name}, the page of its cards, in order: {(cards.Count == 0 ? "none yet" : string.Join(", ", cards.Select(each => $"\"{CardName(each, section.Name)}\"")))}.");
        }
        else
        {
            said.AppendLine(section.Name == TasksPage ? "Page: Tasks, the assets with a date to deliver them by." : $"Page: {section.Name}, a board.");
        }

        if (OpenBoard(project) is { } board) said.AppendLine($"On the board (board key {board.BoardKey}):\n{board.Describe()}");
        return said.ToString().TrimEnd();
    }

    private string DescribeAsset(Project project, BoardCard card)
    {
        var details = _database.GetAssetDetails(card.Id);
        var parts = new List<string> { $"type {AssetTypeName(details.Type) ?? "not set"}" };
        var owner = AssetOwnerChoices(_database, project.Id, card.Board.Split('/')[0]).FirstOrDefault(choice => choice.Card.Id == details.Owner);
        if (owner.Card is not null) parts.Add($"belongs to the {CardPages[owner.Page].One} \"{CardName(owner.Card, owner.Page)}\"");
        if (DueOf(details) is { } due) parts.Add($"to be delivered by {due:yyyy-MM-dd}");
        parts.Add(_database.GetAssetDeliverable(card.Id) is { } given ? $"delivered as {given.Path}" : "not delivered yet");
        return $"The asset: {string.Join(", ", parts)}.";
    }
}
