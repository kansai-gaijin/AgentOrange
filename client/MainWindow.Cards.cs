using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// The pages that are a grid of cards: one card per character, level or mechanic, dragged into
/// any order, each with a picture and a board of nodes of its own. Each stage has its own cards.
/// A page is read away from the window and only the rows of cards in view are built, so one with
/// hundreds of cards stays quick to change.
/// </summary>
public partial class MainWindow
{
    // What one card is called on each page, and the shape of its picture: people stand tall, places lie wide.
    internal static readonly Dictionary<string, (string One, double Width, double Height)> CardPages = new()
    {
        // A story is its beats, in the order they're dragged into.
        ["Story"] = ("beat", 264, 164),
        ["Characters"] = ("character", 184, 244),
        ["Levels"] = ("level", 264, 164),
        ["Mechanics"] = ("mechanic", 264, 164),
        [AssetsPage] = ("asset", 200, 200),
    };
    private const string PictureFiles = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff";
    private const double CardDragStartsAfter = 6;
    private const double CardPageWidth = 1240;

    // Which card's own board is open, on which page of which project.
    private (string ProjectId, string Section, string CardId)? _openCard;

    // Each card's picture is opened once at each size, and again only when it changes.
    private readonly Dictionary<(string CardId, int Width), (long Version, ImageSource? Picture)> _cardPictures = [];

    // A card being dragged to a new place: which, on which page, and the order it's making.
    private string? _draggedBoardCard;
    private CardPageView? _dragView;
    private List<BoardCard> _dragOrder = [];
    private (string CardId, Point At)? _pressedBoardCard;
    private bool _cardOrderChanged;
    private DateTimeOffset _cardDragEnded;

    /// <summary>The card whose board is open, when it's on the page and in the stage being shown.</summary>
    private BoardCard? OpenCardOf(Project project)
    {
        if (_openSection is not { } section || section.ProjectId != project.Id) return null;
        if (_openCard is not { } open || open.ProjectId != project.Id || open.Section != section.Name) return null;
        return _database.ListCards(project.Id, _database.ProjectStage(project.Id), section.Name.ToLowerInvariant()).FirstOrDefault(card => card.Id == open.CardId);
    }

    private void RedrawProject(Project project)
    {
        _ = _sync.SyncNowAsync();
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
    }

    /// <summary>What a page of cards shows, read at once away from the window.</summary>
    private sealed record CardPageData(
        List<BoardCard> Cards,
        List<BoardCard> Shown,
        bool Filtering,
        Dictionary<string, string> Sizes,
        Dictionary<string, AssetLook> Assets,
        Dictionary<string, AssetDetails> Details,
        List<AssetTag> Tags,
        Dictionary<string, long> PictureVersions,
        Dictionary<string, ImageSource?> NewPictures);

    /// <summary>A page of cards as last read, and the list showing it, kept so it keeps its place.</summary>
    private sealed class CardPageView(string projectId, string page, LazyList list)
    {
        public string ProjectId { get; } = projectId;

        public string Page { get; } = page;

        public LazyList List { get; } = list;

        public StackPanel Heading { get; } = new() { MaxWidth = CardPageWidth, Margin = new Thickness(40, 22, 40, 0) };

        public CardPageData? Data { get; set; }

        public int PerRow { get; set; }
    }

    private readonly Dictionary<string, CardPageView> _cardPageViews = [];

    private CardPageView CardPageViewFor(Project project, string stage, string name)
    {
        var key = $"{project.Id}|{stage}|{name}";
        if (_cardPageViews.TryGetValue(key, out var kept)) return kept;

        var view = new CardPageView(project.Id, name, new LazyList());
        view.List.SizeChanged += (_, _) =>
        {
            if (view.Data is not null && PerRowFor(view) != view.PerRow && _openProject?.Id == view.ProjectId) ShowCards(_openProject, view);
        };
        WatchCardDrags(view);
        return _cardPageViews[key] = view;
    }

    private void AddCardPage(StackPanel body, Project project, string name)
    {
        var stage = _database.ProjectStage(project.Id);
        var view = CardPageViewFor(project, stage, name);

        // Edge to edge, so the list scrolls at the window's edge, with its cards in the middle.
        body.MaxWidth = double.PositiveInfinity;
        body.Margin = new Thickness(0);
        (view.Heading.Parent as Panel)?.Children.Remove(view.Heading);
        body.Children.Add(view.Heading);
        AddFittedList(body, view.List);

        // What was read last time shows straight away, and what's read now replaces it.
        FillCardHeading(project, view);
        if (view.Data is not null) ShowCards(project, view);

        var filter = name == AssetsPage ? AssetFilterOf(project.Id) : new AssetFilter(null, [], null);
        var width = PictureWidth(name);
        var known = _cardPictures.Where(each => each.Key.Width == width).ToDictionary(each => each.Key.CardId, each => each.Value.Version);
        LoadPage($"cards|{project.Id}|{stage}|{name}", () => ReadCardPage(project.Id, stage, name, filter, known, width), data =>
        {
            foreach (var (cardId, picture) in data.NewPictures) _cardPictures[(cardId, width)] = (data.PictureVersions[cardId], picture);
            foreach (var gone in _cardPictures.Keys.Where(each => each.Width == width && data.Cards.Any(card => card.Id == each.CardId) && !data.PictureVersions.ContainsKey(each.CardId)).ToList()) _cardPictures.Remove(gone);
            view.Data = data;
            if (_openProject?.Id != project.Id) return;
            FillCardHeading(project, view);
            ShowCards(project, view);
            if (_filterToFocus is { } focusTag)
            {
                _filterToFocus = null;
                _ = Dispatcher.InvokeAsync(() => FindTagged(view.Heading, focusTag)?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        });
    }

    private static int PictureWidth(string page) => (int)(CardPages[page].Width * 2);

    // Away from the window: the page's cards, what each says, its filters, and any picture that changed.
    private CardPageData ReadCardPage(string projectId, string stage, string name, AssetFilter filter, Dictionary<string, long> knownPictures, int pictureWidth)
    {
        var stageCards = _database.ListStageCards(projectId, stage);
        var cards = stageCards.GetValueOrDefault(name.ToLowerInvariant()) ?? [];

        var versions = _database.CardImageVersions(projectId);
        var pictures = new Dictionary<string, ImageSource?>();
        foreach (var card in cards)
        {
            if (versions.TryGetValue(card.Id, out var version) && knownPictures.GetValueOrDefault(card.Id) != version) pictures[card.Id] = OpenCardPicture(_database.GetCardImage(card.Id), pictureWidth);
        }

        var sizes = new Dictionary<string, string>();
        var looks = new Dictionary<string, AssetLook>();
        var details = new Dictionary<string, AssetDetails>();
        var tags = new List<AssetTag>();
        if (name == AssetsPage)
        {
            details = _database.ListAssetDetails(projectId);
            var delivered = _database.DeliveredAssets(projectId);
            var owners = OwnerIndex(stageCards);
            tags = _database.ListAssetTags(projectId);
            foreach (var card in cards) looks[card.Id] = LookOf(details.GetValueOrDefault(card.Id) ?? new AssetDetails(), delivered.Contains(card.Id), owners, tags);
        }
        else
        {
            var nodes = _database.CountNodesByBoard(projectId);
            var documents = _database.ListDocuments(projectId).ToDictionary(each => each.BoardKey, each => each.Markdown);
            foreach (var card in cards)
            {
                var boardKey = LocalDatabase.CardBoardKey(card);
                sizes[card.Id] = CardSize(documents.GetValueOrDefault(boardKey), nodes.GetValueOrDefault(boardKey));
            }
        }

        // Assets can be filtered, and while they are they can't be dragged into order, as most aren't there.
        var filtering = name == AssetsPage && filter.Any;
        var shown = filtering ? cards.Where(card => filter.Shows(details.GetValueOrDefault(card.Id) ?? new AssetDetails(), looks[card.Id].Status)).ToList() : cards;
        return new CardPageData(cards, shown, filtering, sizes, looks, details, tags, versions, pictures);
    }

    // The page's title, how many cards it has, and for assets the tags and filters.
    private void FillCardHeading(Project project, CardPageView view)
    {
        var name = view.Page;
        var data = view.Data;
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var hint = new TextBlock { Text = data is null ? "" : data.Filtering ? "Clear the filters to reorder · right-click for more" : data.Cards.Count > 1 ? "Drag to reorder · right-click for more" : "", Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 3) };
        DockPanel.SetDock(hint, Dock.Right);
        heading.Children.Add(hint);
        if (name == AssetsPage)
        {
            Button editTags = null!;
            editTags = TextButton("Edit tags", () => OpenTagEditor(editTags, project));
            editTags.VerticalAlignment = VerticalAlignment.Bottom;
            editTags.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(editTags, Dock.Right);
            heading.Children.Add(editTags);
        }
        heading.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = name, Foreground = (Brush)FindResource("TextStrong"), FontSize = 24, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = data is null ? "" : data.Filtering ? $"{data.Shown.Count} of {data.Cards.Count}" : data.Cards.Count.ToString(), Foreground = (Brush)FindResource("TextMuted"), FontSize = 15, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 4) },
            },
        });
        view.Heading.Children.Clear();
        view.Heading.Children.Add(heading);
        if (name == AssetsPage && data is { Cards.Count: > 0 }) view.Heading.Children.Add(AssetFilterBar(project, data));
    }

    // As many cards to a row as fit, up to the page's width, beside the list's scroll bar.
    private int PerRowFor(CardPageView view)
    {
        var width = (view.List.ActualWidth > 0 ? view.List.ActualWidth : ProjectScroll.ActualWidth) - SystemParameters.VerticalScrollBarWidth;
        var room = Math.Min(CardPageWidth, width - 80);
        return Math.Max(1, (int)(room / (CardPages[view.Page].Width + 20)));
    }

    // Rows of cards, then the tile to add one. Each row says which cards are in it and how they look,
    // so a row is only built again when one of its cards has changed.
    private void ShowCards(Project project, CardPageView view)
    {
        if (view.Data is not { } data) return;
        var name = view.Page;
        var (one, width, height) = CardPages[name];
        var stage = data.Cards.FirstOrDefault()?.Board.Split('/')[0] ?? _database.ProjectStage(project.Id);
        var part = name.ToLowerInvariant();
        var dragging = _draggedBoardCard is not null && ReferenceEquals(_dragView, view);
        var order = dragging ? _dragOrder : data.Shown;
        var reorderable = !data.Filtering;
        view.PerRow = PerRowFor(view);

        string Version(BoardCard card) =>
            $"{card.Id}|{card.Name}|{data.PictureVersions.GetValueOrDefault(card.Id)}|{(data.Assets.TryGetValue(card.Id, out var look) ? look.Version : data.Sizes.GetValueOrDefault(card.Id))}|{(dragging && card.Id == _draggedBoardCard ? "dragged" : "")}|{reorderable}";

        var lines = new List<ListLine>();
        var tiles = order.Cast<BoardCard?>().Append(null).ToList(); // The last is the tile to add one.
        for (var at = 0; at < tiles.Count; at += view.PerRow)
        {
            var inRow = tiles.Skip(at).Take(view.PerRow).ToList();
            lines.Add(new ListLine($"row:{at / view.PerRow}", string.Join(";", inRow.Select(card => card is null ? $"add|{stage}" : Version(card))), () =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var card in inRow)
                {
                    row.Children.Add(card is null
                        ? AddCardTile(project, stage, part, one, width, height)
                        : CardTile(project, view, card, data.Assets.GetValueOrDefault(card.Id), data.Sizes.GetValueOrDefault(card.Id), reorderable));
                }
                return InPage(row, CardPageWidth);
            }));
        }

        if (data.Filtering && data.Shown.Count == 0) lines.Add(NoteLine("none", "No assets match these filters.", CardPageWidth));
        if (data.Cards.Count == 0) lines.Add(NoteLine("empty", $"Each {one} gets a card here, with a picture and a board of its own to plan it on.", CardPageWidth));
        lines.Add(EndLine());
        view.List.Show(lines);

        if (_cardToFocus is { } focus && focus.Until > DateTime.UtcNow && order.FindIndex(card => card.Id == focus.Id) is var place and >= 0)
        {
            view.List.BringLineIntoView($"row:{place / view.PerRow}");
            _ = Dispatcher.InvokeAsync(() => Buttons(view.List).FirstOrDefault(button => button.Tag is BoardCard card && card.Id == focus.Id)?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // How much is in it: its document's words, and anything on its board.
    private static string CardSize(string? markdown, int nodeCount)
    {
        var words = markdown is null ? 0 : System.Text.RegularExpressions.Regex.Matches(markdown, @"[\p{L}\p{N}]+").Count;
        var parts = new List<string>();
        if (words > 0) parts.Add(words == 1 ? "1 word" : $"{words:N0} words");
        if (nodeCount > 0) parts.Add(nodeCount == 1 ? "1 node on its board" : $"{nodeCount} nodes on its board");
        return parts.Count > 0 ? string.Join(" · ", parts) : "Nothing written yet";
    }

    // It arrived from another PC, and may not be a picture.
    private static ImageSource? OpenCardPicture(string? stored, int decodeWidth)
    {
        if (stored is null) return null;
        try
        {
            return ProjectImages.ToImage(Convert.FromBase64String(stored), decodeWidth);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private ImageSource? CardPicture(string cardId, int decodeWidth)
    {
        if (_database.CardImageVersion(cardId) is not { } version)
        {
            ForgetCardPicture(cardId);
            return null;
        }
        if (_cardPictures.TryGetValue((cardId, decodeWidth), out var opened) && opened.Version == version) return opened.Picture;
        var picture = OpenCardPicture(_database.GetCardImage(cardId), decodeWidth);
        _cardPictures[(cardId, decodeWidth)] = (version, picture);
        return picture;
    }

    private void ForgetCardPicture(string cardId)
    {
        foreach (var gone in _cardPictures.Keys.Where(each => each.CardId == cardId).ToList()) _cardPictures.Remove(gone);
    }

    private Button CardTile(Project project, CardPageView view, BoardCard card, AssetLook? asset, string? size, bool reorderable)
    {
        var page = view.Page;
        var (one, width, height) = CardPages[page];
        var picture = _cardPictures.GetValueOrDefault((card.Id, PictureWidth(page))).Picture;
        var shownName = card.Name.Length > 0 ? card.Name : $"Untitled {one}";

        var cover = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = picture is null ? PlaceholderFor(card.Id) : new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
        };
        if (picture is null)
        {
            // Until it has a picture, its first letter stands in for one.
            cover.Child = new TextBlock { Text = shownName[..1].ToUpperInvariant(), Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), FontSize = height * 0.42, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        var more = new Button
        {
            Style = (Style)FindResource("OverlayButton"),
            Content = "⋯",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Visibility = Visibility.Hidden,
        };
        AutomationProperties.SetName(more, $"More for {shownName}");

        var content = new StackPanel
        {
            Children =
            {
                new Grid { Children = { cover, more } },
                new TextBlock { Text = shownName, Foreground = (Brush)FindResource(card.Name.Length > 0 ? "TextStrong" : "TextMuted"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 10, 2, 0), ToolTip = shownName },
            },
        };
        // An asset says what it is instead of how much is on its board.
        content.Children.Add(asset is not null
            ? AssetCardLines(asset)
            : new TextBlock { Text = size ?? "", Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, Margin = new Thickness(2, 2, 2, 0) });
        var tile = new Button { Style = (Style)FindResource("ProjectCard"), Content = content, Tag = card, Width = width, Margin = new Thickness(0, 0, 20, 24), VerticalAlignment = VerticalAlignment.Top };
        if (_draggedBoardCard == card.Id) tile.Opacity = 0.5;
        AutomationProperties.SetName(tile, shownName);

        void Open()
        {
            _openCard = (project.Id, page, card.Id);
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
        }
        void OpenMenu(bool atMouse) => OpenCardMenu(atMouse ? tile : more, atMouse, project, page, card, one, Open);

        // The dots have to show for the keyboard too, or Tab can never reach them.
        void ShowActive()
        {
            var active = tile.IsMouseOver || tile.IsKeyboardFocusWithin;
            cover.BorderBrush = active ? (Brush)FindResource("Accent") : Brushes.Transparent;
            more.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        }
        tile.MouseEnter += (_, _) => ShowActive();
        tile.MouseLeave += (_, _) => ShowActive();
        tile.IsKeyboardFocusWithinChanged += (_, _) => ShowActive();

        more.PreviewMouseLeftButtonDown += (_, _) => _pressedBoardCard = null;
        more.Click += (_, click) =>
        {
            click.Handled = true;
            OpenMenu(atMouse: false);
        };
        tile.Click += (_, _) =>
        {
            if (DateTimeOffset.UtcNow - _cardDragEnded > TimeSpan.FromMilliseconds(250)) Open();
        };
        tile.MouseRightButtonUp += (_, click) =>
        {
            click.Handled = true;
            OpenMenu(atMouse: true);
        };

        // Dragged past a few pixels, it's picked up, and the list it's in moves it through the others.
        tile.PreviewMouseLeftButtonDown += (_, press) =>
        {
            if (!ReferenceEquals(press.Source, more)) _pressedBoardCard = (card.Id, press.GetPosition(view.List));
        };
        tile.PreviewMouseMove += (_, move) =>
        {
            if (move.LeftButton != MouseButtonState.Pressed)
            {
                _pressedBoardCard = null;
                return;
            }
            if (!reorderable || _draggedBoardCard is not null || _pressedBoardCard is not { } pressed || pressed.CardId != card.Id) return;
            if ((move.GetPosition(view.List) - pressed.At).Length < CardDragStartsAfter) return;
            _draggedBoardCard = card.Id;
            _dragView = view;
            _dragOrder = [.. view.Data?.Shown ?? []];
            _cardOrderChanged = false;
            view.List.CaptureMouse();
            ShowCards(project, view);
        };

        // And without a mouse: Ctrl and an arrow moves it, F2 renames it, Delete removes it.
        tile.PreviewKeyDown += (_, pressed) =>
        {
            if (!ReferenceEquals(pressed.OriginalSource, tile)) return;
            var step = reorderable && KeyState.Modifiers == ModifierKeys.Control ? pressed.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 } : 0;
            if (step != 0)
            {
                var order = (view.Data?.Shown ?? []).ToList();
                var from = order.FindIndex(each => each.Id == card.Id);
                var to = Math.Clamp(from + step, 0, order.Count - 1);
                if (from < 0 || to == from) return;
                var moving = order[from];
                order.RemoveAt(from);
                order.Insert(to, moving);
                _database.SaveCardOrder(order);
                FocusCardAfterRedraw(card.Id);
                RedrawProject(project);
            }
            else if (pressed.Key == Key.F2)
            {
                RenameCard(tile, project, card, one);
            }
            else if (pressed.Key == Key.Delete)
            {
                DeleteCard(project, card, one);
            }
            else if (pressed.Key == Key.Apps || (pressed.Key == Key.F10 && KeyState.Modifiers == ModifierKeys.Shift))
            {
                OpenMenu(atMouse: false);
            }
            else
            {
                return;
            }
            pressed.Handled = true;
        };
        return tile;
    }

    // While a card is dragged, the list holds the mouse, as the card's own row is built again each
    // time it moves past another, and a card under the mouse takes its place.
    private void WatchCardDrags(CardPageView view)
    {
        view.List.PreviewMouseMove += (_, move) =>
        {
            if (_draggedBoardCard is not { } dragged || !ReferenceEquals(_dragView, view) || _openProject is not { } project) return;
            var hit = view.List.InputHitTest(move.GetPosition(view.List)) as DependencyObject;
            while (hit is not null && hit is not Button { Tag: BoardCard }) hit = VisualTreeHelper.GetParent(hit);
            if (hit is not Button { Tag: BoardCard over } || over.Id == dragged) return;
            var from = _dragOrder.FindIndex(card => card.Id == dragged);
            var to = _dragOrder.FindIndex(card => card.Id == over.Id);
            if (from < 0 || to < 0) return;
            var moving = _dragOrder[from];
            _dragOrder.RemoveAt(from);
            _dragOrder.Insert(to, moving);
            _cardOrderChanged = true;
            ShowCards(project, view);
        };
        view.List.PreviewMouseLeftButtonUp += (_, release) =>
        {
            _pressedBoardCard = null;
            if (_draggedBoardCard is null || !ReferenceEquals(_dragView, view)) return;
            release.Handled = true; // Or letting go would count as a click, and open it.
            view.List.ReleaseMouseCapture();
        };
        view.List.LostMouseCapture += (_, _) =>
        {
            if (_draggedBoardCard is null || !ReferenceEquals(_dragView, view)) return;
            _draggedBoardCard = null;
            _dragView = null;
            _cardDragEnded = DateTimeOffset.UtcNow;
            if (_cardOrderChanged) _database.SaveCardOrder(_dragOrder);
            if (_openProject is { } project) RedrawProject(project); // Anything that arrived meanwhile was waiting for this.
        };
    }

    // The card keeps the keyboard once the page is read again and drawn with it.
    private (string Id, DateTime Until)? _cardToFocus;

    private void FocusCardAfterRedraw(string cardId) => _cardToFocus = (cardId, DateTime.UtcNow.AddSeconds(2));

    private Button AddCardTile(Project project, string stage, string part, string one, double width, double height)
    {
        var edge = new Rectangle { RadiusX = 10, RadiusY = 10, Stroke = (Brush)FindResource("Line"), StrokeThickness = 2, StrokeDashArray = [4, 3], Fill = Brushes.Transparent };
        var label = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "+", FontSize = 30, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = $"Add {one}", FontSize = 13.5, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 2, 0, 0) },
            },
        };
        TextElementColour(label, "TextMuted");

        var tile = new Button { Style = (Style)FindResource("ProjectCard"), Content = new Grid { Width = width, Height = height, Children = { edge, label } }, Width = width, Margin = new Thickness(0, 0, 20, 24), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(tile, $"Add {one}");

        void ShowActive()
        {
            var active = tile.IsMouseOver || tile.IsKeyboardFocusWithin;
            edge.Stroke = (Brush)FindResource(active ? "Accent" : "Line");
            TextElementColour(label, active ? "TextStrong" : "TextMuted");
        }
        tile.MouseEnter += (_, _) => ShowActive();
        tile.MouseLeave += (_, _) => ShowActive();
        tile.IsKeyboardFocusWithinChanged += (_, _) => ShowActive();
        tile.Click += (_, _) =>
        {
            // An asset is given its details as it's added.
            if (part == AssetsPart)
            {
                OpenAssetForm(tile, project, stage, null);
                return;
            }
            AskForName(tile, $"New {one}", "", "Add", name =>
            {
                var added = _database.AddCard(project.Id, stage, part, name);
                FocusCardAfterRedraw(added.Id);
                RedrawProject(project);
            });
        };
        return tile;
    }

    private void TextElementColour(Panel of, string brush)
    {
        foreach (var text in of.Children.OfType<TextBlock>()) text.Foreground = (Brush)FindResource(brush);
    }

    // Menus and the small windows they open

    private Popup NewMenu(UIElement by, bool atMouse, UIElement content) => new()
    {
        PlacementTarget = by,
        Placement = atMouse ? PlacementMode.MousePoint : PlacementMode.Bottom,
        VerticalOffset = atMouse ? 0 : 6,
        StaysOpen = false,
        AllowsTransparency = true,
        Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x32)),
            BorderBrush = (Brush)FindResource("ModalBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Child = content,
        },
    };

    private void OpenCardMenu(UIElement by, bool atMouse, Project project, string page, BoardCard card, string one, Action open)
    {
        var items = new StackPanel { MinWidth = 180 };
        var menu = NewMenu(by, atMouse, items);

        void Item(string label, Action act, bool danger = false, string? aside = null)
        {
            var content = new DockPanel();
            if (aside is not null)
            {
                var side = new TextBlock { Text = aside, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(14, 0, 0, 0) };
                DockPanel.SetDock(side, Dock.Right);
                content.Children.Add(side);
            }
            content.Children.Add(new TextBlock { Text = label });
            var item = new Button { Style = (Style)FindResource("HeaderButton"), Content = aside is null ? label : content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = aside is null ? HorizontalAlignment.Left : HorizontalAlignment.Stretch };
            if (aside is not null) AutomationProperties.SetName(item, aside == "✓" ? $"{label}, chosen" : label);
            if (danger) item.Foreground = (Brush)FindResource("Danger");
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                act();
            };
            items.Children.Add(item);
        }

        Item("Open", open);
        Item("Rename", () => RenameCard(by, project, card, one));
        if (page == AssetsPage) Item("Edit details", () => OpenAssetForm(by, project, card.Board.Split('/')[0], card));
        Item(_database.GetCardImage(card.Id) is null ? "Add a picture" : "Change the picture", () => ChangeCardPicture(project, card));
        if (_database.GetCardImage(card.Id) is not null)
        {
            Item("Remove the picture", () =>
            {
                _database.SetCardImage(card.Id, null);
                RedrawProject(project);
            });
        }
        Item("Delete", () => DeleteCard(project, card, one), danger: true);

        // An asset's status, a click away.
        if (page == AssetsPage)
        {
            var line = Divider();
            line.Margin = new Thickness(0, 6, 0, 2);
            items.Children.Add(line);
            items.Children.Add(ListHeading("Status"));
            var now = AssetStatus.Of(_database.GetAssetDetails(card.Id), _database.GetAssetDeliverable(card.Id) is not null);
            foreach (var (id, statusName) in AssetStatus.All) Item(statusName, () => SetAssetStatus(project, card, id), aside: id == now ? "✓" : "");
        }

        menu.PreviewKeyDown += (_, pressed) =>
        {
            var buttons = items.Children.OfType<Button>().ToList();
            var at = buttons.FindIndex(button => button.IsKeyboardFocused);
            if (pressed.Key == Key.Escape) menu.IsOpen = false;
            else if (pressed.Key == Key.Down) buttons[(at + 1) % buttons.Count].Focus();
            else if (pressed.Key == Key.Up) buttons[(at + buttons.Count - 1) % buttons.Count].Focus();
            else return;
            pressed.Handled = true;
        };
        menu.Opened += (_, _) => items.Children.OfType<Button>().First().Focus();
        menu.IsOpen = true;
    }

    // A name typed into a small window by whatever asked for it. Enter uses it, and Esc doesn't.
    private void AskForName(UIElement by, string title, string now, string action, Action<string> use)
    {
        var problem = ProblemText();
        problem.Margin = new Thickness(0, 6, 0, 0);
        var name = Field(now);
        name.Width = double.NaN; // The whole row, in line with its title and button, not the style's own width.
        name.MaxLength = LocalDatabase.LongestCardName;
        AutomationProperties.SetName(name, title);

        var rows = new StackPanel { Width = 260, Margin = new Thickness(8, 6, 8, 6) };
        var asked = NewMenu(by, atMouse: false, rows);
        void Use()
        {
            var typed = name.Text.Trim();
            if (typed.Length == 0)
            {
                ShowProblem(problem, "Give it a name.");
                return;
            }
            asked.IsOpen = false;
            if (typed != now) use(typed);
        }
        name.KeyDown += (_, pressed) =>
        {
            if (pressed.Key == Key.Enter) Use();
            else if (pressed.Key == Key.Escape) asked.IsOpen = false;
            else return;
            pressed.Handled = true;
        };

        var accept = ControlButton(action, Use);
        accept.Margin = new Thickness(0, 8, 0, 0);
        accept.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 0, 0, 6) });
        rows.Children.Add(name);
        rows.Children.Add(accept);
        rows.Children.Add(problem);

        asked.Opened += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        asked.IsOpen = true;
    }

    private void RenameCard(UIElement by, Project project, BoardCard card, string one) =>
        AskForName(by, $"Rename this {one}", card.Name, "Rename", name =>
        {
            _database.RenameCard(card, name);
            FocusCardAfterRedraw(card.Id);
            RedrawProject(project);
        });

    private void ChangeCardPicture(Project project, BoardCard card)
    {
        if (Dialogs.PickFile("Choose a picture", PictureFiles) is not { } path) return;
        if (ProjectImages.PrepareForBoard(path) is not { } image)
        {
            Dialogs.Notify($"{System.IO.Path.GetFileName(path)} couldn't be used as a picture.");
            return;
        }
        _database.SetCardImage(card.Id, image);
        RedrawProject(project);
    }

    private void DeleteCard(Project project, BoardCard card, string one)
    {
        var name = card.Name.Length > 0 ? card.Name : $"this {one}";
        if (!Dialogs.Confirm($"Delete {name}?\n\nIts picture and everything on its board are deleted too. This can't be undone.")) return;
        _database.DeleteCard(project.Id, card);
        ForgetCardPicture(card.Id);
        RedrawProject(project);
    }
}
