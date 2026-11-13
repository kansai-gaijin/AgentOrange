using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// An agent's reply, as Markdown, in a document whose words can be selected and copied: paragraphs,
/// headings, lists, quotes, tables, code, and bold, italic, inline code and links within lines.
///
/// As T3 Code does, the reply is cut into blocks at blank lines outside code, and a block that hasn't
/// changed keeps what it was drawn as. While words stream in, only the last block is drawn again.
/// </summary>
internal sealed partial class AgentMarkdown
{
    public sealed record Look(Brush Text, Brush Strong, Brush Muted, Brush CodeText, Brush CodeBack, Brush InlineCodeBack, Brush Line, Brush Link, FontFamily Font, FontFamily Mono, double Size, double LineHeight);

    private readonly Look _look;
    private readonly List<(string Source, List<Block> Blocks)> _drawn = [];

    public RichTextBox View { get; }

    public AgentMarkdown(Look look)
    {
        _look = look;
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = look.Font,
            FontSize = look.Size,
            Foreground = look.Text,
            LineHeight = look.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Left,
        };
        View = new RichTextBox
        {
            Document = document,
            IsReadOnly = true,
            IsDocumentEnabled = true, // So links can be clicked.
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = look.Text,
            Cursor = System.Windows.Input.Cursors.IBeam,

            // Each block has space under it, so the last one's is taken back, and the reply ends where its words do.
            Margin = new Thickness(0, 0, 0, -12),
        };
        System.Windows.Automation.AutomationProperties.SetName(View, "The agent's reply");
    }

    public void Show(string markdown)
    {
        var chunks = Chunks(markdown.Trim());
        var same = 0;
        while (same < chunks.Count && same < _drawn.Count && _drawn[same].Source == chunks[same]) same++;

        var blocks = View.Document.Blocks;
        for (var i = _drawn.Count - 1; i >= same; i--)
        {
            foreach (var block in _drawn[i].Blocks) blocks.Remove(block);
            _drawn.RemoveAt(i);
        }
        for (var i = same; i < chunks.Count; i++)
        {
            var drawn = Render(chunks[i]);
            foreach (var block in drawn) blocks.Add(block);
            _drawn.Add((chunks[i], drawn));
        }
    }

    // Blocks are what's between blank lines, except inside a fence of code, which is one block however long.
    private static List<string> Chunks(string text)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var inFence = false;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (!inFence && current.Count > 0)
                {
                    chunks.Add(string.Join('\n', current));
                    current.Clear();
                }
                current.Add(line);
                if (inFence)
                {
                    chunks.Add(string.Join('\n', current));
                    current.Clear();
                }
                inFence = !inFence;
                continue;
            }
            if (!inFence && line.Trim().Length == 0)
            {
                if (current.Count > 0) chunks.Add(string.Join('\n', current));
                current.Clear();
                continue;
            }
            current.Add(line);
        }
        if (current.Count > 0) chunks.Add(string.Join('\n', current));
        return chunks;
    }

    private Thickness Spaced => new(0, 0, 0, 12);

    private List<Block> Render(string chunk)
    {
        var lines = chunk.Split('\n');
        if (lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal)) return [Code(lines)];
        if (lines.All(line => line.TrimStart().StartsWith('>'))) return [Quote(lines)];
        if (lines.Length >= 2 && lines.All(line => line.TrimStart().StartsWith('|')) && TableSeparator().IsMatch(lines[1])) return [Table(lines)];
        if (lines.Length == 1 && Rule().IsMatch(lines[0])) return [new BlockUIContainer(new Border { Height = 1, Background = _look.Line, Margin = new Thickness(0, 6, 0, 6) }) { Margin = Spaced }];

        var blocks = new List<Block>();
        var words = new List<string>();
        List? list = null;
        void EndParagraph()
        {
            if (words.Count == 0) return;
            blocks.Add(Paragraph(string.Join(' ', words)));
            words.Clear();
        }
        foreach (var line in lines)
        {
            if (Heading().Match(line) is { Success: true } heading)
            {
                EndParagraph();
                list = null;
                var level = heading.Groups[1].Length;
                var paragraph = Paragraph(heading.Groups[2].Value);
                paragraph.FontSize = level switch { 1 => _look.Size + 6, 2 => _look.Size + 3, _ => _look.Size + 1 };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Foreground = _look.Strong;
                paragraph.Margin = new Thickness(0, blocks.Count == 0 ? 0 : 6, 0, 8);
                blocks.Add(paragraph);
                continue;
            }
            if (ListItem().Match(line) is { Success: true } item)
            {
                EndParagraph();
                var numbered = char.IsDigit(item.Groups[2].Value[0]);
                if (list is null || (list.MarkerStyle == TextMarkerStyle.Decimal) != numbered)
                {
                    list = new List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = Spaced, Padding = new Thickness(22, 0, 0, 0) };
                    if (numbered && int.TryParse(item.Groups[2].Value.TrimEnd('.', ')'), out var start)) list.StartIndex = start;
                    blocks.Add(list);
                }
                var entry = Paragraph(item.Groups[3].Value);
                entry.Margin = new Thickness(Math.Min(item.Groups[1].Value.Length, 8) * 6, 0, 0, 3);
                list.ListItems.Add(new ListItem(entry));
                continue;
            }

            // A line that carries on the list item above it, or the paragraph.
            if (list is not null && line.StartsWith("  ", StringComparison.Ordinal) && list.ListItems.LastListItem?.Blocks.LastBlock is Paragraph last)
            {
                last.Inlines.Add(new Run(" "));
                AddInlines(last.Inlines, line.Trim());
                continue;
            }
            list = null;
            words.Add(line.Trim());
        }
        EndParagraph();
        return blocks;
    }

    private Paragraph Paragraph(string text)
    {
        var paragraph = new Paragraph { Margin = Spaced };
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    // Bold, italic, inline code and links. Anything else is words as they are.
    private void AddInlines(InlineCollection inlines, string text)
    {
        var at = 0;
        foreach (Match found in InlinePart().Matches(text))
        {
            if (found.Index > at) inlines.Add(new Run(text[at..found.Index]));
            at = found.Index + found.Length;
            var part = found.Value;
            if (found.Groups["code"].Success)
            {
                inlines.Add(new Run(found.Groups["code"].Value) { FontFamily = _look.Mono, FontSize = _look.Size - 1, Foreground = _look.CodeText, Background = _look.InlineCodeBack });
            }
            else if (found.Groups["bold"].Success)
            {
                var bold = new Bold { Foreground = _look.Strong };
                AddInlines(bold.Inlines, found.Groups["bold"].Value);
                inlines.Add(bold);
            }
            else if (found.Groups["italic"].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, found.Groups["italic"].Value);
                inlines.Add(italic);
            }
            else if (found.Groups["label"].Success && Uri.TryCreate(found.Groups["url"].Value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            {
                inlines.Add(Link(found.Groups["label"].Value, url));
            }
            else if (found.Groups["bare"].Success && Uri.TryCreate(part, UriKind.Absolute, out var bare))
            {
                inlines.Add(Link(part, bare));
            }
            else
            {
                inlines.Add(new Run(part));
            }
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    // Only web addresses open, in the browser, and only when clicked.
    private Hyperlink Link(string label, Uri url)
    {
        var link = new Hyperlink(new Run(label)) { NavigateUri = url, Foreground = _look.Link, TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = url.ToString() };
        link.RequestNavigate += (_, asked) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(asked.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppLog.Write($"Couldn't open a link: {error.Message}");
            }
        };
        return link;
    }

    private Block Code(string[] lines)
    {
        var language = lines[0].Trim().TrimStart('`').Trim();
        var body = lines.Skip(1).ToList();
        if (body.Count > 0 && body[^1].TrimStart().StartsWith("```", StringComparison.Ordinal)) body.RemoveAt(body.Count - 1);

        var code = new TextBox
        {
            Text = string.Join('\n', body),
            IsReadOnly = true,
            FontFamily = _look.Mono,
            FontSize = _look.Size - 1.5,
            Foreground = _look.CodeText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var panel = new StackPanel();
        if (language.Length > 0) panel.Children.Add(new TextBlock { Text = language, FontSize = 11, Foreground = _look.Muted, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(code);
        return new BlockUIContainer(new Border
        {
            Background = _look.CodeBack,
            BorderBrush = _look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 12),
            Child = panel,
        }) { Margin = Spaced };
    }

    private Block Quote(string[] lines)
    {
        var paragraph = Paragraph(string.Join(' ', lines.Select(line => line.TrimStart().TrimStart('>').Trim())));
        paragraph.Foreground = _look.Muted;
        paragraph.BorderBrush = _look.Line;
        paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
        paragraph.Padding = new Thickness(12, 0, 0, 0);
        return paragraph;
    }

    private Block Table(string[] lines)
    {
        static string[] Cells(string line) => [.. line.Trim().Trim('|').Split('|').Select(cell => cell.Trim())];
        var rows = lines.Where((_, at) => at != 1).Select(Cells).ToList();
        var columns = rows.Max(row => row.Length);
        var table = new Table { CellSpacing = 0, Margin = Spaced, BorderBrush = _look.Line, BorderThickness = new Thickness(1) };
        for (var i = 0; i < columns; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                AddInlines(paragraph.Inlines, c < rows[r].Length ? rows[r][c] : "");
                if (r == 0)
                {
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Foreground = _look.Strong;
                }
                row.Cells.Add(new TableCell(paragraph) { Background = r == 0 ? _look.CodeBack : null, Padding = new Thickness(10, 6, 10, 6), BorderBrush = _look.Line, BorderThickness = new Thickness(0, 0, c < columns - 1 ? 1 : 0, r < rows.Count - 1 ? 1 : 0) });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|__(?<bold>[^_]+)__|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|(?<![\w_])_(?<italic>[^_\s][^_]*?)_(?![\w_])|\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\)|(?<bare>https?://[^\s)<>]+)")]
    private static partial Regex InlinePart();
}
