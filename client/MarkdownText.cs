using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// Markdown to the words a rich text box shows, and back. Headings one to three, bullet and numbered
/// lists, tick boxes, tables, bold, italic, struck, code, links and pictures. What's shown and what's
/// kept are the same thing, so a document an agent writes in Markdown and one a person types read alike.
///
/// Besides web addresses, a link can go to a card, as card:part/id, or to one of the project's files,
/// as doc:path. A picture's address is whatever the caller can turn into one, such as picture:id.
/// </summary>
internal static class MarkdownText
{
    public const string Unticked = "☐";
    public const string Ticked = "☑";

    /// <summary>How the words are drawn: body and heading sizes, and how blocks are kept apart.</summary>
    public sealed record Look(double Text, double[] Headings, Color Link, bool EmptyLineBetween, Thickness BlockMargin)
    {
        public int LevelOf(double size)
        {
            for (var level = 1; level < Headings.Length; level++)
            {
                if (Math.Abs(size - Headings[level]) < 0.5) return level;
            }
            return 0;
        }
    }

    private static readonly Regex MarkdownLine = new(@"^(#{1,6} |\s*[-*+] |\s*\d+[.)] |\|.*\|\s*$|!\[)", RegexOptions.Multiline);
    private static readonly Regex MarkdownMarks = new(@"\*\*[^*\r\n]+\*\*|\[[^\]\r\n]+\]\([^)\s]+\)|`[^`\r\n]+`");
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    private static readonly Regex BulletLine = new(@"^(\s*)[-*+]\s+(.*)$");
    private static readonly Regex NumberLine = new(@"^(\s*)(\d+)[.)]\s+(.*)$");
    private static readonly Regex TickLine = new(@"^\s*[-*+]\s+\[([ xX])\]\s+(.*)$");
    private static readonly Regex RuleLine = new(@"^\s*([-*_])(\s*\1){2,}\s*$");
    private static readonly Regex TableDivider = new(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");
    private static readonly Regex PictureLine = new(@"^\s*!\[(?<caption>[^\]]*)\]\((?<address>[^)\s]+)\)\s*$");
    private static readonly Regex InlineMark = new(
        @"\*\*(?<bold>[^*\r\n]+)\*\*|~~(?<strike>[^~\r\n]+)~~|`(?<code>[^`\r\n]+)`|\[(?<label>[^\]\r\n]+)\]\((?<address>[^)\s]+)\)|(?<![\w*])\*(?<italic>[^*\s][^*\r\n]*?)\*(?!\*)|(?<![\w_])_(?<italic2>[^_\r\n]+)_(?!\w)");

    public static bool LooksLikeMarkdown(string text) => MarkdownLine.IsMatch(text) || MarkdownMarks.IsMatch(text);

    /// <summary>Only web addresses, as words can come from another PC and a link gets opened.</summary>
    public static Uri? WebAddress(string typed)
    {
        if (typed.Length == 0) return null;
        if (!typed.Contains("://", StringComparison.Ordinal)) typed = "https://" + typed;
        return Uri.TryCreate(typed, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps) ? address : null;
    }

    // A web address, or one of Agent Orange's own: a card (card:part/id) or a project file (doc:path).
    private static Uri? LinkAddress(string written, bool ownLinks)
    {
        if (ownLinks && (written.StartsWith("card:", StringComparison.Ordinal) || written.StartsWith("doc:", StringComparison.Ordinal)) && Uri.TryCreate(written, UriKind.Absolute, out var own)) return own;
        return WebAddress(written);
    }

    // Markdown to blocks

    /// <param name="picture">Turns a picture's address into the picture, or null when it isn't one.</param>
    /// <param name="ownLinks">Whether links to cards and files are kept, rather than only web ones.</param>
    public static List<Block> ToBlocks(string text, Look look, Func<string, ImageSource?>? picture = null, bool ownLinks = false)
    {
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // A paragraph's lines so far, which Markdown runs together, except after a line ending in two
        // spaces or a backslash, which it breaks there. Kept as one string with \n where they break.
        var words = new StringBuilder();
        var lists = new List<(List List, int Indent)>(); // Open lists, the outermost first.
        var gap = false; // A blank line since the last block.
        var breakAfter = false;

        void Add(Block block)
        {
            if (gap && blocks.Count > 0 && look.EmptyLineBetween) blocks.Add(new Paragraph());
            gap = false;
            block.Margin = look.BlockMargin;
            blocks.Add(block);
        }
        void EndParagraph()
        {
            if (words.Length == 0) return;
            var paragraph = new Paragraph();
            var parts = words.ToString().Split('\n');
            for (var index = 0; index < parts.Length; index++)
            {
                if (index > 0) paragraph.Inlines.Add(new LineBreak());
                paragraph.Inlines.AddRange(InlinesOf(parts[index], look, ownLinks));
            }
            Add(paragraph);
            words.Clear();
        }
        void AddToList(TextMarkerStyle style, int indent, Paragraph item, int start = 1)
        {
            EndParagraph();
            while (lists.Count > 0 && (lists[^1].Indent > indent || lists[^1].Indent == indent && lists[^1].List.MarkerStyle != style)) lists.RemoveAt(lists.Count - 1);
            if (lists.Count == 0 || lists[^1].Indent < indent)
            {
                var list = new List { MarkerStyle = style, StartIndex = Math.Max(1, start), Margin = lists.Count == 0 ? look.BlockMargin : new Thickness(0), Padding = new Thickness(22, 0, 0, 0) };
                if (lists.Count > 0 && lists[^1].List.ListItems.LastListItem is { } parent) parent.Blocks.Add(list);
                else Add(list);
                list.Margin = lists.Count == 0 ? look.BlockMargin : new Thickness(0);
                lists.Add((list, indent));
            }
            item.Margin = new Thickness(0);
            lists[^1].List.ListItems.Add(new ListItem(item));
        }
        void EndLists() => lists.Clear();

        for (var at = 0; at < lines.Length; at++)
        {
            var line = lines[at];
            if (string.IsNullOrWhiteSpace(line) || RuleLine.IsMatch(line))
            {
                EndParagraph();
                EndLists();
                gap = true;
                continue;
            }

            if (HeadingLine.Match(line) is { Success: true } heading)
            {
                EndParagraph();
                EndLists();
                var level = Math.Min(heading.Groups[1].Length, 3);
                var paragraph = new Paragraph { FontSize = look.Headings[level], FontWeight = FontWeights.Bold };
                paragraph.Inlines.AddRange(InlinesOf(heading.Groups[2].Value, look, ownLinks));
                Add(paragraph);
                continue;
            }

            if (picture is not null && PictureLine.Match(line) is { Success: true } shown)
            {
                EndParagraph();
                EndLists();
                Add(PictureBlock(shown.Groups["address"].Value, shown.Groups["caption"].Value, picture(shown.Groups["address"].Value), look));
                continue;
            }

            if (line.TrimStart().StartsWith('|') && at + 1 < lines.Length && TableDivider.IsMatch(lines[at + 1].Trim()))
            {
                EndParagraph();
                EndLists();
                var rows = new List<string> { line };
                at += 2;
                while (at < lines.Length && lines[at].TrimStart().StartsWith('|')) rows.Add(lines[at++]);
                at--;
                Add(TableOf(rows, look, ownLinks));
                continue;
            }

            if (TickLine.Match(line) is { Success: true } tick)
            {
                EndParagraph();
                EndLists();
                var paragraph = new Paragraph();
                paragraph.Inlines.Add(new Run((tick.Groups[1].Value == " " ? Unticked : Ticked) + " "));
                paragraph.Inlines.AddRange(InlinesOf(tick.Groups[2].Value, look, ownLinks));
                Add(paragraph);
                continue;
            }

            if (BulletLine.Match(line) is { Success: true } bullet)
            {
                var item = new Paragraph();
                item.Inlines.AddRange(InlinesOf(bullet.Groups[2].Value, look, ownLinks));
                AddToList(TextMarkerStyle.Disc, bullet.Groups[1].Value.Length, item);
                continue;
            }

            if (NumberLine.Match(line) is { Success: true } numbered)
            {
                var item = new Paragraph();
                item.Inlines.AddRange(InlinesOf(numbered.Groups[3].Value, look, ownLinks));
                AddToList(TextMarkerStyle.Decimal, numbered.Groups[1].Value.Length, item, int.Parse(numbered.Groups[2].Value));
                continue;
            }

            // A line straight after a list item carries on its words.
            if (lists.Count > 0 && words.Length == 0 && lists[^1].List.ListItems.LastListItem?.Blocks.FirstBlock is Paragraph carried)
            {
                carried.Inlines.Add(new Run(" "));
                carried.Inlines.AddRange(InlinesOf(line.Trim(), look, ownLinks));
                continue;
            }

            if (words.Length > 0) words.Append(breakAfter ? '\n' : ' ');
            breakAfter = line.EndsWith("  ", StringComparison.Ordinal) || line.TrimEnd().EndsWith('\\');
            words.Append(line.Trim().TrimEnd('\\'));
        }
        EndParagraph();
        return blocks;
    }

    /// <summary>A picture in the words, its address kept on it so it's written back the same.</summary>
    public static BlockUIContainer PictureBlock(string address, string caption, ImageSource? source, Look look)
    {
        var shown = new StackPanel { Tag = new PictureMark(address, caption), HorizontalAlignment = HorizontalAlignment.Left };
        if (source is not null)
        {
            var image = new Image { Source = source, Stretch = Stretch.Uniform, MaxWidth = 760, MaxHeight = 520, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            shown.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image });
        }
        else
        {
            shown.Children.Add(new TextBlock { Text = "Picture not here yet", Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 8) });
        }
        if (caption.Length > 0) shown.Children.Add(new TextBlock { Text = caption, FontSize = look.Text - 2, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
        return new BlockUIContainer(shown);
    }

    public sealed record PictureMark(string Address, string Caption);

    private static Table TableOf(List<string> rows, Look look, bool ownLinks)
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
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                paragraph.Inlines.AddRange(InlinesOf(column < cells.Length ? cells[column] : "", look, ownLinks));
                if (index == 0) paragraph.FontWeight = FontWeights.Bold;
                row.Cells.Add(new TableCell(paragraph) { BorderBrush = line, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 3, 6, 3) });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    // Words with their marks: bold, italic, struck, code and links.
    public static List<Inline> InlinesOf(string words, Look look, bool ownLinks = false)
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
            else if (LinkAddress(mark.Groups["address"].Value, ownLinks) is { } address)
            {
                made.Add(new Hyperlink(new Run(mark.Groups["label"].Value)) { NavigateUri = address, Foreground = new SolidColorBrush(look.Link) });
            }
            else
            {
                made.Add(new Run(mark.Groups["label"].Value)); // Not an address that's kept, so only its words.
            }
        }
        if (from < words.Length) made.Add(new Run(words[from..]));
        return made;
    }

    // Blocks back to Markdown

    public static string ToMarkdown(FlowDocument document, Look look)
    {
        var said = new StringBuilder();
        WriteBlocks(said, document.Blocks, look, "");
        return Regex.Replace(said.ToString(), @"\n{3,}", "\n\n").Trim() + "\n";
    }

    private static void WriteBlocks(StringBuilder said, BlockCollection blocks, Look look, string indent)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    var words = InlinesToMarkdown(paragraph.Inlines, heading: false);
                    if (words.Trim().Length == 0) continue;
                    var size = paragraph.Inlines.OfType<Run>().Select(run => run.FontSize).DefaultIfEmpty(paragraph.FontSize).Max();
                    var level = look.LevelOf(Math.Max(size, paragraph.FontSize));
                    if (level > 0)
                    {
                        said.Append('\n').Append(new string('#', level)).Append(' ').Append(InlinesToMarkdown(paragraph.Inlines, heading: true).Trim()).Append("\n\n");
                    }
                    else if (words.StartsWith(Unticked, StringComparison.Ordinal) || words.StartsWith(Ticked, StringComparison.Ordinal))
                    {
                        said.Append(indent).Append(words.StartsWith(Ticked, StringComparison.Ordinal) ? "- [x] " : "- [ ] ").Append(words[1..].TrimStart()).Append('\n');
                    }
                    else
                    {
                        said.Append(words.Trim()).Append("\n\n");
                    }
                    break;
                case List list:
                    WriteList(said, list, indent);
                    said.Append('\n');
                    break;
                case Table table:
                    foreach (var group in table.RowGroups)
                    {
                        for (var index = 0; index < group.Rows.Count; index++)
                        {
                            var cells = group.Rows[index].Cells.Select(cell => string.Join(" ", cell.Blocks.OfType<Paragraph>().Select(each => InlinesToMarkdown(each.Inlines, heading: index == 0).Trim())).Replace("|", "\\|"));
                            said.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                            if (index == 0) said.Append('|').Append(string.Concat(Enumerable.Repeat("---|", group.Rows[index].Cells.Count))).Append('\n');
                        }
                    }
                    said.Append('\n');
                    break;
                case BlockUIContainer { Child: FrameworkElement { Tag: PictureMark mark } }:
                    said.Append("![").Append(mark.Caption).Append("](").Append(mark.Address).Append(")\n\n");
                    break;
                case Section section:
                    WriteBlocks(said, section.Blocks, look, indent);
                    break;
            }
        }
    }

    // Each item's first words after its marker, and lists inside it indented under it.
    private static void WriteList(StringBuilder said, List list, string indent)
    {
        var number = list.StartIndex;
        foreach (var item in list.ListItems)
        {
            var marker = list.MarkerStyle == TextMarkerStyle.Decimal ? $"{number++}. " : "- ";
            var first = true;
            foreach (var inside in item.Blocks)
            {
                if (inside is Paragraph words && first)
                {
                    said.Append(indent).Append(marker).Append(InlinesToMarkdown(words.Inlines, heading: false).Trim()).Append('\n');
                    first = false;
                }
                else if (inside is List nested)
                {
                    WriteList(said, nested, indent + "  ");
                }
            }
        }
    }

    /// <summary>One paragraph's words as Markdown: its marks written out, and any typed as Markdown left as they are.</summary>
    public static string ParagraphMarkdown(Paragraph paragraph) => InlinesToMarkdown(paragraph.Inlines, heading: false);

    private static string InlinesToMarkdown(InlineCollection inlines, bool heading)
    {
        var said = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    said.Append(Marked(run.Text, run, heading));
                    break;
                case LineBreak:
                    said.Append("  \n");
                    break;
                case Hyperlink link:
                    // Its own words only: a range from its start can take in a list's bullet before it,
                    // which documents saved before this was found still carry.
                    var label = InlinesToMarkdown(link.Inlines, heading: true).TrimStart('•', '\t', ' ');
                    said.Append(link.NavigateUri is { } address ? $"[{label}]({address.OriginalString})" : label);
                    break;
                case Span span:
                    said.Append(InlinesToMarkdown(span.Inlines, heading));
                    break;
            }
        }
        return said.ToString();
    }

    // The run's own look, as it's drawn, inherited or not, written as Markdown's marks around its words.
    private static string Marked(string text, Run run, bool heading)
    {
        if (text.Trim().Length == 0) return text;
        var lead = text[..^text.TrimStart().Length];
        var trail = text[text.TrimEnd().Length..];
        var core = text.Trim();
        if (run.FontFamily.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase)) core = $"`{core}`";
        else
        {
            if (run.TextDecorations?.Any(each => each.Location == TextDecorationLocation.Strikethrough) == true) core = $"~~{core}~~";
            if (run.FontStyle == FontStyles.Italic) core = $"*{core}*";
            if (!heading && run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight()) core = $"**{core}**";
        }
        return lead + core + trail;
    }
}
