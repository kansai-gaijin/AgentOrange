using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// The list Shift+A opens, at the mouse, to add a node there. Typing searches every kind of node at
/// once; otherwise kinds sit in groups that open to the side, the way Blender's add menu does.
/// The arrow keys move through it, Right opens a group, Left and Escape go back out of one.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const string PrimitivesGroup = "Primitives";
    private const string ReferencesGroup = "References";

    // Every kind there is, in the order the list shows them, and the group it's under, if any.
    private static readonly (string Kind, string Name, string? Group)[] Kinds =
    [
        (TitleKind, "Title", PrimitivesGroup),
        (TextKind, "Rich Text", PrimitivesGroup),
        (ImageKind, "Image", PrimitivesGroup),
        (BeatReferenceKind, "Story Beat", ReferencesGroup),
        (CharacterReferenceKind, "Character", ReferencesGroup),
        (LevelReferenceKind, "Level", ReferencesGroup),
        (MechanicReferenceKind, "Mechanic", ReferencesGroup),
        (AssetReferenceKind, "Asset", ReferencesGroup),
    ];

    // An asset's own board: a card on the Assets page. Its stage, the page, then the card.
    private bool IsAssetBoard => _board.Split('/') is [_, "assets", _];

    // To the right of whatever's there, along the same line, and moved into view if that's off the board.
    private Point FreeSpotNear(Point where)
    {
        const double gap = 40;
        for (var tries = 0; tries < 500; tries++)
        {
            var wanted = new Rect(where.X - 80, where.Y - 27, TextWidth, 60);
            var inTheWay = _nodes.Values.Select(node => new Rect(node.Data.X, node.Data.Y, node.Box.ActualWidth, node.Box.ActualHeight))
                .Where(taken => { var room = taken; room.Inflate(gap / 2, gap / 2); return room.IntersectsWith(wanted); })
                .ToList();
            if (inTheWay.Count == 0) break;
            where.X = inTheWay.Max(taken => taken.Right) + gap + 80;
        }

        var onScreen = _view.Matrix.Transform(where);
        if (onScreen.X < 0 || onScreen.X > ActualWidth - TextWidth || onScreen.Y < 0 || onScreen.Y > ActualHeight)
        {
            var view = _view.Matrix;
            // Only as far as it has to, so a row stays a row.
            var across = onScreen.X < 0 || onScreen.X > ActualWidth - TextWidth ? ActualWidth / 3 - onScreen.X : 0;
            var down = onScreen.Y < 0 || onScreen.Y > ActualHeight ? ActualHeight / 3 - onScreen.Y : 0;
            view.Translate(across, down);
            _view.Matrix = view;
        }
        return where;
    }

    private void OpenAddList()
    {
        // An asset's board doesn't take reference nodes.
        var kinds = Kinds.Where(each => each.Group != ReferencesGroup || !IsAssetBoard).ToArray();

        // Where the mouse is, or the middle if it's somewhere else, such as when Tab got here.
        var mouse = Mouse.GetPosition(this);
        // Over the board itself, not only within its bounds, as another window can be in front of it.
        var over = IsMouseOver && new Rect(0, 0, ActualWidth, ActualHeight).Contains(mouse);
        var where = ToWorld(over ? mouse : new Point(ActualWidth / 2, ActualHeight / 2));

        // Without the mouse to say where, the middle can already have a node, which the new one would cover.
        if (!over) where = FreeSpotNear(where);
#if DEBUG
        // The dev build's drive tool, which points where the person would with the mouse.
        if (DrivePointer is { } pointed) where = pointed;
#endif

        Brush muted = (Brush)FindResource("TextMuted");
        Border Panel(UIElement child) => MenuPanel(child);

        var search = new TextBox { Style = (Style)FindResource("Field"), Width = double.NaN, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(search, "Search nodes");
        var searchHint = new TextBlock { Text = "Search", Foreground = muted, FontSize = 13, Margin = new Thickness(11, 6, 0, 0), IsHitTestVisible = false };
        var items = new StackPanel();
        var main = Panel(new StackPanel { Children = { new Grid { Children = { search, searchHint } }, items } });

        // A group's kinds, to the right of the group, level with it.
        var groupItems = new StackPanel();
        var groupPanel = Panel(groupItems);
        groupPanel.Margin = new Thickness(4, 0, 0, 0);
        groupPanel.Visibility = Visibility.Collapsed;

        var list = new Popup
        {
            PlacementTarget = this,
            Placement = over ? PlacementMode.MousePoint : PlacementMode.Center,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { main, groupPanel } },
        };

        Button? openGroupRow = null;

        void Add(string kind)
        {
            list.IsOpen = false;
            AddNodeAt(where, kind);
        }

        Button Row(string name, string? aside, Action chosen) => MenuRow(name, aside, chosen);

        void CloseGroup()
        {
            if (openGroupRow is not null) openGroupRow.Foreground = (Brush)FindResource("TextNormal");
            openGroupRow = null;
            groupPanel.Visibility = Visibility.Collapsed;
        }

        void OpenGroup(string group, Button row, bool focusFirst)
        {
            if (openGroupRow != row)
            {
                CloseGroup();
                openGroupRow = row;
                row.Foreground = (Brush)FindResource("TextStrong");
                groupItems.Children.Clear();
                foreach (var (kind, name, _) in kinds.Where(each => each.Group == group)) groupItems.Children.Add(Row(name, null, () => Add(kind)));

                // Its first kind level with the group's row.
                var top = row.TranslatePoint(new Point(0, 0), main).Y - main.Padding.Top - main.BorderThickness.Top;
                groupPanel.Margin = new Thickness(4, Math.Max(0, top), 0, 0);
                groupPanel.Visibility = Visibility.Visible;
            }
            if (focusFirst) groupItems.Children.OfType<Button>().First().Focus();
        }

        // Kinds by group, when nothing's being searched for.
        void ShowGroups()
        {
            CloseGroup();
            items.Children.Clear();
            var shownGroups = new HashSet<string>();
            foreach (var (kind, name, group) in kinds)
            {
                if (group is null)
                {
                    var row = Row(name, null, () => Add(kind));
                    row.MouseEnter += (_, _) => CloseGroup();
                    items.Children.Add(row);
                }
                else if (shownGroups.Add(group))
                {
                    Button groupRow = null!;
                    groupRow = Row(group, "›", () => OpenGroup(group, groupRow, focusFirst: true));
                    groupRow.Tag = group;
                    groupRow.MouseEnter += (_, _) => OpenGroup(group, groupRow, focusFirst: false);
                    items.Children.Add(groupRow);
                }
            }
        }

        // Every kind whose name or group has what's typed, with its group beside it.
        void ShowMatches(string typed)
        {
            CloseGroup();
            items.Children.Clear();
            foreach (var (kind, name, group) in kinds)
            {
                if (name.Contains(typed, StringComparison.OrdinalIgnoreCase) || group?.Contains(typed, StringComparison.OrdinalIgnoreCase) == true)
                {
                    items.Children.Add(Row(name, group, () => Add(kind)));
                }
            }
            if (items.Children.Count == 0) items.Children.Add(new TextBlock { Text = "No nodes match", Foreground = muted, FontSize = 13, Margin = new Thickness(7, 4, 7, 4) });
        }

        search.TextChanged += (_, _) =>
        {
            searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (search.Text.Trim() is { Length: > 0 } typed) ShowMatches(typed);
            else ShowGroups();
        };

        // Typing anywhere in the list searches.
        list.PreviewTextInput += (_, typed) =>
        {
            if (search.IsKeyboardFocused || typed.Text.Length == 0 || char.IsControl(typed.Text[0])) return;
            search.Focus();
            search.Text += typed.Text;
            search.CaretIndex = search.Text.Length;
            typed.Handled = true;
        };

        list.PreviewKeyDown += (_, pressed) =>
        {
            var focused = Keyboard.FocusedElement as Button;
            var inGroup = focused is not null && groupItems.Children.Contains(focused);
            var buttons = (inGroup ? groupItems : items).Children.OfType<Button>().ToList();
            var at = focused is null ? -1 : buttons.IndexOf(focused);

            switch (pressed.Key)
            {
                case Key.Escape when inGroup:
                    var row = openGroupRow;
                    CloseGroup();
                    row?.Focus();
                    break;
                case Key.Escape:
                    list.IsOpen = false;
                    Focus();
                    break;
                case Key.Enter when search.IsKeyboardFocused:
                    // The first match, or into the first group.
                    items.Children.OfType<Button>().FirstOrDefault()?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    break;
                case Key.Down when search.IsKeyboardFocused:
                    if (buttons.Count == 0) return;
                    buttons[0].Focus();
                    break;
                case Key.Down when buttons.Count > 0:
                    buttons[(at + 1) % buttons.Count].Focus();
                    break;
                case Key.Up when !inGroup && at == 0:
                    search.Focus();
                    break;
                case Key.Up when buttons.Count > 0 && !search.IsKeyboardFocused:
                    buttons[(at + buttons.Count - 1) % buttons.Count].Focus();
                    break;
                case Key.Right when focused?.Tag is string group:
                    OpenGroup(group, focused, focusFirst: true);
                    break;
                case Key.Left when inGroup:
                    var from = openGroupRow;
                    CloseGroup();
                    from?.Focus();
                    break;
                default:
                    return;
            }
            pressed.Handled = true;
        };

        ShowGroups();
        // After the key that opened it is done with, so it isn't typed into the search.
        list.Opened += (_, _) => Dispatcher.BeginInvoke(() => search.Focus(), DispatcherPriority.Input);
        // However it closes, keys go back to the board, not to a list no longer there.
        list.Closed += (_, _) =>
        {
            if (Keyboard.FocusedElement is DependencyObject focused && list.Child.IsAncestorOf(focused)) Focus();
        };
        list.IsOpen = true;
    }

    // How the lists that open over the board look: a panel, and a row in it to pick.
    private Border MenuPanel(UIElement child) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x32)),
        BorderBrush = (Brush)FindResource("ModalBorder"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(6),
        MinWidth = 200,
        VerticalAlignment = VerticalAlignment.Top,
        Child = child,
    };

    private Button MenuRow(string name, string? aside, Action chosen)
    {
        var content = new DockPanel();
        if (aside is not null)
        {
            var side = new TextBlock { Text = aside, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(18, 0, 0, 0) };
            DockPanel.SetDock(side, Dock.Right);
            content.Children.Add(side);
        }
        content.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new Button { Style = (Style)FindResource("HeaderButton"), Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(row, name);
        row.Click += (_, _) => chosen();
        return row;
    }
}
