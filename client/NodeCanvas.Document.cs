using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// The Document node: one of the project's own files, such as a plan written in Markdown, shown by
/// its title and section headings, and opened with a double-click. For writing that's read and looked
/// things up in rather than laid out on a board. Like a deliverable, it's only the file's place in the
/// project's folder: nothing is copied, and the file travels with the repo, where agents read it too.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const string DocumentKind = "document";
    private const string DocumentFiles = "Documents|*.md;*.markdown;*.txt|All files|*.*";
    private const double DocumentNodeWidth = 340;
    private const int MostHeadingsShown = 14;
    private const long LargestOutlinedFile = 2_000_000;

    // Its file, as a path in the project's folder, is kept as the node's title.
    private string? DocumentFile(NodeData data) =>
        _database.GetProjectFolder(_projectId) is { } folder && data.Title.Length > 0 ? Path.Combine(folder, data.Title) : null;

    // Asks which of the project's files it is. Nothing is copied.
    private void AddDocumentFromFile(Point where)
    {
        if (_database.GetProjectFolder(_projectId) is not { } folder || !Directory.Exists(folder))
        {
            Dialogs.Notify("This project needs its folder on this PC first, as a document is one of the files in it.");
            return;
        }
        if (Dialogs.PickFile("Choose a document", DocumentFiles, folder) is not { } picked) return;

        var inFolder = Path.GetRelativePath(folder, picked);
        if (inFolder.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(inFolder))
        {
            Dialogs.Notify($"Choose a file in the project's folder.\n\n{Path.GetFileName(picked)} isn't in it, and nothing is copied into it.");
            return;
        }

        var node = AddNodeView(NewId(), new NodeData(_board, Math.Round(where.X - 80), Math.Round(where.Y - 27), inFolder.Replace('\\', '/'), DocumentKind, DocumentNodeWidth));
        _hint.Visibility = Visibility.Collapsed;
        SaveNode(node);
        Select(node.Id);
    }

    // Its first heading, or the file's name, and the headings under it.
    private static (string? Title, List<string> Headings) OutlineOf(string file)
    {
        var headings = new List<string>();
        string? title = null;
        try
        {
            if (new FileInfo(file).Length > LargestOutlinedFile) return (null, headings);
            foreach (var line in File.ReadLines(file))
            {
                if (title is null && line.StartsWith("# ", StringComparison.Ordinal)) title = line[2..].Trim();
                else if (line.StartsWith("## ", StringComparison.Ordinal)) headings.Add(line[3..].Trim());
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Being written, or taken away. It's read again with the board.
        }
        return (title, headings);
    }

    private string DocumentName(NodeData data) =>
        DocumentFile(data) is { } file && File.Exists(file) && OutlineOf(file).Title is { } title ? title : Path.GetFileNameWithoutExtension(data.Title);

    private void AddDocumentParts(NodeView view)
    {
        var file = DocumentFile(view.Data);
        var here = file is not null && File.Exists(file);
        var (title, headings) = here ? OutlineOf(file!) : (null, []);
        var muted = (Brush)FindResource("TextMuted");

        var rows = new StackPanel();
        rows.Children.Add(new TextBlock { Text = "Document", FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("Accent") });
        rows.Children.Add(new TextBlock
        {
            Text = title ?? Path.GetFileNameWithoutExtension(view.Data.Title),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextStrong"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6),
        });
        foreach (var heading in headings.Take(MostHeadingsShown))
        {
            rows.Children.Add(new TextBlock { Text = $"·  {heading}", FontSize = 12.5, Foreground = (Brush)FindResource("TextNormal"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        }
        if (headings.Count > MostHeadingsShown)
        {
            rows.Children.Add(new TextBlock { Text = $"and {headings.Count - MostHeadingsShown} more", FontSize = 12.5, Foreground = muted, Margin = new Thickness(0, 1, 0, 0) });
        }
        rows.Children.Add(new TextBlock
        {
            Text = here ? $"{view.Data.Title} · double-click to open" : $"{view.Data.Title} isn't on this PC yet. It comes with the project's repo.",
            FontSize = 11.5,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        view.Inside.Child = rows;
        view.Box.Width = Math.Clamp(view.Data.Width ?? DocumentNodeWidth, NarrowestText, WidestText);
        view.Box.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x21, 0x2B));
    }

    // In whatever opens the file on this PC, as it's the person's own file.
    private void OpenDocument(NodeView node)
    {
        if (DocumentFile(node.Data) is not { } file || !File.Exists(file))
        {
            Dialogs.Notify($"{node.Data.Title} isn't on this PC yet. It comes with the project's repo.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialogs.Notify($"Nothing on this PC opens {Path.GetFileName(file)}.\n\n{error.Message}");
        }
    }
}
