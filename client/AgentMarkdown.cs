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
    /// <summary>Its colours and type. Line edges code blocks; Rule is the quieter line under table rows.</summary>
    public sealed record Look(Brush Text, Brush Strong, Brush Muted, Brush CodeText, Brush CodeBack, Brush InlineCodeBack, Brush Line, Brush Link, FontFamily Font, FontFamily Mono, double Size, double LineHeight)
    {
        public Brush? Rule { get; init; }
        public Brush? Hover { get; init; }
    }

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
            Margin = new Thickness(0, 0, 0, -Gap),
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

    // T3 Code's: 0.65rem between blocks.
    private const double Gap = 10.4;

    private static Thickness Spaced => new(0, 0, 0, Gap);

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
                paragraph.FontSize = level switch { 1 => 20, 2 => 18, 3 => 16, _ => 14 };
                paragraph.LineHeight = paragraph.FontSize * 1.3;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Foreground = level == 6 ? _look.Muted : _look.Strong;
                paragraph.Margin = new Thickness(0, blocks.Count == 0 && _drawn.Count == 0 ? 0 : 20 - Gap, 0, 8);
                blocks.Add(paragraph);
                continue;
            }
            if (ListItem().Match(line) is { Success: true } item)
            {
                EndParagraph();
                var numbered = char.IsDigit(item.Groups[2].Value[0]);
                if (list is null || (list.MarkerStyle == TextMarkerStyle.Decimal) != numbered)
                {
                    list = new List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = Spaced, Padding = new Thickness(20, 0, 0, 0), MarkerOffset = 6 };
                    if (numbered && int.TryParse(item.Groups[2].Value.TrimEnd('.', ')'), out var start)) list.StartIndex = start;
                    blocks.Add(list);
                }
                var entry = Paragraph(item.Groups[3].Value);
                entry.Margin = new Thickness(Math.Min(item.Groups[1].Value.Length, 8) * 6, list.ListItems.Count == 0 ? 0 : 4, 0, 0);
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
                inlines.Add(new Run($"\u2009{found.Groups["code"].Value}\u2009") { FontFamily = _look.Mono, FontSize = Math.Round(_look.Size * 0.85, 1), Foreground = _look.Strong, Background = _look.InlineCodeBack });
            }
            else if (found.Groups["bold"].Success)
            {
                var bold = new Bold { Foreground = _look.Strong, FontWeight = FontWeights.Bold };
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
        var source = string.Join('\n', body);

        var code = new TextBox
        {
            Text = source,
            IsReadOnly = true,
            FontFamily = _look.Mono,
            FontSize = 13,
            Foreground = _look.CodeText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14.4, 6, 14.4, 12.8),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        TextBlock.SetLineHeight(code, 13 * 1.375);

        var copy = new Button
        {
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Copy",
            Content = AgentIcons.Line("copy", 12, _look.Muted),
            Template = FlatButton(_look.Hover),
        };
        System.Windows.Automation.AutomationProperties.SetName(copy, "Copy code");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(source);
                copy.Content = AgentIcons.Line("check", 12, _look.Muted);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program has the clipboard.
            }
        };
        var header = new DockPanel { Margin = new Thickness(12, 6, 6, 0) };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(new TextBlock { Text = language, FontFamily = _look.Mono, FontSize = 11, Foreground = _look.CodeText, Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center });
        return new BlockUIContainer(new Border
        {
            Background = _look.CodeBack,
            BorderBrush = _look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel { Children = { header, code } },
        }) { Margin = Spaced };
    }

    // A button that's only its content, with a rounded wash on hover.
    internal static ControlTemplate FlatButton(Brush? hover)
    {
        var face = new FrameworkElementFactory(typeof(Border), "Face");
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = face };
        if (hover is not null)
        {
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Face"));
            template.Triggers.Add(over);
        }
        return template;
    }

    private Block Quote(string[] lines)
    {
        var paragraph = Paragraph(string.Join(' ', lines.Select(line => line.TrimStart().TrimStart('>').Trim())));
        paragraph.Foreground = _look.Muted;
        paragraph.BorderBrush = _look.Rule ?? _look.Line;
        paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
        paragraph.Padding = new Thickness(12.8, 0, 0, 0);
        return paragraph;
    }

    private Block Table(string[] lines)
    {
        static string[] Cells(string line) => [.. line.Trim().Trim('|').Split('|').Select(cell => cell.Trim())];
        var rows = lines.Where((_, at) => at != 1).Select(Cells).ToList();
        var columns = rows.Max(row => row.Length);
        var table = new Table { CellSpacing = 0, Margin = Spaced };
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
                var across = r == 0 ? 8.8 : 7.2;
                row.Cells.Add(new TableCell(paragraph)
                {
                    Padding = new Thickness(c == 0 ? 0 : 12, across, c == columns - 1 ? 0 : 12, across),
                    BorderBrush = _look.Rule ?? _look.Line,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                });
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
