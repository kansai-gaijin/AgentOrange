using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentOrange;

/// <summary>
/// A board of nodes and the links between them, for one part of a project. Every node and link is
/// a record of its own, so two PCs changing different ones both count, and it all syncs.
///
/// Shift+A opens the list of nodes and adds the one picked where the mouse is, the way Blender
/// does. Double-click a node to rename it, drag it to move it, drag from the dot on its side to
/// another node to link them, and drag empty space to move around. The wheel zooms, and Delete
/// removes what's selected.
/// </summary>
internal sealed class NodeCanvas : Border
{
    private const double SmallestZoom = 0.25;
    private const double LargestZoom = 3;
    private const double DragStartsAfter = 4;
    // The first kind of node: a heading, in large type. One saved before nodes had kinds is one of these.
    private const string TitleKind = "title";

    // Every kind there is, in the order the list shows them.
    private static readonly (string Kind, string Name)[] Kinds = [(TitleKind, "Title")];
    private const string Untitled = "Title";
    private const double TitleSize = 22;
    private const double TitleWidth = 420;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record NodeData(string Board, double X, double Y, string Title, string? Kind = null);

    private sealed record LinkData(string Board, string From, string To);

    private sealed class NodeView
    {
        public required string Id { get; init; }
        public required NodeData Data { get; set; }
        public required Border Box { get; init; }
        public required Border Inside { get; init; }
        public required TextBlock Title { get; init; }
        public required Ellipse Handle { get; init; }
    }

    private sealed class LinkView
    {
        public required string Id { get; init; }
        public required LinkData Data { get; init; }
        public required Line Line { get; init; }
    }

    private enum Gesture { None, Pan, Move, Link }

    private readonly LocalDatabase _database;
    private readonly string _projectId;
    private readonly string _board;
    private readonly Action _changed;

    private readonly Canvas _world = new();
    private readonly MatrixTransform _view = new();
    private readonly TextBlock _hint;
    private readonly Dictionary<string, NodeView> _nodes = [];
    private readonly Dictionary<string, LinkView> _links = [];
    private string? _selected; // A node's or a link's.

    private Gesture _gesture;
    private Point _pressedAt;
    private Point _pressedAtInWorld;
    private Matrix _viewWhenPressed;
    private NodeView? _held;
    private Point _heldFrom;
    private bool _dragging;
    private Line? _newLink;
    private (NodeView Node, TextBox Box)? _editing;

    public NodeCanvas(LocalDatabase database, string projectId, string board, Action changed)
    {
        _database = database;
        _projectId = projectId;
        _board = board;
        _changed = changed;

        Background = new SolidColorBrush(Color.FromRgb(0x13, 0x1B, 0x24));
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        _world.RenderTransform = _view;
        _hint = new TextBlock
        {
            Text = "Press Shift+A to add a node",
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Child = new Grid { Children = { _world, _hint } };

        MouseLeftButtonDown += Pressed;
        MouseMove += Moved;
        MouseLeftButtonUp += Released;
        LostMouseCapture += (_, _) => EndGesture(null);
        PreviewMouseWheel += Zoomed;
        KeyDown += KeyPressed;
    }

    /// <summary>In the middle of something that drawing the page again would cut short.</summary>
    public bool IsBusy => _gesture != Gesture.None || _editing is not null;

    /// <summary>Reads the board again, as another PC or window can have changed it.</summary>
    public void Reload()
    {
        if (IsBusy) return;

        _world.Children.Clear();
        _nodes.Clear();
        _links.Clear();

        foreach (var (id, text) in _database.ListProjectRecords(LocalDatabase.NodesCollection, _projectId))
        {
            if (Parse<NodeData>(text) is { } data && data.Board == _board) AddNodeView(id, data);
        }
        foreach (var (id, text) in _database.ListProjectRecords(LocalDatabase.NodeLinksCollection, _projectId))
        {
            // One whose node was deleted on another PC at the same moment has nothing to join.
            if (Parse<LinkData>(text) is { } data && data.Board == _board && _nodes.ContainsKey(data.From) && _nodes.ContainsKey(data.To)) AddLinkView(id, data);
        }

        if (_selected is not null && !_nodes.ContainsKey(_selected) && !_links.ContainsKey(_selected)) _selected = null;
        ShowSelection();
        _hint.Visibility = _nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Records arrive from other PCs, so one that can't be read is left out.
    private static T? Parse<T>(string text) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string NewId() => $"{_projectId}:{Guid.NewGuid():N}";

    // Nodes

    private NodeView AddNodeView(string id, NodeData data)
    {
        var title = new TextBlock
        {
            Text = data.Title.Length > 0 ? data.Title : Untitled,
            Foreground = (Brush)FindResource(data.Title.Length > 0 ? "TextStrong" : "TextMuted"),
            FontSize = TitleSize,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = TitleWidth,
        };
        var inside = new Border { Padding = new Thickness(18, 12, 18, 12), Child = title };

        // Sits on the edge, and is where a link is dragged from.
        var handle = new Ellipse
        {
            Width = 13,
            Height = 13,
            Fill = (Brush)FindResource("Accent"),
            Stroke = Background,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -8, 0),
            Cursor = Cursors.Cross,
            Visibility = Visibility.Hidden,
            ToolTip = "Drag to another node to link them",
        };
        var box = new Border
        {
            Background = (Brush)FindResource("ControlBackground"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            MinWidth = 160,
            Child = new Grid { Children = { inside, handle } },
        };

        var view = new NodeView { Id = id, Data = data, Box = box, Inside = inside, Title = title, Handle = handle };
        box.Tag = view;
        box.MouseEnter += (_, _) => handle.Visibility = Visibility.Visible;
        box.MouseLeave += (_, _) => handle.Visibility = _selected == id ? Visibility.Visible : Visibility.Hidden;
        box.SizeChanged += (_, _) => PlaceLinksOf(view);

        Canvas.SetLeft(box, data.X);
        Canvas.SetTop(box, data.Y);
        _world.Children.Add(box);
        _nodes[id] = view;
        return view;
    }

    private void SaveNode(NodeView node)
    {
        _database.SaveRecord(LocalDatabase.NodesCollection, node.Id, JsonSerializer.Serialize(node.Data, JsonOptions));
        _changed();
    }

    private void AddNodeAt(Point where, string kind)
    {
        var node = AddNodeView(NewId(), new NodeData(_board, Math.Round(where.X - 80), Math.Round(where.Y - 27), "", kind));
        _hint.Visibility = Visibility.Collapsed;
        SaveNode(node);
        Select(node.Id);
        StartEditing(node);
    }

    private void StartEditing(NodeView node)
    {
        var box = new TextBox
        {
            Text = node.Data.Title,
            FontSize = TitleSize,
            FontWeight = FontWeights.Bold,
            MinWidth = 124,
            MaxWidth = TitleWidth,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(-2, 0, -2, 0),
            Foreground = (Brush)FindResource("TextStrong"),
            CaretBrush = (Brush)FindResource("TextStrong"),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Node name");
        _editing = (node, box);
        node.Inside.Child = box;

        box.KeyDown += (_, pressed) =>
        {
            if (pressed.Key is not (Key.Enter or Key.Escape)) return;
            pressed.Handled = true;
            StopEditing(keep: pressed.Key == Key.Enter);
            Focus();
        };
        box.LostKeyboardFocus += (_, _) => StopEditing(keep: true);
        Dispatcher.InvokeAsync(() =>
        {
            box.Focus();
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void StopEditing(bool keep)
    {
        if (_editing is not { } editing) return;
        _editing = null; // First, as taking the box away makes it lose focus, which comes back here.

        var (node, box) = editing;
        var title = box.Text.Trim();
        node.Inside.Child = node.Title;
        if (!keep || title == node.Data.Title) return;

        node.Data = node.Data with { Title = title };
        node.Title.Text = title.Length > 0 ? title : Untitled;
        node.Title.Foreground = (Brush)FindResource(title.Length > 0 ? "TextStrong" : "TextMuted");
        SaveNode(node);
    }

    // Links

    private void AddLinkView(string id, LinkData data)
    {
        var line = new Line { Stroke = (Brush)FindResource("TextMuted"), StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Cursor = Cursors.Hand };
        var view = new LinkView { Id = id, Data = data, Line = line };
        line.Tag = view;
        _world.Children.Insert(0, line); // Under the nodes.
        _links[id] = view;
        Place(view);
    }

    private static Point CentreOf(NodeView node) => new(node.Data.X + node.Box.ActualWidth / 2, node.Data.Y + node.Box.ActualHeight / 2);

    private void Place(LinkView link)
    {
        if (!_nodes.TryGetValue(link.Data.From, out var from) || !_nodes.TryGetValue(link.Data.To, out var to)) return;
        var (start, end) = (CentreOf(from), CentreOf(to));
        (link.Line.X1, link.Line.Y1, link.Line.X2, link.Line.Y2) = (start.X, start.Y, end.X, end.Y);
    }

    private void PlaceLinksOf(NodeView node)
    {
        foreach (var link in _links.Values.Where(link => link.Data.From == node.Id || link.Data.To == node.Id)) Place(link);
    }

    private void AddLink(NodeView from, NodeView to)
    {
        // One link between two nodes, whichever way it was drawn.
        if (from == to || _links.Values.Any(link => (link.Data.From == from.Id && link.Data.To == to.Id) || (link.Data.From == to.Id && link.Data.To == from.Id))) return;

        var id = NewId();
        var data = new LinkData(_board, from.Id, to.Id);
        AddLinkView(id, data);
        _database.SaveRecord(LocalDatabase.NodeLinksCollection, id, JsonSerializer.Serialize(data, JsonOptions));
        _changed();
    }

    // Selecting and deleting

    private void Select(string? id)
    {
        _selected = id;
        ShowSelection();
    }

    private void ShowSelection()
    {
        foreach (var node in _nodes.Values)
        {
            var selected = node.Id == _selected;
            node.Box.BorderBrush = (Brush)FindResource(selected ? "Accent" : "Line");
            if (!node.Box.IsMouseOver) node.Handle.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        }
        foreach (var link in _links.Values) link.Line.Stroke = (Brush)FindResource(link.Id == _selected ? "Accent" : "TextMuted");
    }

    private void DeleteSelected()
    {
        if (_selected is not { } id) return;

        if (_links.Remove(id, out var onlyLink))
        {
            _world.Children.Remove(onlyLink.Line);
            _database.SaveRecord(LocalDatabase.NodeLinksCollection, id, "", deleted: true);
        }
        else if (_nodes.Remove(id, out var node))
        {
            _world.Children.Remove(node.Box);
            _database.SaveRecord(LocalDatabase.NodesCollection, id, "", deleted: true);
            foreach (var link in _links.Values.Where(link => link.Data.From == id || link.Data.To == id).ToList())
            {
                _links.Remove(link.Id);
                _world.Children.Remove(link.Line);
                _database.SaveRecord(LocalDatabase.NodeLinksCollection, link.Id, "", deleted: true);
            }
        }
        _selected = null;
        _hint.Visibility = _nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _changed();
    }

    // The list of nodes to add, which opens at the mouse and adds there.

    private void OpenAddList()
    {
        // Where the mouse is, or the middle if it's somewhere else, such as when Tab got here.
        var mouse = Mouse.GetPosition(this);
        var over = new Rect(0, 0, ActualWidth, ActualHeight).Contains(mouse);
        var where = ToWorld(over ? mouse : new Point(ActualWidth / 2, ActualHeight / 2));

        var items = new StackPanel();
        var list = new Popup
        {
            PlacementTarget = this,
            Placement = over ? PlacementMode.MousePoint : PlacementMode.Center,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x32)),
                BorderBrush = (Brush)FindResource("ModalBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                MinWidth = 180,
                Child = items,
            },
        };

        items.Children.Add(new TextBlock { Text = "Add", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(8, 4, 8, 6) });
        foreach (var (kind, name) in Kinds)
        {
            var item = new Button { Style = (Style)FindResource("HeaderButton"), Content = name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            item.Click += (_, _) =>
            {
                list.IsOpen = false;
                AddNodeAt(where, kind);
            };
            items.Children.Add(item);
        }

        list.PreviewKeyDown += (_, pressed) =>
        {
            var buttons = items.Children.OfType<Button>().ToList();
            var at = buttons.FindIndex(button => button.IsKeyboardFocused);
            switch (pressed.Key)
            {
                case Key.Escape:
                    list.IsOpen = false;
                    Focus();
                    break;
                case Key.Down:
                    buttons[(at + 1) % buttons.Count].Focus();
                    break;
                case Key.Up:
                    buttons[(at + buttons.Count - 1) % buttons.Count].Focus();
                    break;
                default:
                    return;
            }
            pressed.Handled = true;
        };
        list.Opened += (_, _) => items.Children.OfType<Button>().First().Focus();
        list.IsOpen = true;
    }

    // Mouse and keyboard

    private Point ToWorld(Point onScreen)
    {
        var inverse = _view.Matrix;
        inverse.Invert();
        return inverse.Transform(onScreen);
    }

    // What a click landed on, as it lands on the text or shape inside.
    private static T? Under<T>(object? source) where T : class
    {
        for (var at = source as DependencyObject; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is FrameworkElement { Tag: T found }) return found;
        }
        return null;
    }

    private NodeView? NodeAt(Point inWorld) =>
        _nodes.Values.LastOrDefault(node => new Rect(node.Data.X, node.Data.Y, node.Box.ActualWidth, node.Box.ActualHeight).Contains(inWorld));

    private void Pressed(object sender, MouseButtonEventArgs press)
    {
        Focus();
        StopEditing(keep: true);

        var node = Under<NodeView>(press.OriginalSource);
        var link = Under<LinkView>(press.OriginalSource);
        _pressedAt = press.GetPosition(this);
        _pressedAtInWorld = ToWorld(_pressedAt);
        press.Handled = true;

        if (press.ClickCount == 2)
        {
            if (node is not null) StartEditing(node);
            return;
        }

        _dragging = false;
        _held = node;
        if (node is not null && ReferenceEquals(press.OriginalSource, node.Handle))
        {
            Select(node.Id);
            _gesture = Gesture.Link;
            _newLink = new Line { Stroke = (Brush)FindResource("Accent"), StrokeThickness = 2.5, StrokeDashArray = [3, 2], IsHitTestVisible = false };
            var from = CentreOf(node);
            (_newLink.X1, _newLink.Y1, _newLink.X2, _newLink.Y2) = (from.X, from.Y, from.X, from.Y);
            _world.Children.Insert(0, _newLink);
        }
        else if (node is not null)
        {
            Select(node.Id);
            _gesture = Gesture.Move;
            _heldFrom = new Point(node.Data.X, node.Data.Y);
        }
        else if (link is not null)
        {
            Select(link.Id);
            return;
        }
        else
        {
            Select(null);
            _gesture = Gesture.Pan;
            _viewWhenPressed = _view.Matrix;
        }
        CaptureMouse();
    }

    private void Moved(object sender, MouseEventArgs move)
    {
        if (_gesture == Gesture.None) return;

        var at = move.GetPosition(this);
        if (!_dragging && (at - _pressedAt).Length < DragStartsAfter) return; // A click that wobbled.
        _dragging = true;
        var inWorld = ToWorld(at);

        switch (_gesture)
        {
            case Gesture.Pan:
                var view = _viewWhenPressed;
                view.Translate(at.X - _pressedAt.X, at.Y - _pressedAt.Y);
                _view.Matrix = view;
                break;
            case Gesture.Move when _held is { } node:
                node.Data = node.Data with { X = Math.Round(_heldFrom.X + inWorld.X - _pressedAtInWorld.X), Y = Math.Round(_heldFrom.Y + inWorld.Y - _pressedAtInWorld.Y) };
                Canvas.SetLeft(node.Box, node.Data.X);
                Canvas.SetTop(node.Box, node.Data.Y);
                PlaceLinksOf(node);
                break;
            case Gesture.Link when _newLink is { } line:
                (line.X2, line.Y2) = (inWorld.X, inWorld.Y);
                break;
        }
    }

    private void Released(object sender, MouseButtonEventArgs release) => EndGesture(ToWorld(release.GetPosition(this)));

    // Null for where it ended means it was cut short, such as by the window losing the mouse.
    private void EndGesture(Point? endedAt)
    {
        var (gesture, held, dragged) = (_gesture, _held, _dragging);
        if (gesture == Gesture.None) return;
        _gesture = Gesture.None;
        _held = null;
        _dragging = false;
        if (_newLink is { } line)
        {
            _world.Children.Remove(line);
            _newLink = null;
        }
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (gesture == Gesture.Move && dragged && held is not null) SaveNode(held);
        if (gesture == Gesture.Link && held is not null && endedAt is { } at && NodeAt(at) is { } target) AddLink(held, target);
    }

    private void Zoomed(object sender, MouseWheelEventArgs wheel)
    {
        wheel.Handled = true; // Or the page behind would scroll.

        var view = _view.Matrix;
        var zoom = Math.Clamp(view.M11 * (wheel.Delta > 0 ? 1.1 : 1 / 1.1), SmallestZoom, LargestZoom);
        var at = wheel.GetPosition(this);
        view.ScaleAt(zoom / view.M11, zoom / view.M11, at.X, at.Y);
        _view.Matrix = view;
    }

    private void KeyPressed(object sender, KeyEventArgs pressed)
    {
        if (_editing is not null) return;

        if (pressed.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            OpenAddList();
            pressed.Handled = true;
        }
        else if (pressed.Key == Key.Delete)
        {
            DeleteSelected();
            pressed.Handled = true;
        }
        else if (pressed.Key == Key.F2 && _selected is { } id && _nodes.TryGetValue(id, out var node))
        {
            StartEditing(node);
            pressed.Handled = true;
        }
    }
}
