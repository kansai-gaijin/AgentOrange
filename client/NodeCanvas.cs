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
/// does. Double-click a node to change its words, drag it to move it, and drag from one of its
/// dots to another node to link them. Drag empty space to select the nodes in a box, and hold
/// Shift or Ctrl to add to what's selected, so they move together. Drag with the middle or the
/// right button to move around, and with Ctrl and the right button across links to cut them.
/// The wheel zooms, and Delete removes what's selected.
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
    // to be, and Height how tall at least, as it's never shorter than its words. Aspect is an Image
    // node's shape, its height over its width, so it's the right size before its picture arrives.
    // Card is the card a reference node stands for.
    private sealed record NodeData(string Board, double X, double Y, string Title, string? Kind = null, double? Width = null, string? Text = null, double? Height = null, double? Aspect = null, string? Card = null);

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
        public Image? Picture { get; set; } // An Image node's picture.
    }

    private sealed class LinkView
    {
        public required string Id { get; init; }
        public required LinkData Data { get; init; }
        public required Line Line { get; init; }
        public required Line Hit { get; init; } // Wider and unseen, as the line itself is too thin to click.
    }

    private enum Gesture { None, Pan, Move, Link, Resize, Box, Cut }

    private readonly LocalDatabase _database;
    private readonly string _projectId;
    private readonly string _board;
    private readonly Action _changed;

    private readonly Canvas _world = new();
    private readonly MatrixTransform _view = new();
    private readonly TextBlock _hint;
    private readonly Dictionary<string, NodeView> _nodes = [];
    private readonly Dictionary<string, LinkView> _links = [];
    private readonly HashSet<string> _selection = []; // Nodes' and links'.
    private readonly Dictionary<NodeView, Point> _movedFrom = []; // Where each node being dragged started.
    private readonly Border _band; // The box dragged out to select what's in it.
    private HashSet<string> _selectionBefore = [];
    private bool _adding; // Shift or Ctrl was held, so it adds to what's selected.
    private Polyline? _cut;

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
        _band = new Border
        {
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x5B, 0xD0, 0xD6)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Child = new Grid { Children = { _world, _hint, _band, _toolbar } };

        MouseLeftButtonDown += Pressed;
        MouseMove += Moved;
        MouseLeftButtonUp += Released;
        MouseDown += OtherPressed;
        MouseUp += (_, release) =>
        {
            if (release.ChangedButton != MouseButton.Left) EndGesture(ToWorld(release.GetPosition(this)));
        };
        LostMouseCapture += (_, _) => EndGesture(null);
        PreviewMouseWheel += Zoomed;
        KeyDown += KeyPressed;
        AllowDrop = true;
        DragOver += FilesDraggedOver;
        Drop += FilesDropped;
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
        ReadPictures();

        foreach (var (id, text) in _database.ListProjectRecords(LocalDatabase.NodesCollection, _projectId))
        {
            if (Parse<NodeData>(text) is { } data && data.Board == _board) AddNodeView(id, data);
        }
        foreach (var (id, text) in _database.ListProjectRecords(LocalDatabase.NodeLinksCollection, _projectId))
        {
            // One whose node was deleted on another PC at the same moment has nothing to join.
            if (Parse<LinkData>(text) is { } data && data.Board == _board && _nodes.ContainsKey(data.From) && _nodes.ContainsKey(data.To)) AddLinkView(id, data);
        }

        _selection.RemoveWhere(id => !_nodes.ContainsKey(id) && !_links.ContainsKey(id));
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
        var isImage = data.Kind == ImageKind;
        var reference = ReferenceTo(data.Kind);
        var inside = new Border { Padding = isImage || reference is not null ? new Thickness(0) : new Thickness(10, isText ? 12 : 6, 10, 12) };
        var parts = new Grid { Children = { inside } };
        var box = new Border
        {
            // Only the words show. See-through rather than nothing, so the space between them can still be clicked.
            Background = Brushes.Transparent,
            // A Rich Text or reference node has a border, to show its size. A Title node's shows only while it's selected.
            BorderBrush = isText || reference is not null ? (Brush)FindResource("Line") : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Child = parts,
        };
        var view = new NodeView { Id = id, Data = data, Box = box, Inside = inside, Pins = isText || isImage || reference is not null ? [TopPin, BottomPin] : [BottomPin] };
        box.Tag = view;

        if (isText)
        {
            AddTextParts(view, parts);
        }
        else if (isImage)
        {
            AddImageParts(view, parts);
        }
        else if (reference is { } to)
        {
            AddReferenceParts(view, to);
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

        AddGrips(parts);
    }

    private static void AddGrips(Grid parts)
    {
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
        if (kind == ImageKind)
        {
            AddImageFromFile(where);
            return;
        }

        var node = AddNodeView(NewId(), new NodeData(_board, Math.Round(where.X - 80), Math.Round(where.Y - 27), "", kind, kind == TextKind ? TextWidth : null));
        _hint.Visibility = Visibility.Collapsed;
        SaveNode(node);
        Select(node.Id);
        StartEditing(node);
    }

    private void StartEditing(NodeView node)
    {
        if (node.Picture is not null)
        {
            ReplaceImage(node);
            return;
        }

        // Once it's been laid out, as the list of cards opens under it, and once a double-click on it
        // is let go, as letting go of the mouse outside the list closes it.
        if (ReferenceTo(node.Data.Kind) is not null)
        {
            void Open() => Dispatcher.InvokeAsync(() => ChooseCard(node), System.Windows.Threading.DispatcherPriority.Loaded);
            void LetGo(object sender, MouseButtonEventArgs release)
            {
                PreviewMouseLeftButtonUp -= LetGo;
                Open();
            }
            if (Mouse.LeftButton == MouseButtonState.Pressed) PreviewMouseLeftButtonUp += LetGo;
            else Open();
            return;
        }

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
        var line = new Line { Stroke = (Brush)FindResource("TextMuted"), StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
        var hit = new Line { Stroke = Brushes.Transparent, StrokeThickness = 14, Cursor = Cursors.Hand, ToolTip = "Click to select, then Delete to remove" };
        var view = new LinkView { Id = id, Data = data, Line = line, Hit = hit };
        hit.Tag = view;
        _world.Children.Insert(0, line); // Under the nodes.
        _world.Children.Insert(0, hit);
        _links[id] = view;
        Place(view);
    }

    private void RemoveLink(LinkView link)
    {
        _links.Remove(link.Id);
        _selection.Remove(link.Id);
        _world.Children.Remove(link.Line);
        _world.Children.Remove(link.Hit);
        _database.SaveRecord(LocalDatabase.NodeLinksCollection, link.Id, "", deleted: true);
    }

    // Takes away every link the line drawn crosses.
    private void CutLinks(PointCollection along)
    {
        static double Side(Point a, Point b, Point c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        static bool Cross(Point a, Point b, Point c, Point d) => Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;

        var crossed = _links.Values.Where(link => Enumerable.Range(0, Math.Max(0, along.Count - 1)).Any(at =>
            Cross(along[at], along[at + 1], new Point(link.Line.X1, link.Line.Y1), new Point(link.Line.X2, link.Line.Y2)))).ToList();
        if (crossed.Count == 0) return;

        foreach (var link in crossed) RemoveLink(link);
        _changed();
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
        (link.Hit.X1, link.Hit.Y1, link.Hit.X2, link.Hit.Y2) = (start.X, start.Y, end.X, end.Y);
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
        _selection.Clear();
        if (id is not null) _selection.Add(id);
        ShowSelection();
    }

    private void Toggle(string id)
    {
        if (!_selection.Remove(id)) _selection.Add(id);
        ShowSelection();
    }

    private void ShowSelection()
    {
        foreach (var node in _nodes.Values)
        {
            var selected = _selection.Contains(node.Id);
            node.Box.BorderBrush = selected ? (Brush)FindResource("Accent") : node.Rich is not null || ReferenceTo(node.Data.Kind) is not null ? (Brush)FindResource("Line") : Brushes.Transparent;
        }
        foreach (var link in _links.Values)
        {
            var selected = _selection.Contains(link.Id);
            link.Line.Stroke = (Brush)FindResource(selected ? "Accent" : "TextMuted");
            link.Line.StrokeThickness = selected ? 3.5 : 2.5;
        }
    }

    private void DeleteSelected()
    {
        if (_selection.Count == 0) return;

        foreach (var id in _selection.ToList())
        {
            if (_links.TryGetValue(id, out var onlyLink))
            {
                RemoveLink(onlyLink);
            }
            else if (_nodes.Remove(id, out var node))
            {
                _world.Children.Remove(node.Box);
                _database.SaveRecord(LocalDatabase.NodesCollection, id, "", deleted: true);
                if (node.Picture is not null) ForgetPicture(id);
                foreach (var link in _links.Values.Where(link => link.Data.From == id || link.Data.To == id).ToList()) RemoveLink(link);
            }
        }
        _selection.Clear();
        _hint.Visibility = _nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _changed();
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
        if (_gesture != Gesture.None) return;
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
        _adding = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
        if (node is not null && press.OriginalSource is FrameworkElement { Tag: string part } && part.StartsWith(ResizePart, StringComparison.Ordinal))
        {
            Select(node.Id);
            _gesture = Gesture.Resize;
            _heldEdge = part;
            _heldFrom = node.Picture is null ? new Point(node.Box.ActualWidth, node.Box.ActualHeight) : new Point(node.Inside.ActualWidth, node.Inside.ActualHeight);
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
            if (_adding)
            {
                Toggle(node.Id);
                if (!_selection.Contains(node.Id))
                {
                    _held = null;
                    return;
                }
            }
            else if (!_selection.Contains(node.Id))
            {
                Select(node.Id); // One already among those selected keeps them, so they all move.
            }

            _gesture = Gesture.Move;
            _movedFrom.Clear();
            foreach (var id in _selection)
            {
                if (_nodes.TryGetValue(id, out var moved)) _movedFrom[moved] = new Point(moved.Data.X, moved.Data.Y);
            }
        }
        else if (link is not null)
        {
            if (_adding) Toggle(link.Id);
            else Select(link.Id);
            return;
        }
        else
        {
            if (!_adding) Select(null);
            _selectionBefore = [.. _selection];
            _gesture = Gesture.Box;
        }
        CaptureMouse();
    }

    // The middle and right buttons move around, and Ctrl with the right one cuts links.
    private void OtherPressed(object sender, MouseButtonEventArgs press)
    {
        if (press.ChangedButton == MouseButton.Left || _gesture != Gesture.None) return;
        if (_editing?.Rich is { IsMouseOver: true } || IsInToolbar(press.OriginalSource)) return; // Words being edited have a menu of their own.

        Focus();
        StopEditing(keep: true);
        _pressedAt = press.GetPosition(this);
        _pressedAtInWorld = ToWorld(_pressedAt);
        _dragging = false;
        _held = null;
        press.Handled = true;

        if (press.ChangedButton == MouseButton.Right && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _gesture = Gesture.Cut;
            _cut = new Polyline { Stroke = (Brush)FindResource("Danger"), StrokeThickness = 2, StrokeDashArray = [3, 2], IsHitTestVisible = false, Points = [_pressedAtInWorld] };
            _world.Children.Add(_cut);
        }
        else
        {
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
            case Gesture.Move:
                foreach (var (moved, from) in _movedFrom)
                {
                    moved.Data = moved.Data with { X = Math.Round(from.X + inWorld.X - _pressedAtInWorld.X), Y = Math.Round(from.Y + inWorld.Y - _pressedAtInWorld.Y) };
                    Canvas.SetLeft(moved.Box, moved.Data.X);
                    Canvas.SetTop(moved.Box, moved.Data.Y);
                    PlaceLinksOf(moved);
                }
                break;
            case Gesture.Resize when _held is { Picture: not null } pictured:
                ResizeImage(pictured, inWorld);
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
            case Gesture.Box:
                var onScreen = new Rect(_pressedAt, at);
                _band.Margin = new Thickness(onScreen.X, onScreen.Y, 0, 0);
                (_band.Width, _band.Height) = (onScreen.Width, onScreen.Height);
                _band.Visibility = Visibility.Visible;

                var area = new Rect(_pressedAtInWorld, inWorld);
                _selection.Clear();
                _selection.UnionWith(_selectionBefore);
                foreach (var inside in _nodes.Values.Where(one => area.IntersectsWith(new Rect(one.Data.X, one.Data.Y, one.Box.ActualWidth, one.Box.ActualHeight)))) _selection.Add(inside.Id);
                ShowSelection();
                break;
            case Gesture.Cut when _cut is { } cutting:
                cutting.Points.Add(inWorld);
                break;
        }
    }

    private void Released(object sender, MouseButtonEventArgs release) => EndGesture(ToWorld(release.GetPosition(this)));

    // Null for where it ended means it was cut short, such as by the window losing the mouse.
    private void EndGesture(Point? endedAt)
    {
        var (gesture, held, dragged, cut) = (_gesture, _held, _dragging, _cut);
        if (gesture == Gesture.None) return;
        _gesture = Gesture.None;
        _held = null;
        _dragging = false;
        _cut = null;
        _band.Visibility = Visibility.Collapsed;
        if (_newLink is { } line)
        {
            _world.Children.Remove(line);
            _newLink = null;
        }
        if (cut is not null) _world.Children.Remove(cut);
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (gesture == Gesture.Move)
        {
            if (dragged)
            {
                foreach (var moved in _movedFrom.Keys) _database.SaveRecord(LocalDatabase.NodesCollection, moved.Id, JsonSerializer.Serialize(moved.Data, JsonOptions));
                _changed();
            }
            else if (!_adding && held is not null && _selection.Count > 1)
            {
                Select(held.Id); // A plain click on one of several leaves just that one.
            }
            _movedFrom.Clear();
        }
        if (gesture == Gesture.Resize && dragged && held is not null) SaveNode(held);
        if (gesture == Gesture.Link && held is not null && endedAt is { } at && NodeAt(at) is { } target) AddLink(held, _heldPin, target, NearestPin(target, at));
        if (gesture == Gesture.Cut && endedAt is not null && cut is not null) CutLinks(cut.Points);
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
        else if (pressed.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            pressed.Handled = PasteImage();
        }
        else if (pressed.Key == Key.Delete)
        {
            DeleteSelected();
            pressed.Handled = true;
        }
        else if (pressed.Key == Key.A && Keyboard.Modifiers == ModifierKeys.None)
        {
            _selection.UnionWith(_nodes.Keys);
            ShowSelection();
            pressed.Handled = true;
        }
        else if (pressed.Key == Key.Escape)
        {
            Select(null);
            pressed.Handled = true;
        }
        else if (pressed.Key == Key.F2 && _selection.Count == 1 && _nodes.TryGetValue(_selection.First(), out var node))
        {
            StartEditing(node);
            pressed.Handled = true;
        }
    }
}
