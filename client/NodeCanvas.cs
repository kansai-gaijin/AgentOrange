using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentOrange;

/// <summary>
/// A board of nodes and the links between them, for one part of a project. Every node and link is
/// a record of its own, so two PCs changing different ones both count, and it all syncs.
///
/// Shift+A opens the list of nodes and adds the one picked where the mouse is, the way Blender
/// does. Double-click a node to change its words, drag it to move it, drag from one of its dots to
/// another node to link them, and drag empty space to move around. The wheel zooms, and Delete
/// removes what's selected.
/// </summary>
internal sealed partial class NodeCanvas : Border
{
    private const double SmallestZoom = 0.25;
    private const double LargestZoom = 3;
    private const double DragStartsAfter = 4;
    // The first kind of node: a heading, in large type. One saved before nodes had kinds is one of these.
    private const string TitleKind = "title";

    // Words that can be given headings, lists, tick boxes, colours and links, in a box dragged to size.
    private const string TextKind = "text";

    // Every kind there is, in the order the list shows them.
    private static readonly (string Kind, string Name)[] Kinds = [(TitleKind, "Title"), (TextKind, "Rich Text")];
    private const string TopPin = "top";
    private const string BottomPin = "bottom";
    private const string ResizePart = "resize";
    private const double TextSize = 15;
    private const double TextWidth = 280;
    private const double NarrowestText = 120;
    private const double WidestText = 1200;
    private const double ShortestText = 30;
    private const double TallestText = 4000;
    private const string Untitled = "Title";
    private const double TitleSize = 22;
    private const double TitleWidth = 420;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Text is a Rich Text node's words, as the XAML of a document. Width is how wide it was dragged
    // to be, and Height how tall at least, as it's never shorter than its words.
    private sealed record NodeData(string Board, double X, double Y, string Title, string? Kind = null, double? Width = null, string? Text = null, double? Height = null);

    // A link saved before nodes had a pin on top joins the bottom ones.
    private sealed record LinkData(string Board, string From, string To, string? FromPin = null, string? ToPin = null);

    private sealed class NodeView
    {
        public required string Id { get; init; }
        public required NodeData Data { get; set; }
        public required Border Box { get; init; }
        public required Border Inside { get; init; }
        public required string[] Pins { get; init; }
        public TextBlock? Title { get; set; } // A Title node's words.
        public RichTextBox? Rich { get; set; } // A Rich Text node's words.
        public TextBlock? Empty { get; set; } // What a Rich Text node shows while it has none.
    }

    private sealed class LinkView
    {
        public required string Id { get; init; }
        public required LinkData Data { get; init; }
        public required Line Line { get; init; }
    }

    private enum Gesture { None, Pan, Move, Link, Resize }

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
    private string _heldPin = BottomPin;
    private string _heldEdge = ResizePart; // Which of its edges is being dragged to resize it.
    private Point _heldFrom;
    private bool _dragging;
    private Line? _newLink;
    private NodeView? _editing;
    private TextBox? _editingBox; // Only a Title node gets a box put in to type in.

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
        _toolbar = NewToolbar();
        Child = new Grid { Children = { _world, _hint, _toolbar } };

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
        var isText = data.Kind == TextKind;
        var inside = new Border { Padding = new Thickness(10, isText ? 12 : 6, 10, 12) };
        var parts = new Grid { Children = { inside } };
        var box = new Border
        {
            // Only the words show. See-through rather than nothing, so the space between them can still be clicked.
            Background = Brushes.Transparent,
            // A Rich Text node has a border, to show its size. A Title node's shows only while it's selected.
            BorderBrush = isText ? (Brush)FindResource("Line") : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Child = parts,
        };
        var view = new NodeView { Id = id, Data = data, Box = box, Inside = inside, Pins = isText ? [TopPin, BottomPin] : [BottomPin] };
        box.Tag = view;

        if (isText)
        {
            AddTextParts(view, parts);
        }
        else
        {
            view.Title = new TextBlock
            {
                Text = data.Title.Length > 0 ? data.Title : Untitled,
                Foreground = (Brush)FindResource(data.Title.Length > 0 ? "TextStrong" : "TextMuted"),
                FontSize = TitleSize,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = TitleWidth,
            };
            inside.Child = view.Title;
        }

        // Each sits on an edge, always showing, and is where a link is dragged from and where links join.
        foreach (var pin in view.Pins)
        {
            parts.Children.Add(new Ellipse
            {
                Tag = pin,
                Width = 13,
                Height = 13,
                Fill = (Brush)FindResource("Accent"),
                Stroke = Background,
                StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = pin == TopPin ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                Margin = pin == TopPin ? new Thickness(0, -7, 0, 0) : new Thickness(0, 0, 0, -7),
                Cursor = Cursors.Cross,
                ToolTip = "Drag to another node to link them",
            });
        }
        box.SizeChanged += (_, _) =>
        {
            PlaceLinksOf(view);
            if (_editing == view) PlaceToolbar();
        };

        Canvas.SetLeft(box, data.X);
        Canvas.SetTop(box, data.Y);
        _world.Children.Add(box);
        _nodes[id] = view;
        return view;
    }

    private void AddTextParts(NodeView view, Grid parts)
    {
        // Always there, and only lets itself be typed in while it's being edited, so a drag moves the node.
        var rich = new RichTextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = (Brush)FindResource("TextStrong"),
            CaretBrush = (Brush)FindResource("TextStrong"),
            FontSize = TextSize,
            AcceptsReturn = true,
            AcceptsTab = true, // Tab moves a list item in a level.
            IsReadOnly = true,
            IsHitTestVisible = false,
            Focusable = false,
        };
        System.Windows.Automation.AutomationProperties.SetName(rich, "Node text");
        rich.Resources.Add(typeof(Paragraph), new Style(typeof(Paragraph)) { Setters = { new Setter(Block.MarginProperty, new Thickness(0)) } });
        ShowText(rich.Document, view.Data.Text);

        var empty = new TextBlock
        {
            Text = "Text",
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = TextSize,
            Margin = new Thickness(5, 0, 0, 0), // Where the document starts its words.
            IsHitTestVisible = false,
            Visibility = string.IsNullOrEmpty(view.Data.Text) ? Visibility.Visible : Visibility.Collapsed,
        };
        view.Rich = rich;
        view.Empty = empty;
        view.Inside.Child = new Grid { Children = { rich, empty } };
        view.Box.Width = Math.Clamp(view.Data.Width ?? TextWidth, NarrowestText, WidestText);
        view.Box.MinHeight = Math.Clamp(view.Data.Height ?? 0, 0, TallestText);

        rich.PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key == Key.Escape)
            {
                pressed.Handled = true;
                StopEditing(keep: false);
                Focus();
            }
            else if (pressed.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                pressed.Handled = EnterPressed(rich);
            }
            else if (pressed.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                pressed.Handled = true;
                AskForLink(rich);
            }
        };
        rich.PreviewTextInput += (_, typed) => SpaceTyped(rich, typed);
        rich.TextChanged += (_, _) =>
        {
            // After the change is over, as the words can't be changed again from inside it.
            if (_editing == view && !_formatting) Dispatcher.BeginInvoke(() => FormatAsTyped(rich));
        };
        rich.LostKeyboardFocus += (_, lost) =>
        {
            if (_editing == view && !IsInToolbar(lost.NewFocus)) StopEditing(keep: true);
        };

        // The right edge, the bottom edge and the corner between them, dragged to resize it.
        parts.Children.Add(new Border
        {
            Tag = ResizePart + "-w",
            Width = 10,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -5, 0),
            Cursor = Cursors.SizeWE,
        });
        parts.Children.Add(new Border
        {
            Tag = ResizePart + "-h",
            Height = 10,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -5),
            Cursor = Cursors.SizeNS,
        });
        parts.Children.Add(new Border
        {
            Tag = ResizePart + "-wh",
            Width = 14,
            Height = 14,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -5, -5),
            Cursor = Cursors.SizeNWSE,
        });
    }

    // Words arrive from other PCs, so ones that can't be read as a document are shown as they are.
    private static void ShowText(FlowDocument document, string? text)
    {
        document.Blocks.Clear();
        if (string.IsNullOrEmpty(text)) return;

        var all = new TextRange(document.ContentStart, document.ContentEnd);
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            all.Load(stream, DataFormats.Xaml);
        }
        catch (Exception error) when (error is ArgumentException or System.Windows.Markup.XamlParseException or System.Xml.XmlException)
        {
            all.Text = text;
        }
    }

    // Nothing when there are no words, so an emptied node is saved as an empty one.
    private static string TextOf(FlowDocument document)
    {
        var all = new TextRange(document.ContentStart, document.ContentEnd);
        if (string.IsNullOrWhiteSpace(all.Text)) return "";

        using var stream = new MemoryStream();
        all.Save(stream, DataFormats.Xaml);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void SaveNode(NodeView node)
    {
        _database.SaveRecord(LocalDatabase.NodesCollection, node.Id, JsonSerializer.Serialize(node.Data, JsonOptions));
        _changed();
    }

    private void AddNodeAt(Point where, string kind)
    {
        var node = AddNodeView(NewId(), new NodeData(_board, Math.Round(where.X - 80), Math.Round(where.Y - 27), "", kind, kind == TextKind ? TextWidth : null));
        _hint.Visibility = Visibility.Collapsed;
        SaveNode(node);
        Select(node.Id);
        StartEditing(node);
    }

    private void StartEditing(NodeView node)
    {
        if (node.Rich is { } rich)
        {
            _editing = node;
            node.Empty!.Visibility = Visibility.Collapsed;
            rich.IsReadOnly = false;
            rich.IsHitTestVisible = true;
            rich.Focusable = true;
            Dispatcher.InvokeAsync(() =>
            {
                rich.Focus();
                rich.CaretPosition = rich.Document.ContentEnd;
                PlaceToolbar();
            }, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

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
        _editing = node;
        _editingBox = box;
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
        if (_editing is not { } node) return;
        var box = _editingBox;
        _editing = null; // First, as taking the box away makes it lose focus, which comes back here.
        _editingBox = null;

        if (node.Rich is { } rich)
        {
            rich.IsReadOnly = true;
            rich.IsHitTestVisible = false;
            rich.Focusable = false;
            var text = TextOf(rich.Document);
            if (!keep)
            {
                ShowText(rich.Document, node.Data.Text);
            }
            else if (text != (node.Data.Text ?? ""))
            {
                node.Data = node.Data with { Text = text };
                SaveNode(node);
            }
            node.Empty!.Visibility = string.IsNullOrEmpty(node.Data.Text) ? Visibility.Visible : Visibility.Collapsed;
            PlaceToolbar();
            return;
        }
        if (box is null || node.Title is null) return;

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

    // Where a pin is: the middle of the top or bottom edge. The bottom one when it has no such pin.
    private static Point PinOf(NodeView node, string? pin) =>
        new(node.Data.X + node.Box.ActualWidth / 2, node.Data.Y + (pin == TopPin && node.Pins.Contains(TopPin) ? 0 : node.Box.ActualHeight));

    private static string NearestPin(NodeView node, Point to) => node.Pins.MinBy(pin => (PinOf(node, pin) - to).Length)!;

    private void Place(LinkView link)
    {
        if (!_nodes.TryGetValue(link.Data.From, out var from) || !_nodes.TryGetValue(link.Data.To, out var to)) return;
        var (start, end) = (PinOf(from, link.Data.FromPin), PinOf(to, link.Data.ToPin));
        (link.Line.X1, link.Line.Y1, link.Line.X2, link.Line.Y2) = (start.X, start.Y, end.X, end.Y);
    }

    private void PlaceLinksOf(NodeView node)
    {
        foreach (var link in _links.Values.Where(link => link.Data.From == node.Id || link.Data.To == node.Id)) Place(link);
    }

    private void AddLink(NodeView from, string fromPin, NodeView to, string toPin)
    {
        // One link between two nodes, whichever way it was drawn.
        if (from == to || _links.Values.Any(link => (link.Data.From == from.Id && link.Data.To == to.Id) || (link.Data.From == to.Id && link.Data.To == from.Id))) return;

        var id = NewId();
        var data = new LinkData(_board, from.Id, to.Id, fromPin, toPin);
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
            node.Box.BorderBrush = selected ? (Brush)FindResource("Accent") : node.Rich is not null ? (Brush)FindResource("Line") : Brushes.Transparent;
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

    // A little past its edges counts, as its pins stick out.
    private NodeView? NodeAt(Point inWorld) =>
        _nodes.Values.LastOrDefault(node => new Rect(node.Data.X - 8, node.Data.Y - 8, node.Box.ActualWidth + 16, node.Box.ActualHeight + 16).Contains(inWorld));

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

        var onPart = (press.OriginalSource as FrameworkElement)?.Tag is string;
        if (node?.Rich is { } words && !onPart && ClickedInWords(node, words, press)) return;

        _dragging = false;
        _held = node;
        if (node is not null && press.OriginalSource is FrameworkElement { Tag: string part } && part.StartsWith(ResizePart, StringComparison.Ordinal))
        {
            Select(node.Id);
            _gesture = Gesture.Resize;
            _heldEdge = part;
            _heldFrom = new Point(node.Box.ActualWidth, node.Box.ActualHeight);
        }
        else if (node is not null && press.OriginalSource is Ellipse { Tag: string pin })
        {
            Select(node.Id);
            _heldPin = pin;
            _gesture = Gesture.Link;
            _newLink = new Line { Stroke = (Brush)FindResource("Accent"), StrokeThickness = 2.5, StrokeDashArray = [3, 2], IsHitTestVisible = false };
            var from = PinOf(node, pin);
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
            case Gesture.Resize when _held is { } resized:
                if (_heldEdge != ResizePart + "-h")
                {
                    var width = Math.Clamp(Math.Round(_heldFrom.X + inWorld.X - _pressedAtInWorld.X), NarrowestText, WidestText);
                    resized.Data = resized.Data with { Width = width };
                    resized.Box.Width = width;
                }
                if (_heldEdge != ResizePart + "-w")
                {
                    var height = Math.Clamp(Math.Round(_heldFrom.Y + inWorld.Y - _pressedAtInWorld.Y), ShortestText, TallestText);
                    resized.Data = resized.Data with { Height = height };
                    resized.Box.MinHeight = height;
                }
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

        if (gesture is Gesture.Move or Gesture.Resize && dragged && held is not null) SaveNode(held);
        if (gesture == Gesture.Link && held is not null && endedAt is { } at && NodeAt(at) is { } target) AddLink(held, _heldPin, target, NearestPin(target, at));
    }

    private void Zoomed(object sender, MouseWheelEventArgs wheel)
    {
        wheel.Handled = true; // Or the page behind would scroll.

        var view = _view.Matrix;
        var zoom = Math.Clamp(view.M11 * (wheel.Delta > 0 ? 1.1 : 1 / 1.1), SmallestZoom, LargestZoom);
        var at = wheel.GetPosition(this);
        view.ScaleAt(zoom / view.M11, zoom / view.M11, at.X, at.Y);
        _view.Matrix = view;
        PlaceToolbar();
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
