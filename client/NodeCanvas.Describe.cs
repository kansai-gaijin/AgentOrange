using System.Windows.Documents;

namespace AgentOrange;

/// <summary>
/// The board in words, for an agent: each node, what's selected, and the links between them, so
/// it can talk about what the person is looking at without being told.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const int LongestDescribedText = 300;

    /// <summary>The board's key, as its records name it: its stage, its part, then its card if it's one's.</summary>
    public string BoardKey => _board;

    public string Describe()
    {
        if (_nodes.Count == 0) return "The board is empty.";

        var names = new Dictionary<string, string>();
        var lines = new List<string>();
        var number = 0;
        foreach (var node in _nodes.Values.OrderBy(node => node.Data.Y).ThenBy(node => node.Data.X))
        {
            var said = DescribeNode(node);
            names[node.Id] = $"#{++number}";
            lines.Add($"#{number} {said}{(_selection.Contains(node.Id) ? " (selected)" : "")}");
        }
        foreach (var link in _links.Values)
        {
            if (names.TryGetValue(link.Data.From, out var from) && names.TryGetValue(link.Data.To, out var to)) lines.Add($"{from} is linked to {to}{(_selection.Contains(link.Id) ? " (selected)" : "")}");
        }
        return string.Join("\n", lines);
    }

    private string DescribeNode(NodeView node)
    {
        if (ReferenceTo(node.Data.Kind) is { } to)
        {
            return ReferencedCard(node.Data, to) is { } card
                ? $"{char.ToUpperInvariant(to.One[0])}{to.One[1..]} node, standing for the {to.One} card \"{card.Name}\""
                : $"{char.ToUpperInvariant(to.One[0])}{to.One[1..]} node, with no card picked yet";
        }
        return node.Data.Kind switch
        {
            TextKind => $"Rich Text node: {Shortened(node.Rich is { } rich ? new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text : "")}",
            ImageKind => "Image node",
            _ => $"Title node: {(node.Data.Title.Length > 0 ? node.Data.Title : "(untitled)")}",
        };
    }

    private static string Shortened(string text)
    {
        var oneLine = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length == 0) return "(empty)";
        return oneLine.Length > LongestDescribedText ? oneLine[..LongestDescribedText] + "…" : oneLine;
    }
}
