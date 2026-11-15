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

    public DocumentPage(LocalDatabase database, string projectId, string boardKey, string markdown, ImageSource? banner, int onBoard, Action changed, Action<Uri> openLink, Action showBoard)
    {
        _database = database;
        _projectId = projectId;
        _boardKey = boardKey;
        _changed = changed;
        _saved = markdown;

        Background = (Brush)FindResource("ContentBackground");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The side: the board, then the document's headings.
        var boardButton = Ui.TextButton(onBoard > 0 ? $"Board · {onBoard} {(onBoard == 1 ? "node" : "nodes")}" : "Board", showBoard);
        boardButton.HorizontalAlignment = HorizontalAlignment.Left;
        boardButton.ToolTip = "Its node board, for what's better laid out than written, such as workflows.";
        var side = new DockPanel { Margin = new Thickness(28, 24, 12, 24) };
        DockPanel.SetDock(boardButton, Dock.Top);
        side.Children.Add(boardButton);
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
        SetColumn(_scroller, 1);
        Children.Add(_scroller);

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

        ShowOutline();
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

    // Markdown as it's typed: a space after #, ##, ###, -, 1. or [] at the start of a line.
    private void SpaceTyped(object sender, TextCompositionEventArgs typed)
    {
        if (typed.Text != " " || !_editor.Selection.IsEmpty || _editor.CaretPosition.Paragraph is not { } paragraph) return;
        var before = new TextRange(paragraph.ContentStart, _editor.CaretPosition);
        Action? make = before.Text switch
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

    private void SetHeading(Paragraph paragraph, int level)
    {
        paragraph.FontSize = Look.Headings[level];
        paragraph.FontWeight = level > 0 ? FontWeights.Bold : FontWeights.Normal;
        _editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, Look.Headings[level]);
        _editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, paragraph.FontWeight);
    }

    // Enter at the end of a heading goes back to normal words.
    private void EnterAfterHeading(object sender, KeyEventArgs pressed)
    {
        if (pressed.Key != Key.Enter || KeyState.Modifiers != ModifierKeys.None || _editor.CaretPosition.Paragraph is not { } paragraph || Look.LevelOf(paragraph.FontSize) == 0) return;
        pressed.Handled = true;
        _editor.BeginChange();
        EditingCommands.EnterParagraphBreak.Execute(null, _editor);
        if (_editor.CaretPosition.Paragraph is { } next && !ReferenceEquals(next, paragraph)) SetHeading(next, 0);
        _editor.EndChange();
    }

    // Markdown pasted in comes in as what it means.
    private void PasteMarkdown(object sender, DataObjectPastingEventArgs pasting)
    {
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
