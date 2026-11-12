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
/// </summary>
public partial class MainWindow
{
    // What one card is called on each page, and the shape of its picture: people stand tall, places lie wide.
    private static readonly Dictionary<string, (string One, double Width, double Height)> CardPages = new()
    {
        ["Characters"] = ("character", 184, 244),
        ["Levels"] = ("level", 264, 164),
        ["Mechanics"] = ("mechanic", 264, 164),
    };
    private const string PictureFiles = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff";
    private const double CardDragStartsAfter = 6;

    // Which card's own board is open, on which page of which project.
    private (string ProjectId, string Section, string CardId)? _openCard;

    // Each card's picture is opened once, and again only when it changes.
    private readonly Dictionary<string, (string Stored, ImageSource? Picture)> _cardPictures = [];

    private Button? _draggedBoardCard;
    private (Button Card, Point At)? _pressedBoardCard;
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

    private void AddCardPage(StackPanel body, Project project, string name)
    {
        var (one, width, height) = CardPages[name];
        var stage = _database.ProjectStage(project.Id);
        var part = name.ToLowerInvariant();
        var cards = _database.ListCards(project.Id, stage, part);
        var nodes = _database.CountNodesByBoard(project.Id);

        body.MaxWidth = 1240;
        body.Margin = new Thickness(40, 22, 40, 30);

        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var hint = new TextBlock { Text = cards.Count > 1 ? "Drag to reorder · right-click for more" : "", Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 3) };
        DockPanel.SetDock(hint, Dock.Right);
        heading.Children.Add(hint);
        heading.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = name, Foreground = (Brush)FindResource("TextStrong"), FontSize = 24, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = cards.Count.ToString(), Foreground = (Brush)FindResource("TextMuted"), FontSize = 15, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 4) },
            },
        });
        body.Children.Add(heading);

        var grid = new WrapPanel();
        foreach (var card in cards) grid.Children.Add(CardTile(project, name, card, grid, nodes.GetValueOrDefault(LocalDatabase.CardBoardKey(card))));
        grid.Children.Add(AddCardTile(project, stage, part, one, width, height));
        body.Children.Add(grid);

        if (cards.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"Each {one} gets a card here, with a picture and a board of its own to plan it on.",
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 13.5,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
    }

    private ImageSource? CardPicture(string cardId, int decodeWidth)
    {
        if (_database.GetCardImage(cardId) is not { } stored)
        {
            _cardPictures.Remove(cardId);
            return null;
        }
        if (_cardPictures.TryGetValue(cardId, out var opened) && opened.Stored == stored) return opened.Picture;

        ImageSource? picture = null;
        try
        {
            picture = ProjectImages.ToImage(Convert.FromBase64String(stored), decodeWidth);
        }
        catch (FormatException)
        {
            // It arrived from another PC, and isn't a picture.
        }
        _cardPictures[cardId] = (stored, picture);
        return picture;
    }

    private Button CardTile(Project project, string page, BoardCard card, WrapPanel grid, int nodeCount)
    {
        var (one, width, height) = CardPages[page];
        var picture = CardPicture(card.Id, (int)(width * 2));
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
                new TextBlock { Text = nodeCount switch { 0 => "Nothing on its board yet", 1 => "1 node", _ => $"{nodeCount} nodes" }, Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, Margin = new Thickness(2, 2, 2, 0) },
            },
        };
        var tile = new Button { Style = (Style)FindResource("ProjectCard"), Content = content, Tag = card, Width = width, Margin = new Thickness(0, 0, 20, 24), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(tile, shownName);

        void Open()
        {
            _openCard = (project.Id, page, card.Id);
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
        }
        void OpenMenu(bool atMouse) => OpenCardMenu(atMouse ? tile : more, atMouse, project, card, one, Open);

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

        // Dragged past a few pixels, it follows the mouse through the others.
        tile.PreviewMouseLeftButtonDown += (_, press) =>
        {
            if (!ReferenceEquals(press.Source, more)) _pressedBoardCard = (tile, press.GetPosition(grid));
        };
        tile.PreviewMouseMove += (_, move) =>
        {
            if (move.LeftButton != MouseButtonState.Pressed)
            {
                _pressedBoardCard = null;
                return;
            }
            var at = move.GetPosition(grid);
            if (_draggedBoardCard is null && _pressedBoardCard is { } pressed && ReferenceEquals(pressed.Card, tile) && (at - pressed.At).Length >= CardDragStartsAfter)
            {
                _draggedBoardCard = tile;
                _cardOrderChanged = false;
                tile.Opacity = 0.5;
                tile.CaptureMouse();
            }
            if (!ReferenceEquals(_draggedBoardCard, tile)) return;

            var over = grid.Children.OfType<Button>().FirstOrDefault(other => other.Tag is BoardCard && !ReferenceEquals(other, tile) && new Rect(other.TranslatePoint(new Point(0, 0), grid), other.RenderSize).Contains(at));
            if (over is null) return;
            var to = grid.Children.IndexOf(over);
            grid.Children.Remove(tile);
            grid.Children.Insert(to, tile);
            _cardOrderChanged = true;
        };
        tile.PreviewMouseLeftButtonUp += (_, release) =>
        {
            _pressedBoardCard = null;
            if (!ReferenceEquals(_draggedBoardCard, tile)) return;
            release.Handled = true; // Or letting go would count as a click, and open it.
            tile.ReleaseMouseCapture();
        };
        tile.LostMouseCapture += (_, _) =>
        {
            if (!ReferenceEquals(_draggedBoardCard, tile)) return;
            _draggedBoardCard = null;
            _cardDragEnded = DateTimeOffset.UtcNow;
            tile.Opacity = 1;
            if (_cardOrderChanged) _database.SaveCardOrder([.. grid.Children.OfType<Button>().Select(other => other.Tag).OfType<BoardCard>()]);
            RedrawProject(project); // Anything that arrived meanwhile was waiting for this.
        };

        // And without a mouse: Ctrl and an arrow moves it, F2 renames it, Delete removes it.
        tile.PreviewKeyDown += (_, pressed) =>
        {
            if (!ReferenceEquals(pressed.OriginalSource, tile)) return;
            var step = Keyboard.Modifiers == ModifierKeys.Control ? pressed.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 } : 0;
            if (step != 0)
            {
                var order = grid.Children.OfType<Button>().Select(other => other.Tag).OfType<BoardCard>().ToList();
                var from = order.IndexOf(card);
                var to = Math.Clamp(from + step, 0, order.Count - 1);
                if (to == from) return;
                order.RemoveAt(from);
                order.Insert(to, card);
                _database.SaveCardOrder(order);
                RedrawProject(project);
                FocusCardAfterRedraw(card.Id);
            }
            else if (pressed.Key == Key.F2)
            {
                RenameCard(tile, project, card, one);
            }
            else if (pressed.Key == Key.Delete)
            {
                DeleteCard(project, card, one);
            }
            else if (pressed.Key == Key.Apps || (pressed.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
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

    private void FocusCardAfterRedraw(string cardId)
    {
        static IEnumerable<Button> Buttons(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Button button) yield return button;
                foreach (var inside in Buttons(child)) yield return inside;
            }
        }
        _ = Dispatcher.InvokeAsync(() => Buttons(ProjectPage).FirstOrDefault(button => button.Tag is BoardCard card && card.Id == cardId)?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

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
        tile.Click += (_, _) => AskForName(tile, $"New {one}", "", "Add", name =>
        {
            var added = _database.AddCard(project.Id, stage, part, name);
            RedrawProject(project);
            FocusCardAfterRedraw(added.Id);
        });
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

    private void OpenCardMenu(UIElement by, bool atMouse, Project project, BoardCard card, string one, Action open)
    {
        var items = new StackPanel { MinWidth = 180 };
        var menu = NewMenu(by, atMouse, items);

        void Item(string label, Action act, bool danger = false)
        {
            var item = new Button { Style = (Style)FindResource("HeaderButton"), Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
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
            RedrawProject(project);
            FocusCardAfterRedraw(card.Id);
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
        _cardPictures.Remove(card.Id);
        RedrawProject(project);
    }
}
