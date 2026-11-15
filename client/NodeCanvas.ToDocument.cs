using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace AgentOrange;

/// <summary>
/// A board written out as a document, in the order it's read: what's linked follows its links, from
/// where each chain starts; everything else goes row by row, left to right; a frame is a heading with
/// what's in it under it. Used once, when a card's or the overview's document is made from its board.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const double SameRow = 24; // Tops this close are on the same row, read left to right.

    /// <param name="keepPicture">Stores a picture for the document and gives its address.</param>
    /// <param name="leaveOutFirstPicture">For a card, whose picture already shows above its document.</param>
    internal string ToDocument(Func<byte[], string> keepPicture, bool leaveOutFirstPicture)
    {
        var frames = _nodes.Values.Where(IsFrame).ToList();

        // The smallest frame each node is in, if any.
        NodeView? FrameOf(NodeView node) => frames
            .Where(frame => !ReferenceEquals(frame, node) && new Rect(frame.Data.X, frame.Data.Y, frame.Data.Width ?? FrameWidth, frame.Data.Height ?? FrameHeight).Contains(new Point(node.Data.X, node.Data.Y)))
            .OrderBy(frame => (frame.Data.Width ?? FrameWidth) * (frame.Data.Height ?? FrameHeight))
            .FirstOrDefault();
        var frameOf = _nodes.Values.ToDictionary(node => node, FrameOf);

        var said = new StringBuilder();
        var leftOut = !leaveOutFirstPicture;
        Write(_nodes.Values.Where(node => frameOf[node] is null).ToList());
        return said.ToString().Trim() + "\n";

        void Write(List<NodeView> group)
        {
            var inGroup = group.Select(node => node.Id).ToHashSet();
            var after = new Dictionary<string, List<NodeView>>();
            var hasBefore = new HashSet<string>();
            foreach (var link in _links.Values.Where(link => inGroup.Contains(link.Data.From) && inGroup.Contains(link.Data.To)))
            {
                if (!after.TryGetValue(link.Data.From, out var next)) after[link.Data.From] = next = [];
                next.Add(_nodes[link.Data.To]);
                hasBefore.Add(link.Data.To);
            }

            var written = new HashSet<string>();
            void Follow(NodeView node)
            {
                if (!written.Add(node.Id)) return;
                Emit(node);
                foreach (var next in ReadingOrder(after.GetValueOrDefault(node.Id) ?? [])) Follow(next);
            }
            foreach (var start in ReadingOrder(group.Where(node => !hasBefore.Contains(node.Id)).ToList())) Follow(start);
            foreach (var rest in ReadingOrder(group)) Follow(rest); // Any in a loop of links.
        }

        void Emit(NodeView node)
        {
            if (IsFrame(node))
            {
                said.Append("## ").Append(node.Data.Title.Length > 0 ? node.Data.Title : "Frame").Append("\n\n");
                Write(_nodes.Values.Where(inside => ReferenceEquals(frameOf[inside], node)).ToList());
                return;
            }
            if (node.Rich is { } rich)
            {
                said.Append(MarkdownText.ToMarkdown(rich.Document, NodeLook).Trim()).Append("\n\n");
            }
            else if (node.Picture is not null)
            {
                if (!leftOut)
                {
                    leftOut = true; // The card's own picture, shown above its document already.
                    return;
                }
                if (_storedPictures.TryGetValue(node.Id, out var stored))
                {
                    try
                    {
                        said.Append("![](").Append(keepPicture(Convert.FromBase64String(stored))).Append(")\n\n");
                    }
                    catch (FormatException)
                    {
                        // Arrived from another PC, and isn't a picture.
                    }
                }
            }
            else if (node.Data.Kind == DocumentKind)
            {
                said.Append("- [").Append(DocumentName(node.Data)).Append("](doc:").Append(node.Data.Title).Append(")\n\n");
            }
            else if (ReferenceTo(node.Data.Kind) is { } to)
            {
                if (ReferencedCard(node.Data, to) is { } card)
                {
                    var page = MainWindow.CardPages.Keys.First(each => each.ToLowerInvariant() == to.Part);
                    said.Append("- [").Append(MainWindow.CardName(card, page)).Append("](card:").Append(to.Part).Append('/').Append(card.Id).Append(")\n\n");
                }
            }
            else if (node.Data.Title.Length > 0)
            {
                said.Append("## ").Append(node.Data.Title).Append("\n\n");
            }
        }
    }

    // Row by row, top to bottom, and left to right along each row.
    private static List<NodeView> ReadingOrder(List<NodeView> nodes)
    {
        var ordered = new List<NodeView>();
        var byTop = nodes.OrderBy(node => node.Data.Y).ToList();
        for (var at = 0; at < byTop.Count;)
        {
            var rowTop = byTop[at].Data.Y;
            var row = byTop.Skip(at).TakeWhile(node => node.Data.Y - rowTop <= SameRow).ToList();
            ordered.AddRange(row.OrderBy(node => node.Data.X));
            at += row.Count;
        }
        return ordered;
    }

    /// <summary>Everything on the board goes, once it's in the document. A copy is kept in the folder given first.</summary>
    internal void ClearIntoBackup(string backupFolder)
    {
        Directory.CreateDirectory(backupFolder);
        var backup = new
        {
            board = _board,
            nodes = _nodes.Values.ToDictionary(node => node.Id, node => node.Data),
            links = _links.Values.ToDictionary(link => link.Id, link => link.Data),
            pictures = _nodes.Keys.Where(_storedPictures.ContainsKey).ToDictionary(id => id, id => _storedPictures[id]),
        };
        var file = Path.Combine(backupFolder, $"{_board.Replace('/', '_')}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(backup, JsonOptions));

        foreach (var link in _links.Values.ToList()) _database.SaveRecord(LocalDatabase.NodeLinksCollection, link.Id, "", deleted: true);
        foreach (var node in _nodes.Values.ToList())
        {
            _database.SaveRecord(LocalDatabase.NodesCollection, node.Id, "", deleted: true);
            if (node.Picture is not null) ForgetPicture(node.Id);
        }
        Reload();
        _changed();
    }
}
