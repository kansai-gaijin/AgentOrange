using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// A card's document, or the overview's: its main view, as a board gets hard to read once it holds a
/// lot. Written in place, with Markdown as it's typed or pasted, and kept as Markdown so agents read
/// and write the same thing. Its headings are listed at the side to jump between, and the board it
/// had is a click away for what's better laid out, such as workflows.
/// </summary>
internal sealed class DocumentPage : Grid
{
    // Bigger than a node's, as it's read like a page.
    public static readonly MarkdownText.Look Look = new(15.5, [15.5, 28, 21, 17.5], Color.FromRgb(0x6C, 0xB6, 0xFF), EmptyLineBetween: false, BlockMargin: new Thickness(0, 0, 0, 10));

    private readonly LocalDatabase _database;
    private readonly string _projectId;
    private readonly string _boardKey;
    private readonly Action _changed;
    private readonly RichTextBox _editor;
    private readonly StackPanel _outline = new();
    private readonly DispatcherTimer _saveSoon;
    private ScrollViewer _scroller = null!;
    private string _saved;
    private bool _formatting;

    public DocumentPage(LocalDatabase database, string projectId, string boardKey, string markdown, ImageSource? banner, int onBoard, Action changed, Action<Uri> openLink, Action showBoard, bool hasBoard = true)
    {
        _database = database;
        _projectId = projectId;
        _boardKey = boardKey;
        _changed = changed;
        _saved = markdown;

        Background = (Brush)FindResource("ContentBackground");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The side: a picture to add, the board, then the document's headings.
        var addPicture = Ui.TextButton("Add picture", PickPicture);
        addPicture.HorizontalAlignment = HorizontalAlignment.Left;
        addPicture.ToolTip = "Goes in under where you're writing. Pasting a picture, or dropping picture files on the page, does it too.";
        _boardButton = Ui.TextButton(BoardLabel(onBoard), showBoard);
        _boardButton.HorizontalAlignment = HorizontalAlignment.Left;
        _boardButton.ToolTip = "Its node board, for what's better laid out than written, such as workflows.";
        if (!hasBoard) _boardButton.Visibility = Visibility.Collapsed; // A document of its own, such as the project's lessons.
        var side = new DockPanel { Margin = new Thickness(28, 24, 12, 24) };
        DockPanel.SetDock(addPicture, Dock.Top);
        side.Children.Add(addPicture);
        DockPanel.SetDock(_boardButton, Dock.Top);
        side.Children.Add(_boardButton);
        var contents = new TextBlock { Text = "CONTENTS", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(7, 22, 0, 6) };
        DockPanel.SetDock(contents, Dock.Top);
        side.Children.Add(contents);
        side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _outline });
        Children.Add(side);

        _editor = new RichTextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("TextStrong"),
            CaretBrush = (Brush)FindResource("TextStrong"),
            FontSize = Look.Text,
            AcceptsTab = true,
            IsDocumentEnabled = true, // So links can be followed, with Ctrl and a click.
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, // The page scrolls, picture and all.
            Padding = new Thickness(0, 0, 24, 80),
        };
        System.Windows.Automation.AutomationProperties.SetName(_editor, "Document");
        _editor.Document = NewDocument(markdown);
        _editor.AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler((_, asked) =>
        {
            asked.Handled = true;
            openLink(asked.Uri);
        }));

        var page = new StackPanel { Margin = new Thickness(12, 24, 0, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        if (banner is not null)
        {
            page.Children.Add(new Border { Height = 220, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 24, 22), Background = new ImageBrush(banner) { Stretch = Stretch.UniformToFill } });
        }
        page.Children.Add(_editor);
        _scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = page };

        // The buttons stay at the top while the page scrolls under them.
        var bar = Toolbar();
        var column = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        column.Children.Add(bar);
        column.Children.Add(_scroller);
        SetColumn(column, 1);
        Children.Add(column);

        _saveSoon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveSoon.Tick += (_, _) => Save();
        _editor.TextChanged += (_, _) =>
        {
            if (_formatting) return;
            _saveSoon.Stop();
            _saveSoon.Start();
        };
        _editor.LostKeyboardFocus += (_, _) => Save();
        Unloaded += (_, _) => Save();
        _editor.PreviewTextInput += SpaceTyped;
        _editor.PreviewKeyDown += EnterAfterHeading;
        DataObject.AddPastingHandler(_editor, PasteMarkdown);
        _editor.AllowDrop = true;
        _editor.PreviewDragOver += (_, drag) =>
        {
            if (PictureFiles(drag.Data).Count == 0) return;
            drag.Effects = DragDropEffects.Copy;
            drag.Handled = true;
        };
        _editor.PreviewDrop += (_, drop) =>
        {
            var files = PictureFiles(drop.Data);
            if (files.Count == 0) return;
            drop.Handled = true;
            if (_editor.GetPositionFromPoint(drop.GetPosition(_editor), snapToText: true) is { } at) _editor.CaretPosition = at;
            foreach (var file in files) AddPicture(ProjectImages.PrepareForBoard(file));
        };

        ShowOutline();
    }

    private readonly Button _boardButton;

    private static string BoardLabel(int onBoard) => onBoard > 0 ? $"Board · {onBoard} {(onBoard == 1 ? "node" : "nodes")}" : "Board";

    /// <summary>Whether someone is writing in it, which drawing the page again would interrupt.</summary>
    public bool IsEditing => _editor.IsKeyboardFocusWithin;

    /// <summary>Opened again: what's stored now, if it changed elsewhere, such as on another PC or by an agent.</summary>
    public void ShowLatest(string markdown, int onBoard)
    {
        _boardButton.Content = BoardLabel(onBoard);
        if (IsEditing || markdown == _saved) return;
        _saved = markdown;
        _formatting = true;
        _editor.Document = NewDocument(markdown);
        _formatting = false;
        ShowOutline();
    }

    // Pictures

    internal static readonly string[] PictureEndings =[".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    private static List<string> PictureFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files ? [.. files.Where(file => PictureEndings.Contains(Path.GetExtension(file).ToLowerInvariant()))] : [];

    private void PickPicture()
    {
        if (Dialogs.PickFile("Choose a picture", "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff") is { } file) AddPicture(ProjectImages.PrepareForBoard(file));
    }

    // Kept with the project's documents, and put in under the block being written in.
    private void AddPicture(byte[]? picture)
    {
        if (picture is null)
        {
            Dialogs.Notify("That couldn't be used as a picture.");
            return;
        }
        var id = _database.AddDocumentPicture(_projectId, picture);
        var block = MarkdownText.PictureBlock($"picture:{id}", "", ProjectImages.ToImage(picture), Look);
        block.Margin = Look.BlockMargin;
        if (OuterBlock(_editor.CaretPosition) is { } at) _editor.Document.Blocks.InsertAfter(at, block);
        else _editor.Document.Blocks.Add(block);
        _editor.CaretPosition = block.ElementEnd;
        Save();
    }

    // The block at the document's own level that holds a place in it, such as a whole list rather than an item in it.
    private Block? OuterBlock(TextPointer at)
    {
        DependencyObject? holder = at.Paragraph ?? at.Parent as DependencyObject;
        while (holder is TextElement element && element.Parent is not FlowDocument) holder = element.Parent;
        return holder is Block block && _editor.Document.Blocks.Contains(block) ? block : null;
    }

    /// <summary>The words as they stand, for agents and saving.</summary>
    public string Markdown => MarkdownText.ToMarkdown(_editor.Document, Look);

    private FlowDocument NewDocument(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = SystemFonts.MessageFontFamily, // As the app's own words, not a new document's serif.
            FontSize = Look.Text,
            Foreground = (Brush)FindResource("TextStrong"),
            TextAlignment = TextAlignment.Left,
            PagePadding = new Thickness(0),
            LineHeight = Look.Text * 1.55,
        };
        document.Blocks.AddRange(MarkdownText.ToBlocks(markdown, Look, PictureOf, ownLinks: true));
        return document;
    }

    // picture:id is one stored with the project's documents.
    private ImageSource? PictureOf(string address)
    {
        if (!address.StartsWith("picture:", StringComparison.Ordinal) || _database.GetDocumentPicture(address["picture:".Length..]) is not { } stored) return null;
        try
        {
            return ProjectImages.ToImage(Convert.FromBase64String(stored));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void Save()
    {
        _saveSoon.Stop();
        var now = Markdown;
        if (now == _saved) return;
        _saved = now;
        _database.SetDocument(_projectId, _boardKey, now);
        _changed();
        ShowOutline();
    }

    // Each heading, indented by its level, a click away.
    private void ShowOutline()
    {
        _outline.Children.Clear();
        var headings = _editor.Document.Blocks.OfType<Paragraph>()
            .Select(paragraph => (Paragraph: paragraph, Level: Look.LevelOf(Math.Max(paragraph.FontSize, paragraph.Inlines.OfType<Run>().Select(run => run.FontSize).DefaultIfEmpty(0).Max())), Words: new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim()))
            .Where(each => each.Level > 0 && each.Words.Length > 0)
            .ToList();
        // Indented from the document's own top level, which isn't always the first.
        var top = headings.Select(each => each.Level).DefaultIfEmpty(1).Min();
        foreach (var (paragraph, level, words) in headings)
        {
            var depth = level - top;
            var jump = Ui.TextButton(words, () =>
            {
                _editor.CaretPosition = paragraph.ContentStart;
                var at = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var inPage = _editor.TranslatePoint(new Point(0, at.Top), (UIElement)_scroller.Content);
                _scroller.ScrollToVerticalOffset(Math.Max(0, inPage.Y - 12));
                _editor.Focus();
            });
            jump.HorizontalAlignment = HorizontalAlignment.Stretch;
            jump.HorizontalContentAlignment = HorizontalAlignment.Left;
            jump.Margin = new Thickness(depth * 12, 0, 0, 0);
            jump.FontSize = depth == 0 ? 13.5 : 13;
            jump.FontWeight = depth == 0 ? FontWeights.SemiBold : FontWeights.Normal;
            jump.Foreground = (Brush)FindResource(depth == 0 ? "TextStrong" : "TextNormal");
            if (jump.Content is string text) jump.Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
            _outline.Children.Add(jump);
        }
        if (_outline.Children.Count == 0) _outline.Children.Add(new TextBlock { Text = "Headings show here.", FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(7, 0, 0, 0) });
    }

    // The button bar: headings, marks and lists, for what's selected or the line being written.
    private Border Toolbar()
    {
        var row = new WrapPanel { Margin = new Thickness(12, 14, 0, 6) };
        Button Tool(object face, string tip, Action act)
        {
            var button = new Button { Style = (Style)FindResource("HeaderButton"), Content = face, ToolTip = tip, Focusable = false, MinWidth = 30 };
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            button.Click += (_, _) =>
            {
                act();
                _editor.Focus();
            };
            row.Children.Add(button);
            return button;
        }
        void Gap() => row.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 5, 6, 5), Background = (Brush)FindResource("Line") });
        void Heading(int level)
        {
            if (_editor.CaretPosition.Paragraph is not { } paragraph) return;
            _editor.BeginChange();
            foreach (var each in ParagraphsSelected().DefaultIfEmpty(paragraph)) SetHeading(each, level);
            _editor.EndChange();
            Save();
        }

        Tool("H1", "Large heading (# at the start of a line)", () => Heading(1));
        Tool("H2", "Heading (##)", () => Heading(2));
        Tool("H3", "Small heading (###)", () => Heading(3));
        Tool("Text", "Normal text", () => Heading(0));
        Gap();
        Tool(new TextBlock { Text = "B", FontWeight = FontWeights.Bold }, "Bold (Ctrl+B, or **words**)", () => EditingCommands.ToggleBold.Execute(null, _editor));
        Tool(new TextBlock { Text = "I", FontStyle = FontStyles.Italic }, "Italic (Ctrl+I, or *words*)", () => EditingCommands.ToggleItalic.Execute(null, _editor));
        Tool(new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough }, "Strikethrough (~~words~~)", () =>
        {
            var now = _editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            var struck = now?.Any(each => each.Location == TextDecorationLocation.Strikethrough) == true;
            _editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, struck ? new TextDecorationCollection() : TextDecorations.Strikethrough);
        });
        Gap();
        Tool("•", "Bulleted list (- at the start of a line)", () => EditingCommands.ToggleBullets.Execute(null, _editor));
        Tool("1.", "Numbered list (1.)", () => EditingCommands.ToggleNumbering.Execute(null, _editor));
        Tool(MarkdownText.Unticked, "Tick box ([])", () =>
        {
            if (_editor.CaretPosition.Paragraph is not { } paragraph) return;
            var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            new TextRange(start, start).Text = MarkdownText.Unticked + " ";
        });
        return new Border { Child = row, Background = (Brush)FindResource("ContentBackground") };
    }

    private IEnumerable<Paragraph> ParagraphsSelected()
    {
        var found = new List<Paragraph>();
        for (var at = _editor.Selection.Start.Paragraph; at is not null && found.Count < 1000; at = at.ContentEnd.GetNextInsertionPosition(LogicalDirection.Forward)?.Paragraph)
        {
            if (!found.Contains(at)) found.Add(at);
            if (ReferenceEquals(at, _editor.Selection.End.Paragraph)) break;
        }
        return found;
    }

    // The Japanese IME's full-width marks and space count as the ones Markdown uses.
    private static string Narrow(string text) => text.Replace('＃', '#').Replace('－', '-').Replace('＊', '*').Replace('　', ' ').Replace('［', '[').Replace('］', ']').Replace('．', '.').Replace('１', '1');

    // Markdown as it's typed: a space after #, ##, ###, -, 1. or [] at the start of a line.
    private void SpaceTyped(object sender, TextCompositionEventArgs typed)
    {
        if (Narrow(typed.Text) != " " || !_editor.Selection.IsEmpty || _editor.CaretPosition.Paragraph is not { } paragraph) return;
        var before = new TextRange(paragraph.ContentStart, _editor.CaretPosition);
        Action? make = Narrow(before.Text) switch
        {
            "#" => () => SetHeading(paragraph, 1),
            "##" => () => SetHeading(paragraph, 2),
            "###" => () => SetHeading(paragraph, 3),
            "-" or "*" => () => EditingCommands.ToggleBullets.Execute(null, _editor),
            "1." => () => EditingCommands.ToggleNumbering.Execute(null, _editor),
            "[]" or "[ ]" => () => new TextRange(_editor.CaretPosition, _editor.CaretPosition).Text = MarkdownText.Unticked + " ",
            _ => null,
        };
        if (make is null) return;
        typed.Handled = true;
        _editor.BeginChange();
        before.Text = "";
        make();
        _editor.EndChange();
    }

    // The whole line, words already on it included, and what's typed next on it.
    private void SetHeading(Paragraph paragraph, int level)
    {
        paragraph.FontSize = Look.Headings[level];
        paragraph.FontWeight = level > 0 ? FontWeights.Bold : FontWeights.Normal;
        var all = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        all.ApplyPropertyValue(TextElement.FontSizeProperty, Look.Headings[level]);
        all.ApplyPropertyValue(TextElement.FontWeightProperty, paragraph.FontWeight);
        if (_editor.Selection.IsEmpty && _editor.CaretPosition.Paragraph == paragraph)
        {
            _editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, Look.Headings[level]);
            _editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, paragraph.FontWeight);
        }
    }

    // Enter: the line just written becomes what its Markdown says, such as "# Title" or **bold**, and
    // the line after a heading goes back to normal words.
    private void EnterAfterHeading(object sender, KeyEventArgs pressed)
    {
        if (pressed.Key != Key.Enter || KeyState.Modifiers != ModifierKeys.None || _editor.CaretPosition.Paragraph is not { } paragraph) return;
        if (paragraph.Parent is not FlowDocument || !_editor.Selection.IsEmpty) return; // List items and table cells keep the box's own Enter.

        var written = Narrow(MarkdownText.ParagraphMarkdown(paragraph));
        var hasMarkdown = System.Text.RegularExpressions.Regex.IsMatch(written, @"^#{1,3}\s|\*\*[^*]+\*\*|(?<![\w*])\*[^*\s][^*]*\*|`[^`]+`|~~[^~]+~~|\[[^\]]+\]\([^)\s]+\)");
        var level = Look.LevelOf(paragraph.FontSize);
        if (!hasMarkdown && level == 0) return;

        pressed.Handled = true;
        _formatting = true;
        _editor.BeginChange();
        try
        {
            var current = paragraph;
            var atEnd = _editor.CaretPosition.CompareTo(paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)) >= 0;
            if (hasMarkdown && atEnd && MarkdownText.ToBlocks(level > 0 ? $"{new string('#', level)} {written}" : written, Look, ownLinks: true).FirstOrDefault() is Paragraph made)
            {
                _editor.Document.Blocks.InsertAfter(paragraph, made);
                _editor.Document.Blocks.Remove(paragraph);
                current = made;
                _editor.CaretPosition = made.ContentEnd;
                level = Look.LevelOf(made.FontSize);
            }
            EditingCommands.EnterParagraphBreak.Execute(null, _editor);
            if (level > 0 && _editor.CaretPosition.Paragraph is { } next && !ReferenceEquals(next, current)) SetHeading(next, 0);
        }
        finally
        {
            _editor.EndChange();
            _formatting = false;
        }
        Save();
    }

    // Markdown pasted in comes in as what it means.
    private void PasteMarkdown(object sender, DataObjectPastingEventArgs pasting)
    {
        // Picture files copied in Explorer, or a copied picture, go in as pictures.
        if (PictureFiles(pasting.SourceDataObject) is { Count: > 0 } files)
        {
            pasting.CancelCommand();
            foreach (var file in files) AddPicture(ProjectImages.PrepareForBoard(file));
            return;
        }
        if (pasting.SourceDataObject.GetDataPresent(DataFormats.Bitmap) && Clipboard.ContainsImage() && Clipboard.GetImage() is { } copied)
        {
            pasting.CancelCommand();
            AddPicture(ProjectImages.PrepareForBoard(copied, keepSeeThrough: false));
            return;
        }
        if (pasting.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string text || !MarkdownText.LooksLikeMarkdown(text)) return;
        pasting.CancelCommand();
        var made = new FlowDocument { FontFamily = _editor.Document.FontFamily, FontSize = Look.Text, Foreground = _editor.Foreground, TextAlignment = TextAlignment.Left };
        made.Blocks.AddRange(MarkdownText.ToBlocks(text, Look, ownLinks: true));
        using var xaml = new MemoryStream();
        new TextRange(made.ContentStart, made.ContentEnd).Save(xaml, DataFormats.Xaml);
        xaml.Position = 0;
        _formatting = true;
        _editor.BeginChange();
        try
        {
            _editor.Selection.Load(xaml, DataFormats.Xaml);
            _editor.CaretPosition = _editor.Selection.End;
        }
        finally
        {
            _editor.EndChange();
            _formatting = false;
        }
        Save();
    }
}
