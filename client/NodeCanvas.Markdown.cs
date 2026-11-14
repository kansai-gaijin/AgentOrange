using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// Markdown pasted into a Rich Text node, such as a part of a design document, comes in as what it
/// means: headings, lists, tick boxes, tables and marked words, made the same way the buttons and
/// typed Markdown make them. Words that don't look like Markdown paste as they always did.
/// </summary>
internal sealed partial class NodeCanvas
{
    private static readonly Regex MarkdownLine = new(@"^(#{1,6} |\s*[-*+] |\s*\d+[.)] |\|.*\|\s*$)", RegexOptions.Multiline);
    private static readonly Regex MarkdownMarks = new(@"\*\*[^*\r\n]+\*\*|\[[^\]\r\n]+\]\([^)\s]+\)|`[^`\r\n]+`");
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    private static readonly Regex BulletLine = new(@"^\s*[-*+]\s+(.*)$");
    private static readonly Regex NumberLine = new(@"^\s*(\d+)[.)]\s+(.*)$");
    private static readonly Regex TickLine = new(@"^\s*[-*+]\s+\[([ xX])\]\s+(.*)$");
    private static readonly Regex RuleLine = new(@"^\s*([-*_])(\s*\1){2,}\s*$");
    private static readonly Regex TableDivider = new(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");
    private static readonly Regex InlineMark = new(
        @"\*\*(?<bold>[^*\r\n]+)\*\*|~~(?<strike>[^~\r\n]+)~~|`(?<code>[^`\r\n]+)`|\[(?<label>[^\]\r\n]+)\]\((?<address>[^)\s]+)\)|(?<![\w*])\*(?<italic>[^*\s][^*\r\n]*?)\*(?!\*)|(?<![\w_])_(?<italic2>[^_\r\n]+)_(?!\w)");

    private const double DocumentWidth = 560;

    private static bool LooksLikeMarkdown(string text) => MarkdownLine.IsMatch(text) || MarkdownMarks.IsMatch(text);

    // Takes the paste over when it's Markdown, and leaves anything else to the box as before.
    private void PasteMarkdown(NodeView node, RichTextBox rich, DataObjectPastingEventArgs pasting)
    {
        if (!pasting.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true)) return;
        if (pasting.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string text || !LooksLikeMarkdown(text)) return;
        pasting.CancelCommand();

        // A part of a document would be a long thin strip in a node as narrow as a new one.
        if ((node.Data.Width ?? TextWidth) <= TextWidth && (text.Length > 600 || text.Contains("\n|", StringComparison.Ordinal)))
        {
            node.Data = node.Data with { Width = DocumentWidth };
            node.Box.Width = DocumentWidth;
        }

        // In the node's own look, or it would take a new document's: black, serif, justified and spaced out.
        var made = new FlowDocument
        {
            FontFamily = rich.Document.FontFamily,
            FontSize = TextSize,
            Foreground = rich.Foreground,
            TextAlignment = TextAlignment.Left,
        };
        made.Blocks.AddRange(MarkdownBlocks(text));
        foreach (var block in made.Blocks) block.Margin = new Thickness(0);
        using var xaml = new MemoryStream();
        new TextRange(made.ContentStart, made.ContentEnd).Save(xaml, DataFormats.Xaml);
        xaml.Position = 0;

        _formatting = true;
        rich.BeginChange();
        try
        {
            rich.Selection.Load(xaml, DataFormats.Xaml);
            rich.CaretPosition = rich.Selection.End;
        }
        finally
        {
            rich.EndChange();
            _formatting = false;
        }
    }

    private static List<Block> MarkdownBlocks(string text)
    {
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // A paragraph's lines so far, which Markdown runs together, except after a line ending in two
        // spaces or a backslash, which it breaks there. Kept as one string with \n where they break.
        var words = new StringBuilder();
        List? list = null;
        var gap = false; // A blank line since the last block, kept as an empty line between them.
        var breakAfter = false;

        void Add(Block block)
        {
            if (gap && blocks.Count > 0) blocks.Add(new Paragraph());
            gap = false;
            blocks.Add(block);
        }
        void EndParagraph()
        {
            if (words.Length == 0) return;
            var paragraph = new Paragraph();
            var lines = words.ToString().Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0) paragraph.Inlines.Add(new LineBreak());
                paragraph.Inlines.AddRange(InlinesOf(lines[index]));
            }
            Add(paragraph);
            words.Clear();
        }
        void AddToList(TextMarkerStyle style, Paragraph item, int start = 1)
        {
            EndParagraph();
            if (list is null || list.MarkerStyle != style)
            {
                list = new List { MarkerStyle = style, StartIndex = Math.Max(1, start) };
                Add(list);
            }
            list.ListItems.Add(new ListItem(item));
        }

        for (var at = 0; at < lines.Length; at++)
        {
            var line = lines[at];
            if (string.IsNullOrWhiteSpace(line))
            {
                EndParagraph();
                list = null;
                gap = true;
                continue;
            }

            if (RuleLine.IsMatch(line))
            {
                EndParagraph();
                list = null;
                gap = true;
                continue;
            }

            if (HeadingLine.Match(line) is { Success: true } heading)
            {
                EndParagraph();
                list = null;
                var level = Math.Min(heading.Groups[1].Length, 3);
                var paragraph = new Paragraph { FontSize = HeadingSizes[level], FontWeight = FontWeights.Bold };
                paragraph.Inlines.AddRange(InlinesOf(heading.Groups[2].Value));
                Add(paragraph);
                continue;
            }

            if (line.TrimStart().StartsWith('|') && at + 1 < lines.Length && TableDivider.IsMatch(lines[at + 1].Trim()))
            {
                EndParagraph();
                list = null;
                var rows = new List<string> { line };
                at += 2;
                while (at < lines.Length && lines[at].TrimStart().StartsWith('|')) rows.Add(lines[at++]);
                at--;
                Add(MarkdownTable(rows));
                continue;
            }

            if (TickLine.Match(line) is { Success: true } tick)
            {
                EndParagraph();
                list = null;
                var paragraph = new Paragraph();
                paragraph.Inlines.Add(new Run((tick.Groups[1].Value == " " ? Unticked : Ticked) + " "));
                paragraph.Inlines.AddRange(InlinesOf(tick.Groups[2].Value));
                Add(paragraph);
                continue;
            }

            if (BulletLine.Match(line) is { Success: true } bullet)
            {
                var item = new Paragraph();
                item.Inlines.AddRange(InlinesOf(bullet.Groups[1].Value));
                AddToList(TextMarkerStyle.Disc, item);
                continue;
            }

            if (NumberLine.Match(line) is { Success: true } numbered)
            {
                var item = new Paragraph();
                item.Inlines.AddRange(InlinesOf(numbered.Groups[2].Value));
                AddToList(TextMarkerStyle.Decimal, item, int.Parse(numbered.Groups[1].Value));
                continue;
            }

            // A line straight after a list item carries on its words.
            if (list is not null && words.Length == 0 && list.ListItems.LastListItem?.Blocks.LastBlock is Paragraph carried)
            {
                carried.Inlines.Add(new Run(" "));
                carried.Inlines.AddRange(InlinesOf(line.Trim()));
                continue;
            }

            if (words.Length > 0) words.Append(breakAfter ? '\n' : ' ');
            breakAfter = line.EndsWith("  ", StringComparison.Ordinal) || line.TrimEnd().EndsWith('\\');
            words.Append(line.Trim().TrimEnd('\\'));
        }
        EndParagraph();
        return blocks;
    }

    private static Table MarkdownTable(List<string> rows)
    {
        static string[] Cells(string row)
        {
            var inside = row.Trim();
            if (inside.StartsWith('|')) inside = inside[1..];
            if (inside.EndsWith('|')) inside = inside[..^1];
            return inside.Split('|').Select(cell => cell.Trim()).ToArray();
        }

        var line = new SolidColorBrush(Color.FromRgb(0x3A, 0x48, 0x56));
        var table = new Table { CellSpacing = 0, BorderBrush = line, BorderThickness = new Thickness(1, 1, 0, 0) };
        var columns = rows.Max(row => Cells(row).Length);
        for (var column = 0; column < columns; column++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = new TableRow();
            var cells = Cells(rows[index]);
            for (var column = 0; column < columns; column++)
            {
                var paragraph = new Paragraph();
                paragraph.Inlines.AddRange(InlinesOf(column < cells.Length ? cells[column] : ""));
                if (index == 0) paragraph.FontWeight = FontWeights.Bold;
                row.Cells.Add(new TableCell(paragraph) { BorderBrush = line, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 3, 6, 3) });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    // Words with their marks: bold, italic, struck, code and links.
    private static List<Inline> InlinesOf(string words)
    {
        var made = new List<Inline>();
        var from = 0;
        foreach (Match mark in InlineMark.Matches(words))
        {
            if (mark.Index > from) made.Add(new Run(words[from..mark.Index]));
            from = mark.Index + mark.Length;

            if (mark.Groups["bold"].Success) made.Add(new Run(mark.Groups["bold"].Value) { FontWeight = FontWeights.Bold });
            else if (mark.Groups["strike"].Success) made.Add(new Run(mark.Groups["strike"].Value) { TextDecorations = TextDecorations.Strikethrough });
            else if (mark.Groups["code"].Success) made.Add(new Run(mark.Groups["code"].Value) { FontFamily = new FontFamily("Consolas") });
            else if (mark.Groups["italic"].Success) made.Add(new Run(mark.Groups["italic"].Value) { FontStyle = FontStyles.Italic });
            else if (mark.Groups["italic2"].Success) made.Add(new Run(mark.Groups["italic2"].Value) { FontStyle = FontStyles.Italic });
            else if (WebAddress(mark.Groups["address"].Value) is { } address)
            {
                made.Add(new Hyperlink(new Run(mark.Groups["label"].Value)) { NavigateUri = address, Foreground = new SolidColorBrush(LinkColour) });
            }
            else
            {
                made.Add(new Run(mark.Groups["label"].Value)); // Not a web address, so only its words.
            }
        }
        if (from < words.Length) made.Add(new Run(words[from..]));
        return made;
    }
}
