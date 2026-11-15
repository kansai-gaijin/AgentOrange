using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// The overview and every card open on their document, with their board a click away. A board that
/// already holds things is written out as the document the first time, in the order it's read, and
/// emptied, with a copy of it kept in the agent files folder.
/// </summary>
public partial class MainWindow
{
    // Boards being shown instead of their documents, by board key.
    private readonly HashSet<string> _boardShown = [];

    private string BoardKeyOf(Project project, string name, BoardCard? card) =>
        card is null ? LocalDatabase.BoardKey(_database.ProjectStage(project.Id), BoardPart(name)) : LocalDatabase.CardBoardKey(card);

    private void AddDocumentOrBoard(StackPanel body, Project project, string name, BoardCard? card = null)
    {
        _database.GiveBoardsAStage(project.Id);
        var boardKey = BoardKeyOf(project, name, card);
        if (_boardShown.Contains(boardKey))
        {
            var back = TextButton("← Document", () =>
            {
                _boardShown.Remove(boardKey);
                RedrawProject(project);
            });
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.Margin = new Thickness(20, 6, 0, 6);
            body.Children.Add(back);
            AddBoard(body, project, name, card);
            return;
        }

        var board = BoardFor(project, name, card);
        board.Reload();
        var markdown = _database.GetDocument(project.Id, boardKey) ?? WriteBoardAsDocument(project, board, boardKey, card);

        // The whole page, edge to edge, under the header.
        body.MaxWidth = double.PositiveInfinity;
        body.Margin = new Thickness(0);
        var height = Math.Max(240, ProjectView.ActualHeight - HeroHeight(project));

        // The same page as before, if it's open again, so it keeps where it was scrolled to, taking
        // in what changed elsewhere, such as on another PC or by an agent.
        if (_documents.TryGetValue(boardKey, out var kept))
        {
            (kept.Parent as Panel)?.Children.Remove(kept);
            kept.ShowLatest(markdown, board.NodeCount);
            kept.Height = height;
            _openDocument = kept;
            body.Children.Add(kept);
            return;
        }

        var page = new DocumentPage(
            _database,
            project.Id,
            boardKey,
            markdown,
            card is null ? null : CardPicture(card.Id, 1200),
            board.NodeCount,
            () => _ = _sync.SyncNowAsync(),
            link => FollowLink(project, link),
            () =>
            {
                _boardShown.Add(boardKey);
                RedrawProject(project);
            })
        {
            Height = height,
        };
        _documents[boardKey] = page;
        _openDocument = page;
        body.Children.Add(page);
    }

    private DocumentPage? _openDocument;
    private readonly Dictionary<string, DocumentPage> _documents = [];

    // The first time: what's on the board, written out in reading order, and the board emptied into a backup.
    private string WriteBoardAsDocument(Project project, NodeCanvas board, string boardKey, BoardCard? card)
    {
        if (board.NodeCount == 0) return "";
        var markdown = board.ToDocument(picture => $"picture:{_database.AddDocumentPicture(project.Id, picture)}", leaveOutFirstPicture: card is not null && _database.GetCardImage(card.Id) is not null);
        _database.SetDocument(project.Id, boardKey, markdown);
        board.ClearIntoBackup(Path.Combine(Settings.Folders.AgentFolder, "board-backups"));
        _ = _sync.SyncNowAsync();
        return markdown;
    }

    // A link in a document: a card opens, a project file opens in whatever opens it, and a web address in the browser.
    private void FollowLink(Project project, Uri link)
    {
        switch (link.Scheme)
        {
            case "card":
                var parts = link.OriginalString["card:".Length..].Split('/', 2);
                if (parts.Length == 2) OpenCardBoard(project, parts[0], parts[1]);
                return;
            case "doc":
                if (ProjectFolder(project.Id) is not { } folder) return;
                var file = Path.GetFullPath(Path.Combine(folder, link.OriginalString["doc:".Length..]));
                if (!file.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return;
                Open(file);
                return;
            default:
                if (MarkdownText.WebAddress(link.OriginalString) is { } web) Open(web.AbsoluteUri);
                return;
        }

        static void Open(string what)
        {
            try
            {
                Process.Start(new ProcessStartInfo(what) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Nothing on this PC opens it.
            }
        }
    }
}
