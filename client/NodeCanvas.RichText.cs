using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentOrange;

/// <summary>
/// What a Rich Text node's words can be made into, two ways: a bar of buttons that sits by the node
/// while it's being edited, and Markdown that turns into the real thing as it's typed.
/// </summary>
internal sealed partial class NodeCanvas
{
    // A tick box is one of these two at the start of a line, so it saves and syncs as plain words.
    private const string Unticked = "☐";
    private const string Ticked = "☑";

    // Body, then headings one to three.
    private static readonly double[] HeadingSizes = [TextSize, 26, 20, 17];
    private static readonly Color LinkColour = Color.FromRgb(0x6C, 0xB6, 0xFF);

    private static readonly (string Name, Color Colour)[] TextColours =
    [
        ("White", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("Grey", Color.FromRgb(0xA9, 0xB4, 0xBF)),
        ("Red", Color.FromRgb(0xF2, 0x77, 0x7A)),
        ("Orange", Color.FromRgb(0xF9, 0xA8, 0x5D)),
        ("Yellow", Color.FromRgb(0xFF, 0xD8, 0x66)),
        ("Green", Color.FromRgb(0x8F, 0xD6, 0x94)),
        ("Teal", Color.FromRgb(0x5B, 0xD0, 0xD6)),
        ("Purple", Color.FromRgb(0xC4, 0xA7, 0xFF)),
    ];

    // Markdown around words, which is taken away and the words inside made that way. Off is what
    // goes back on for what's typed next, or what was there before when there's none.
    private static readonly (Regex Pattern, int Marker, DependencyProperty Property, object On, object? Off)[] InlineMarks =
    [
        (new Regex(@"\*\*([^*\r\n]+)\*\*$"), 2, TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal),
        (new Regex(@"(?<![*\w])\*([^*\s][^*\r\n]*)\*$"), 1, TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal),
        (new Regex(@"(?<![_\w])_([^_\r\n]+)_$"), 1, TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal),
        (new Regex(@"~~([^~\r\n]+)~~$"), 2, Inline.TextDecorationsProperty, TextDecorations.Strikethrough, new TextDecorationCollection()),
        (new Regex(@"`([^`\r\n]+)`$"), 1, TextElement.FontFamilyProperty, new FontFamily("Consolas"), null),
    ];

    private static readonly Regex LinkMark = new(@"\[([^\]\r\n]+)\]\(([^)\s]+)\)$");

    private Border _toolbar = null!;
    private TextBox _linkBox = null!;
    private bool _formatting; // Changing the words itself, which mustn't set off another change.

    // The bar of buttons

    private Border NewToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        row.Children.Add(ToolButton(new TextBlock { Text = "B", FontWeight = FontWeights.Bold }, "Bold (Ctrl+B)", rich => EditingCommands.ToggleBold.Execute(null, rich)));
        row.Children.Add(ToolButton(new TextBlock { Text = "I", FontStyle = FontStyles.Italic }, "Italic (Ctrl+I)", rich => EditingCommands.ToggleItalic.Execute(null, rich)));
        row.Children.Add(ToolButton(new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }, "Underline (Ctrl+U)", rich => EditingCommands.ToggleUnderline.Execute(null, rich)));
        row.Children.Add(ToolButton(new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough }, "Strikethrough", ToggleStrikethrough));
        row.Children.Add(ToolDivider());
        row.Children.Add(ToolButton("H1", "Large heading", rich => SetHeading(rich, 1)));
        row.Children.Add(ToolButton("H2", "Heading", rich => SetHeading(rich, 2)));
        row.Children.Add(ToolButton("H3", "Small heading", rich => SetHeading(rich, 3)));
        row.Children.Add(ToolButton("Text", "Normal text", rich => SetHeading(rich, 0)));
        row.Children.Add(ToolDivider());
        row.Children.Add(ToolButton("•", "Bulleted list", rich => EditingCommands.ToggleBullets.Execute(null, rich)));
        row.Children.Add(ToolButton("1.", "Numbered list", rich => EditingCommands.ToggleNumbering.Execute(null, rich)));
        row.Children.Add(ToolButton(Unticked, "Tick box", ToggleTickBoxes));
        row.Children.Add(ToolDivider());
        foreach (var (name, colour) in TextColours)
        {
            var brush = new SolidColorBrush(colour);
            row.Children.Add(ToolButton(new Ellipse { Width = 12, Height = 12, Fill = brush }, name, rich => rich.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, brush)));
        }
        row.Children.Add(ToolDivider());
        row.Children.Add(ToolButton("Link", "Link (Ctrl+K)", AskForLink));

        _linkBox = new TextBox
        {
            Width = 220,
            Margin = new Thickness(4, 0, 2, 0),
            Padding = new Thickness(5, 3, 5, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("ControlBackground"),
            Foreground = (Brush)FindResource("TextStrong"),
            CaretBrush = (Brush)FindResource("TextStrong"),
            BorderBrush = (Brush)FindResource("Line"),
            Visibility = Visibility.Collapsed,
            ToolTip = "The address to link to. Enter to set it, empty to take the link away.",
        };
        System.Windows.Automation.AutomationProperties.SetName(_linkBox, "Link address");
        _linkBox.KeyDown += (_, pressed) =>
        {
            if (pressed.Key is not (Key.Enter or Key.Escape)) return;
            pressed.Handled = true;
            if (_editing?.Rich is not { } rich) return;
            if (pressed.Key == Key.Enter) SetLink(rich, _linkBox.Text.Trim());
            rich.Focus();
            _linkBox.Visibility = Visibility.Collapsed;
            PlaceToolbar();
        };
        _linkBox.LostKeyboardFocus += (_, lost) =>
        {
            if (_editing is { } node && !ReferenceEquals(lost.NewFocus, node.Rich) && !IsInToolbar(lost.NewFocus)) StopEditing(keep: true);
        };
        row.Children.Add(_linkBox);

        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x32)),
            BorderBrush = (Brush)FindResource("ModalBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = row,
        };
        // Or a click between its buttons would count as one on the board, and end the editing.
        bar.MouseLeftButtonDown += (_, press) => press.Handled = true;
        return bar;
    }

    // Can't take the keyboard, so the words being edited keep it and keep what's selected in them.
    private Button ToolButton(object face, string tip, Action<RichTextBox> act)
    {
        var button = new Button { Style = (Style)FindResource("HeaderButton"), Content = face, ToolTip = tip, Focusable = false, MinWidth = 26 };
        button.Click += (_, _) =>
        {
            if (_editing?.Rich is { } rich) act(rich);
        };
        return button;
    }

    private Border ToolDivider() => new() { Width = 1, Margin = new Thickness(4, 4, 4, 4), Background = (Brush)FindResource("Line") };

    private bool IsInToolbar(object? what)
    {
        for (var at = what as DependencyObject; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, _toolbar)) return true;
        }
        return false;
    }

    // Above the node being edited, or under it when there's no room above.
    private void PlaceToolbar()
    {
        if (_editing is not { Rich: not null } node)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            _linkBox.Visibility = Visibility.Collapsed;
            return;
        }

        _toolbar.Visibility = Visibility.Visible;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;
        var top = _view.Matrix.Transform(new Point(node.Data.X, node.Data.Y));
        var bottom = _view.Matrix.Transform(new Point(node.Data.X, node.Data.Y + node.Box.ActualHeight));
        var y = top.Y - size.Height - 14;
        if (y < 4) y = bottom.Y + 14;
        var x = Math.Clamp(top.X, 4, Math.Max(4, ActualWidth - size.Width - 4));
        _toolbar.Margin = new Thickness(x, y, 0, 0);
    }

    // What the buttons do

    private static List<Paragraph> ParagraphsIn(TextSelection selection)
    {
        var found = new List<Paragraph>();
        var last = selection.End.Paragraph;
        for (var at = selection.Start.Paragraph; at is not null && found.Count < 10_000; at = at.ContentEnd.GetNextInsertionPosition(LogicalDirection.Forward)?.Paragraph)
        {
            if (!found.Contains(at)) found.Add(at);
            if (ReferenceEquals(at, last)) break;
        }
        return found;
    }

    private static string WordsOf(Paragraph paragraph) => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;

    private static bool HasTickBox(string words) => words.StartsWith(Unticked, StringComparison.Ordinal) || words.StartsWith(Ticked, StringComparison.Ordinal);

    private static bool IsHeading(Paragraph paragraph) => paragraph.FontSize > TextSize + 0.5;

    private static void SetHeading(RichTextBox rich, int level)
    {
        var size = HeadingSizes[level];
        var weight = level > 0 ? FontWeights.Bold : FontWeights.Normal;
        rich.BeginChange();
        foreach (var paragraph in ParagraphsIn(rich.Selection))
        {
            paragraph.FontSize = size;
            paragraph.FontWeight = weight;
            var all = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
            all.ApplyPropertyValue(TextElement.FontSizeProperty, size);
            all.ApplyPropertyValue(TextElement.FontWeightProperty, weight);
        }
        if (rich.Selection.IsEmpty)
        {
            // For what's typed next, where the line has no words yet to change.
            rich.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
            rich.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, weight);
        }
        rich.EndChange();
    }

    private static void ToggleStrikethrough(RichTextBox rich)
    {
        var now = rich.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var struck = now is not null && now.Any(one => one.Location == TextDecorationLocation.Strikethrough);
        var next = new TextDecorationCollection((now ?? []).Where(one => one.Location != TextDecorationLocation.Strikethrough));
        if (!struck) next.Add(TextDecorations.Strikethrough[0]);
        rich.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
    }

    private static void ToggleTickBoxes(RichTextBox rich)
    {
        rich.BeginChange();
        foreach (var paragraph in ParagraphsIn(rich.Selection))
        {
            var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var words = WordsOf(paragraph);
            if (HasTickBox(words))
            {
                new TextRange(start, Step(start, words.Length > 1 && words[1] == ' ' ? 2 : 1, LogicalDirection.Forward)).Text = "";
            }
            else
            {
                new TextRange(start, start).Text = Unticked + " ";
            }
        }
        rich.EndChange();
    }

    private static Hyperlink? LinkAt(TextPointer? at)
    {
        for (var over = at?.Parent; over is TextElement element; over = element.Parent)
        {
            if (element is Hyperlink link) return link;
        }
        return null;
    }

    private void AskForLink(RichTextBox rich)
    {
        _linkBox.Text = LinkAt(rich.Selection.Start)?.NavigateUri?.ToString() ?? "";
        _linkBox.Visibility = Visibility.Visible;
        PlaceToolbar();
        _linkBox.Focus();
        _linkBox.SelectAll();
    }

    // Only web addresses, as the words can come from another PC and a link gets opened.
    private static Uri? WebAddress(string typed)
    {
        if (typed.Length == 0) return null;
        if (!typed.Contains("://", StringComparison.Ordinal)) typed = "https://" + typed;
        return Uri.TryCreate(typed, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps) ? address : null;
    }

    private static void SetLink(RichTextBox rich, string typed)
    {
        rich.BeginChange();
        try
        {
            if (LinkAt(rich.Selection.Start) is { } old)
            {
                if (typed.Length == 0)
                {
                    // Its words stay, as plain ones.
                    var plain = new Run(new TextRange(old.ContentStart, old.ContentEnd).Text);
                    old.SiblingInlines.InsertBefore(old, plain);
                    old.SiblingInlines.Remove(old);
                }
                else if (WebAddress(typed) is { } changed)
                {
                    old.NavigateUri = changed;
                }
                return;
            }

            if (WebAddress(typed) is not { } address) return;
            if (rich.Selection.IsEmpty) rich.Selection.Text = typed;
            MakeLink(rich.Selection.Start, rich.Selection.End, address);
        }
        finally
        {
            rich.EndChange();
        }
    }

    private static Hyperlink? MakeLink(TextPointer from, TextPointer to, Uri address)
    {
        try
        {
            return new Hyperlink(from, to) { NavigateUri = address, Foreground = new SolidColorBrush(LinkColour) };
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            return null; // Such as one that would run over two lines.
        }
    }

    // Markdown, as it's typed

    // That many letters along, past where the look of the words changes between them.
    private static TextPointer Step(TextPointer from, int letters, LogicalDirection way)
    {
        var at = from;
        while (letters > 0)
        {
            if (at.GetPointerContext(way) == TextPointerContext.Text)
            {
                var take = Math.Min(at.GetTextRunLength(way), letters);
                if (at.GetPositionAtOffset(way == LogicalDirection.Forward ? take : -take) is not { } moved) break;
                at = moved;
                letters -= take;
            }
            else if (at.GetNextContextPosition(way) is { } next)
            {
                at = next;
            }
            else
            {
                break;
            }
        }
        return at;
    }

    // A space after one of these at the start of a line makes the line that kind.
    private void SpaceTyped(RichTextBox rich, TextCompositionEventArgs typed)
    {
        if (typed.Text != " " || !rich.Selection.IsEmpty || rich.CaretPosition.Paragraph is not { } paragraph) return;

        var before = new TextRange(paragraph.ContentStart, rich.CaretPosition);
        Action? make = before.Text switch
        {
            "#" => () => SetHeading(rich, 1),
            "##" => () => SetHeading(rich, 2),
            "###" => () => SetHeading(rich, 3),
            "-" or "*" => () => EditingCommands.ToggleBullets.Execute(null, rich),
            "1." => () => EditingCommands.ToggleNumbering.Execute(null, rich),
            "[]" or "[ ]" => () => StartLineWith(rich, before, Unticked + " "),
            "[x]" or "[X]" => () => StartLineWith(rich, before, Ticked + " "),
            _ => null,
        };
        if (make is null) return;

        typed.Handled = true;
        _formatting = true;
        rich.BeginChange();
        try
        {
            before.Text = "";
            make();
        }
        finally
        {
            rich.EndChange();
            _formatting = false;
        }
    }

    private static void StartLineWith(RichTextBox rich, TextRange start, string words)
    {
        start.Text = words;
        rich.CaretPosition = start.End;
    }

    // The closing mark of a pair, or the end of a link, changes what's between them.
    private void FormatAsTyped(RichTextBox rich)
    {
        if (_formatting || _editing?.Rich != rich || !rich.Selection.IsEmpty || rich.CaretPosition.Paragraph is not { } paragraph) return;

        var typed = new TextRange(paragraph.ContentStart, rich.CaretPosition).Text;
        if (typed.Length == 0 || !"*_~`)".Contains(typed[^1])) return;

        _formatting = true;
        rich.BeginChange();
        try
        {
            var back = LogicalDirection.Backward;
            if (LinkMark.Match(typed) is { Success: true } link && WebAddress(link.Groups[2].Value) is { } address)
            {
                var whole = new TextRange(Step(rich.CaretPosition, link.Length, back), rich.CaretPosition) { Text = link.Groups[1].Value };
                if (MakeLink(whole.Start, whole.End, address) is { } made) rich.CaretPosition = made.ElementEnd;
                return;
            }

            foreach (var (pattern, marker, property, on, off) in InlineMarks)
            {
                if (pattern.Match(typed) is not { Success: true } found) continue;

                var inside = found.Groups[1].Length;
                var was = rich.Selection.GetPropertyValue(property);
                new TextRange(Step(rich.CaretPosition, marker, back), rich.CaretPosition).Text = "";
                var opening = Step(rich.CaretPosition, inside + marker, back);
                new TextRange(opening, Step(opening, marker, LogicalDirection.Forward)).Text = "";
                new TextRange(Step(rich.CaretPosition, inside, back), rich.CaretPosition).ApplyPropertyValue(property, on);

                // What's typed next goes back to how it was.
                rich.Selection.Select(rich.CaretPosition, rich.CaretPosition);
                var after = off ?? (was == DependencyProperty.UnsetValue ? null : was) ?? rich.GetValue(property);
                if (after is not null) rich.Selection.ApplyPropertyValue(property, after);
                return;
            }
        }
        finally
        {
            rich.EndChange();
            _formatting = false;
        }
    }

    // Enter after a tick box starts another, and after a heading goes back to normal words.
    private bool EnterPressed(RichTextBox rich)
    {
        if (rich.CaretPosition.Paragraph is not { } paragraph) return false;

        var words = WordsOf(paragraph);
        var ticks = HasTickBox(words);
        var heading = IsHeading(paragraph) && paragraph.Parent is not ListItem;
        if (!ticks && !heading) return false;

        _formatting = true;
        rich.BeginChange();
        try
        {
            if (ticks && words.Trim().Length == 1)
            {
                // Enter on an empty one ends the run of them.
                new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text = "";
                return true;
            }

            if (!rich.Selection.IsEmpty) rich.Selection.Text = "";
            EditingCommands.EnterParagraphBreak.Execute(null, rich);
            if (rich.CaretPosition.Paragraph is not { } next || ReferenceEquals(next, paragraph)) return true;

            if (ticks)
            {
                var start = next.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                var box = new TextRange(start, start) { Text = Unticked + " " };
                rich.CaretPosition = box.End;
            }
            else if (WordsOf(next).Length == 0)
            {
                next.FontSize = TextSize;
                next.FontWeight = FontWeights.Normal;
                rich.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, TextSize);
                rich.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
            }
            return true;
        }
        finally
        {
            rich.EndChange();
            _formatting = false;
        }
    }

    // Clicks on the words while they aren't being edited

    // A click on a tick box ticks it, and Ctrl and a click on a link opens it. True when it was one of those.
    private bool ClickedInWords(NodeView node, RichTextBox rich, MouseButtonEventArgs press)
    {
        var point = press.GetPosition(rich);

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (LinkAt(rich.GetPositionFromPoint(point, false)) is not { NavigateUri: { } address } || WebAddress(address.ToString()) is not { } web) return false;
            try
            {
                Process.Start(new ProcessStartInfo(web.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Nothing on this PC opens web addresses.
            }
            return true;
        }

        if (rich.GetPositionFromPoint(point, true)?.Paragraph is not { } paragraph) return false;
        var words = WordsOf(paragraph);
        if (!HasTickBox(words)) return false;

        var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var box = start.GetCharacterRect(LogicalDirection.Forward);
        if (point.X < box.Left - 3 || point.X > box.Left + paragraph.FontSize * 1.2 || point.Y < box.Top || point.Y > box.Bottom) return false;

        new TextRange(start, Step(start, 1, LogicalDirection.Forward)).Text = words.StartsWith(Unticked, StringComparison.Ordinal) ? Ticked : Unticked;
        node.Data = node.Data with { Text = TextOf(rich.Document) };
        SaveNode(node);
        Select(node.Id);
        return true;
    }
}
