using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// The agents' window: conversations down the side, the open one in the middle, and the box to write
/// to it at the bottom, which also says what the agent will be told the person is looking at.
///
/// Built the way T3 Code builds its timeline. What the agent did between messages folds into one
/// line, opened to see each tool. Streamed words are put on screen every 50 ms rather than as each
/// piece arrives, and only the part of the timeline that changed is drawn again. Only the latest
/// turns are drawn, with earlier ones a click away, and it follows the end unless it's been
/// scrolled up.
/// </summary>
public sealed class AgentsWindow : Window
{
    private const int TurnsAtATime = 20;
    private const double FollowsWithin = 40;

    private readonly MainWindow _main;
    private readonly StackPanel _list = new();
    private readonly StackPanel _timeline = new();
    private readonly Border _earlier = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _sees = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 8), Visibility = Visibility.Collapsed };
    private readonly Border _composer;
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _seesTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private AgentConversation? _open;
    private int _turnsShown = TurnsAtATime;
    private bool _follow = true;
    private readonly HashSet<AgentItem> _changed = [];
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<string, Segment> _segmentOf = [];
    private readonly HashSet<string> _openTools = [];
    private readonly HashSet<Segment> _openGroups = [];
    private readonly Dictionary<string, Dictionary<string, string>> _choices = []; // Answers being picked, by question request.

    // A run of the timeline drawn as one thing: a message, a request, or the tools used between messages.
    private sealed class Segment
    {
        public required AgentItemKind Kind { get; init; }
        public List<AgentItem> Items { get; } = [];
        public FrameworkElement Element { get; set; } = new Border();
        public TextBox? Words { get; set; }
    }

    public AgentsWindow(MainWindow main)
    {
        _main = main;
        Title = "Agents";
        Width = 1040;
        Height = 720;
        MinWidth = 620;
        MinHeight = 420;
        Background = Brush("ContentBackground");
        _subtitle.Foreground = Brush("TextMuted");
        _sees.Foreground = Brush("TextMuted");
        _title.Foreground = Brush("TextStrong");

        // Down the side.
        var add = PrimaryButton("New conversation", NewConversation);
        add.Margin = new Thickness(12, 14, 12, 10);
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        var side = new DockPanel { Background = Brush("SidebarBackground") };
        DockPanel.SetDock(add, Dock.Top);
        side.Children.Add(add);
        side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(6, 0, 6, 12) });

        // The open conversation.
        var header = new Border
        {
            BorderBrush = Brush("Line"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 14, 24, 12),
            Child = new StackPanel { Children = { _title, _subtitle } },
        };
        var earlier = TextButton("Show earlier messages", () =>
        {
            _turnsShown += TurnsAtATime;
            DrawAll();
        });
        earlier.HorizontalAlignment = HorizontalAlignment.Center;
        _earlier.Child = earlier;
        _earlier.Margin = new Thickness(0, 0, 0, 8);
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { MaxWidth = 820, Margin = new Thickness(28, 16, 28, 24), Children = { _earlier, _timeline } },
        };

        // Scrolled up, it stays put. Back near the end, it follows again.
        _scroll.ScrollChanged += (_, changed) =>
        {
            if (changed.ExtentHeightChange == 0) _follow = _scroll.VerticalOffset >= _scroll.ScrollableHeight - FollowsWithin;
        };

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 44,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brush("TextStrong"),
            CaretBrush = Brush("TextStrong"),
            FontSize = 14,
            Padding = new Thickness(2, 2, 2, 6),
        };
        AutomationProperties.SetName(_input, "Message for the agent");
        _send = PrimaryButton("Send", SendOrStop);
        _send.VerticalAlignment = VerticalAlignment.Center;
        var bottom = new DockPanel();
        DockPanel.SetDock(_send, Dock.Right);
        bottom.Children.Add(_send);
        bottom.Children.Add(_sees);
        var box = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        box.Children.Add(bottom);
        box.Children.Add(_input);
        _composer = new Border
        {
            BorderBrush = Brush("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Background = Brush("SidebarBackground"),
            Padding = new Thickness(12, 10, 10, 10),
            Child = box,
        };
        var composerArea = new StackPanel { MaxWidth = 860, Margin = new Thickness(24, 0, 24, 18), Children = { _note, _composer } };

        var conversationArea = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(composerArea, Dock.Bottom);
        conversationArea.Children.Add(header);
        conversationArea.Children.Add(composerArea);
        conversationArea.Children.Add(_scroll);

        var layout = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(270) }, new ColumnDefinition() } };
        Grid.SetColumn(conversationArea, 1);
        layout.Children.Add(side);
        layout.Children.Add(conversationArea);
        Content = layout;

        _input.PreviewKeyDown += (_, pressed) =>
        {
            // Enter sends, and Shift+Enter starts a new line.
            if (pressed.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            pressed.Handled = true;
            if (_input.Text.Trim().Length > 0) SendOrStop();
        };
        _input.TextChanged += (_, _) => ShowSendButton();
        _flush.Tick += (_, _) => Flush();
        _seesTimer.Tick += (_, _) => ShowSees();
        Activated += (_, _) =>
        {
            ShowSees();
            _seesTimer.Start();
            if (_open is not null) _open.Unseen = false;
        };
        Deactivated += (_, _) => _seesTimer.Stop();
        Closed += (_, _) =>
        {
            Watch(null);
            _flush.Stop();
            _seesTimer.Stop();
        };

        ShowConversations();
        Show(_main.Conversations.OrderByDescending(conversation => conversation.Updated).FirstOrDefault());
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    internal bool IsLookingAt(AgentConversation conversation) => IsActive && _open == conversation;

    // Down the side

    /// <summary>Every conversation, by project, the latest first, with what each is doing.</summary>
    internal void ShowConversations()
    {
        _list.Children.Clear();
        var conversations = _main.Conversations;
        if (conversations.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No conversations yet.", Foreground = Brush("TextMuted"), FontSize = 12.5, Margin = new Thickness(8, 6, 8, 0) });
            return;
        }

        foreach (var project in conversations.GroupBy(conversation => conversation.ProjectId).OrderByDescending(group => group.Max(conversation => conversation.Updated)))
        {
            _list.Children.Add(new TextBlock { Text = _main.ProjectName(project.Key), Foreground = Brush("TextMuted"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 12, 8, 4), TextTrimming = TextTrimming.CharacterEllipsis });
            foreach (var conversation in project.OrderByDescending(conversation => conversation.Updated)) _list.Children.Add(ConversationRow(conversation));
        }
    }

    private FrameworkElement ConversationRow(AgentConversation conversation)
    {
        var (state, stateBrush) = conversation.IsWaiting ? ("Needs you", "Accent") : conversation.IsWorking ? ("Working", "Accent") : conversation.Unseen ? ("New", "TextStrong") : ("", "TextMuted");
        var row = new DockPanel();
        var said = new TextBlock { Text = state, FontSize = 11.5, Foreground = Brush(stateBrush), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(said, Dock.Right);
        row.Children.Add(said);
        row.Children.Add(new TextBlock { Text = conversation.Title.Length > 0 ? conversation.Title : "New conversation", TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush(conversation == _open ? "TextStrong" : "TextNormal") });

        var button = new Button { Style = (Style)FindResource("HeaderButton"), Content = row, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(button, $"{(conversation.Title.Length > 0 ? conversation.Title : "New conversation")}{(state.Length > 0 ? $", {state}" : "")}");
        button.Click += (_, _) => Show(conversation);
        button.MouseRightButtonUp += (_, click) =>
        {
            click.Handled = true;
            OfferToDelete(button, conversation);
        };
        return new Border { Background = conversation == _open ? Brush("SelectedBackground") : Brushes.Transparent, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 0, 1), Child = button };
    }

    private void OfferToDelete(UIElement by, AgentConversation conversation)
    {
        var menu = SmallMenu(by);
        var delete = TextButton("Delete this conversation", () =>
        {
            menu.IsOpen = false;
            if (!Dialogs.Confirm("Delete this conversation?\n\nWhat was said in it is gone from Agent Orange. Anything the agent changed stays changed.")) return;
            _main.DeleteConversation(conversation);
            if (_open == conversation) Show(_main.Conversations.OrderByDescending(each => each.Updated).FirstOrDefault());
            ShowConversations();
        });
        delete.Foreground = Brush("Danger");
        ((Border)menu.Child).Child = delete;
        menu.IsOpen = true;
    }

    private static Popup SmallMenu(UIElement by) => new()
    {
        PlacementTarget = by,
        Placement = PlacementMode.MousePoint,
        StaysOpen = false,
        AllowsTransparency = true,
        Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x32)),
            BorderBrush = Brush("ModalBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            MinWidth = 200,
        },
    };

    // A conversation about the project open in the main window, or one picked from a list.
    private void NewConversation()
    {
        if (_main.ProjectOnScreen is { } project)
        {
            Show(_main.NewConversation(project));
            ShowConversations();
            return;
        }

        var projects = _main.AgentProjects();
        var menu = SmallMenu((UIElement)_list.Parent);
        var rows = new StackPanel();
        rows.Children.Add(new TextBlock { Text = projects.Count == 0 ? "Make a project first." : "Which project is it about?", Foreground = Brush("TextMuted"), FontSize = 12, Margin = new Thickness(8, 4, 8, 6) });
        foreach (var each in projects)
        {
            var pick = TextButton(each.Name, () =>
            {
                menu.IsOpen = false;
                Show(_main.NewConversation(each));
                ShowConversations();
            });
            pick.HorizontalAlignment = HorizontalAlignment.Stretch;
            pick.HorizontalContentAlignment = HorizontalAlignment.Left;
            rows.Children.Add(pick);
        }
        ((Border)menu.Child).Child = rows;
        menu.IsOpen = true;
    }

    // The open conversation

    private void Watch(AgentConversation? conversation)
    {
        if (_open is not null)
        {
            _open.ItemChanged -= ItemChanged;
            _open.StateChanged -= WorkingChanged;
        }
        _open = conversation;
        if (_open is null) return;
        _open.ItemChanged += ItemChanged;
        _open.StateChanged += WorkingChanged;
    }

    private void Show(AgentConversation? conversation)
    {
        Watch(conversation);
        _turnsShown = TurnsAtATime;
        _openTools.Clear();
        _openGroups.Clear();
        if (conversation is not null) conversation.Unseen = false;

        _title.Text = conversation is null ? "No conversation open" : conversation.Title.Length > 0 ? conversation.Title : "New conversation";
        _subtitle.Text = conversation is null ? "Start one with New conversation." : _main.ProjectName(conversation.ProjectId);
        _composer.IsEnabled = conversation is not null;
        DrawAll();
        ShowSendButton();
        ShowConversations();
        ShowNote(null);
        _follow = true;
        ScrollToEnd();
        if (conversation is not null) _ = Dispatcher.InvokeAsync(() => _input.Focus(), DispatcherPriority.Input);
    }

    private void ShowSees() => _sees.Text = $"Sees {_main.ViewLabel()}";

    private void ShowNote(string? problem)
    {
        _note.Text = problem ?? "";
        _note.Foreground = Brush("Danger");
        _note.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowSendButton()
    {
        // While it works, typing something sends it on, and with nothing typed the button stops it.
        var stops = _open is { IsWorking: true } && _input.Text.Trim().Length == 0;
        _send.Content = stops ? "Stop" : "Send";
        AutomationProperties.SetName(_send, stops ? "Stop the agent" : "Send");
    }

    private async void SendOrStop()
    {
        if (_open is not { } conversation) return;
        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            if (conversation.IsWorking) conversation.Stop();
            return;
        }

        if (await _main.AgentProblemAsync(conversation) is { } problem)
        {
            ShowNote(problem);
            return;
        }
        ShowNote(null);
        _input.Clear();
        _follow = true;
        conversation.Send(text, _main.ViewLabel(), _main.DescribeView(), () => _main.PrepareAgent(conversation));
        if (_title.Text == "New conversation") _title.Text = conversation.Title;
        ShowConversations();
    }

    private void WorkingChanged()
    {
        ShowSendButton();
        if (_open is not null && IsActive) _open.Unseen = false;
    }

    // Changes are put on screen together, every 50 ms, rather than as each piece of a word arrives.
    private void ItemChanged(AgentItem item)
    {
        _changed.Add(item);
        if (!_flush.IsEnabled) _flush.Start();
    }

    private void Flush()
    {
        _flush.Stop();
        if (_open is null)
        {
            _changed.Clear();
            return;
        }
        var redraw = new HashSet<Segment>();
        foreach (var item in _open.Items.Where(_changed.Contains))
        {
            if (_segmentOf.TryGetValue(item.Id, out var segment))
            {
                // Only the words, for words still arriving.
                if (segment.Words is { } words && item.Kind == AgentItemKind.Assistant) words.Text = item.Text;
                else redraw.Add(segment);
            }
            else
            {
                Append(item, redraw);
            }
        }
        _changed.Clear();
        foreach (var segment in redraw) Draw(segment);
        if (_follow) ScrollToEnd();
    }

    private void ScrollToEnd() => _ = Dispatcher.InvokeAsync(_scroll.ScrollToEnd, DispatcherPriority.Loaded);

    // The latest turns, and a way to the earlier ones.
    private void DrawAll()
    {
        _timeline.Children.Clear();
        _segments.Clear();
        _segmentOf.Clear();
        _changed.Clear();
        if (_open is null)
        {
            _earlier.Visibility = Visibility.Collapsed;
            return;
        }

        var turns = _open.Items.Select((item, at) => (item, at)).Where(each => each.item.Kind == AgentItemKind.User).Select(each => each.at).ToList();
        var from = turns.Count > _turnsShown ? turns[^_turnsShown] : 0;
        _earlier.Visibility = from > 0 ? Visibility.Visible : Visibility.Collapsed;
        var redraw = new HashSet<Segment>();
        foreach (var item in _open.Items.Skip(from)) Append(item, redraw);
        foreach (var segment in redraw) Draw(segment);

        if (_open.Items.Count == 0)
        {
            _timeline.Children.Add(new TextBlock
            {
                Text = $"Ask for anything about {_main.ProjectName(_open.ProjectId)}. The agent does the work in the tools, and is told what you're looking at in Agent Orange each time you send something.",
                Foreground = Brush("TextMuted"),
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 24, 0, 0),
            });
        }
    }

    // Tools used one after another go together, between whatever comes before and after them.
    private void Append(AgentItem item, HashSet<Segment> redraw)
    {
        if (_segments.Count == 0 && _timeline.Children.Count > 0) _timeline.Children.Clear(); // The note for an empty conversation.
        if (item.Kind == AgentItemKind.Tool && _segments.LastOrDefault() is { Kind: AgentItemKind.Tool } tools)
        {
            tools.Items.Add(item);
            _segmentOf[item.Id] = tools;
            redraw.Add(tools);
            return;
        }

        var segment = new Segment { Kind = item.Kind };
        segment.Items.Add(item);
        _segments.Add(segment);
        _segmentOf[item.Id] = segment;
        _timeline.Children.Add(segment.Element);
        redraw.Add(segment);
    }

    private void Draw(Segment segment)
    {
        var at = _timeline.Children.IndexOf(segment.Element);
        segment.Words = null;
        var item = segment.Items[0];
        segment.Element = segment.Kind switch
        {
            AgentItemKind.User => UserMessage(item),
            AgentItemKind.Assistant => AssistantMessage(segment, item),
            AgentItemKind.Tool => ToolGroup(segment),
            AgentItemKind.Approval => Approval(item),
            AgentItemKind.Question => Questions(item),
            AgentItemKind.Problem => new TextBlock { Text = item.Text, Foreground = Brush("Danger"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) },
            _ => new TextBlock { Text = item.Text, Foreground = Brush("TextMuted"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) },
        };
        if (at >= 0) _timeline.Children[at] = segment.Element;
    }

    // Words that can be selected and copied, without looking like a field.
    private static TextBox Words(string text, string brush, double size) => new()
    {
        Text = text,
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        Foreground = Brush(brush),
        FontSize = size,
        Cursor = Cursors.IBeam,
    };

    private static FrameworkElement UserMessage(AgentItem item)
    {
        var said = new StackPanel { Children = { Words(item.Text, "TextStrong", 14) } };
        if (item.View is { Length: > 0 } view) said.Children.Add(new TextBlock { Text = $"Looking at {view}", Foreground = Brush("TextMuted"), FontSize = 11.5, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 620,
            Background = Brush("HoverBackground"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 9, 14, 10),
            Margin = new Thickness(60, 18, 0, 8),
            Child = said,
        };
    }

    private static FrameworkElement AssistantMessage(Segment segment, AgentItem item)
    {
        var words = Words(item.Text.Trim(), "TextNormal", 14);
        segment.Words = words;
        return new Border { Margin = new Thickness(0, 6, 0, 6), Child = words };
    }

    // One line for the lot while it's done, or the tool working now while it isn't. Opened, a line each.
    private FrameworkElement ToolGroup(Segment segment)
    {
        var running = segment.Items.LastOrDefault(tool => tool.ToolState == AgentToolState.Running && _open?.IsWorking == true);
        var opened = _openGroups.Contains(segment);
        var failed = segment.Items.Count(tool => tool.ToolState == AgentToolState.Failed);
        var said = running is not null ? running.Text : Summary(segment.Items) + (failed > 0 ? $" · {failed} failed" : "");

        var header = new Button { Style = (Style)FindResource("HeaderButton"), HorizontalAlignment = HorizontalAlignment.Left };
        header.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = opened ? "▾" : "▸", Foreground = Brush("TextMuted"), Margin = new Thickness(0, 0, 8, 0) },
                new TextBlock { Text = said, Foreground = Brush(running is not null ? "Accent" : "TextMuted"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 700 },
            },
        };
        AutomationProperties.SetName(header, $"{said}, {(opened ? "showing each tool" : "click to see each tool")}");
        header.Click += (_, _) =>
        {
            if (!_openGroups.Remove(segment)) _openGroups.Add(segment);
            Draw(segment);
        };

        var group = new StackPanel { Margin = new Thickness(-7, 2, 0, 2), Children = { header } };
        if (!opened) return group;
        foreach (var tool in segment.Items) group.Children.Add(ToolLine(segment, tool));
        return group;
    }

    private FrameworkElement ToolLine(Segment segment, AgentItem tool)
    {
        var mark = tool.ToolState switch { AgentToolState.Done => "✓", AgentToolState.Failed => "✕", _ => "…" };
        var line = new Button { Style = (Style)FindResource("HeaderButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 0, 0, 0) };
        line.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = mark, Width = 16, Foreground = Brush(tool.ToolState == AgentToolState.Failed ? "Danger" : "TextMuted") },
                new TextBlock { Text = tool.Text, Foreground = Brush("TextNormal"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 680 },
            },
        };
        line.Click += (_, _) =>
        {
            if (!_openTools.Remove(tool.Id)) _openTools.Add(tool.Id);
            Draw(segment);
        };
        if (!_openTools.Contains(tool.Id) || tool.Detail is not { Length: > 0 } detail) return line;

        var output = Words(detail, "TextMuted", 12);
        output.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        output.TextWrapping = TextWrapping.NoWrap;
        output.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        output.MaxHeight = 240;
        return new StackPanel
        {
            Children =
            {
                line,
                new Border { Background = Brush("SidebarBackground"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(41, 2, 0, 6), Child = output },
            },
        };
    }

    // Such as "Read 3 files, ran 2 commands", from the two kinds of tool used most.
    private static string Summary(List<AgentItem> tools)
    {
        static string KindOf(string name) => name switch
        {
            "Read" => "read",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "edit",
            "Bash" or "PowerShell" => "command",
            "Grep" or "Glob" => "search",
            "WebFetch" or "WebSearch" => "web",
            "TodoWrite" => "plan",
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) && name.Split("__") is [_, var server, ..] => $"mcp:{server}",
            _ => $"other:{name}",
        };
        static string Said(string kind, int count) => kind switch
        {
            "read" => count == 1 ? "read a file" : $"read {count} files",
            "edit" => count == 1 ? "edited a file" : $"made {count} edits",
            "command" => count == 1 ? "ran a command" : $"ran {count} commands",
            "search" => count == 1 ? "searched once" : $"searched {count} times",
            "web" => count == 1 ? "looked something up" : $"looked {count} things up",
            "plan" => "updated its plan",
            _ when kind.StartsWith("mcp:", StringComparison.Ordinal) => $"used {kind[4..]}{(count == 1 ? "" : $" {count} times")}",
            _ => $"used {kind[6..]}{(count == 1 ? "" : $" {count} times")}",
        };

        var kinds = tools.GroupBy(tool => KindOf(tool.Tool ?? "")).OrderByDescending(kind => kind.Count()).ToList();
        var said = string.Join(", ", kinds.Take(2).Select(kind => Said(kind.Key, kind.Count())));
        if (kinds.Count > 2) said += $" and {kinds.Skip(2).Sum(kind => kind.Count())} more";
        return char.ToUpperInvariant(said[0]) + said[1..];
    }

    private static Border Card(UIElement content) => new()
    {
        BorderBrush = Brush("Line"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16, 12, 16, 14),
        Margin = new Thickness(0, 8, 0, 8),
        Child = content,
    };

    // Something it wants to do that needs the person's yes first.
    private FrameworkElement Approval(AgentItem item)
    {
        var rows = new StackPanel();
        rows.Children.Add(new TextBlock { Text = $"Wants to use {item.Tool}", Foreground = Brush("TextMuted"), FontSize = 12 });
        rows.Children.Add(Words(item.Text, "TextStrong", 14));
        if (item.Detail is { Length: > 0 } detail && _openTools.Contains(item.Id))
        {
            var shown = Words(detail, "TextMuted", 12);
            shown.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            shown.MaxHeight = 220;
            shown.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            rows.Children.Add(new Border { Background = Brush("SidebarBackground"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 0), Child = shown });
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (item.RequestState == AgentRequestState.Waiting)
        {
            var allow = PrimaryButton("Allow", () => _open?.Answer(item, allow: true));
            var deny = ControlButton("Don't allow", () => _open?.Answer(item, allow: false));
            allow.Margin = deny.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(allow);
            buttons.Children.Add(deny);
        }
        else
        {
            buttons.Children.Add(new TextBlock
            {
                Text = item.RequestState switch { AgentRequestState.Allowed => "Allowed", AgentRequestState.Denied => "Not allowed", _ => "No longer waiting" },
                Foreground = Brush("TextMuted"),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            });
        }
        if (item.Detail is { Length: > 0 })
        {
            buttons.Children.Add(TextButton(_openTools.Contains(item.Id) ? "Hide details" : "Details", () =>
            {
                if (!_openTools.Remove(item.Id)) _openTools.Add(item.Id);
                if (_segmentOf.TryGetValue(item.Id, out var segment)) Draw(segment);
            }));
        }
        rows.Children.Add(buttons);
        var card = Card(rows);
        if (item.RequestState == AgentRequestState.Waiting) card.BorderBrush = Brush("Accent");
        return card;
    }

    // Its questions, each answered with one of its choices, several where it says so, or the person's own words.
    private FrameworkElement Questions(AgentItem item)
    {
        var rows = new StackPanel();
        var questions = item.Questions ?? [];
        if (item.RequestState != AgentRequestState.Waiting)
        {
            rows.Children.Add(new TextBlock { Text = item.RequestState == AgentRequestState.Answered ? "You answered" : "It asked, but is no longer waiting", Foreground = Brush("TextMuted"), FontSize = 12 });
            foreach (var question in questions)
            {
                rows.Children.Add(Words(question.Question, "TextNormal", 13.5));
                if (item.Answers?.GetValueOrDefault(question.Question) is { } answer) rows.Children.Add(Words(answer, "TextStrong", 13.5));
            }
            return Card(rows);
        }

        if (!_choices.TryGetValue(item.Id, out var chosen)) chosen = _choices[item.Id] = [];
        var send = PrimaryButton("Send answers", () =>
        {
            _open?.AnswerQuestions(item, new Dictionary<string, string>(chosen));
            _choices.Remove(item.Id);
        });
        send.HorizontalAlignment = HorizontalAlignment.Left;
        send.Margin = new Thickness(0, 12, 0, 0);
        void ShowReady() => send.IsEnabled = questions.All(question => chosen.GetValueOrDefault(question.Question) is { Length: > 0 });

        foreach (var question in questions)
        {
            if (question.Header.Length > 0) rows.Children.Add(new TextBlock { Text = question.Header, Foreground = Brush("Accent"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, rows.Children.Count == 0 ? 0 : 12, 0, 2) });
            rows.Children.Add(Words(question.Question, "TextStrong", 14));

            var picked = (chosen.GetValueOrDefault(question.Question) ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var options = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var option in question.Options)
            {
                var on = picked.Contains(option.Label);
                var choice = new Button
                {
                    Style = (Style)FindResource("ControlButton"),
                    Margin = new Thickness(0, 0, 8, 8),
                    ToolTip = option.Description.Length > 0 ? option.Description : null,
                    Content = new StackPanel
                    {
                        MaxWidth = 260,
                        Children =
                        {
                            new TextBlock { Text = option.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = option.Description, FontSize = 11.5, Foreground = Brush("TextMuted"), TextWrapping = TextWrapping.Wrap, Visibility = option.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed },
                        },
                    },
                };
                if (on) choice.BorderBrush = Brush("Accent");
                AutomationProperties.SetName(choice, on ? $"{option.Label}, chosen" : option.Label);
                choice.Click += (_, _) =>
                {
                    if (question.MultiSelect)
                    {
                        if (!picked.Remove(option.Label)) picked.Add(option.Label);
                        chosen[question.Question] = string.Join(", ", question.Options.Select(each => each.Label).Where(picked.Contains));
                    }
                    else
                    {
                        chosen[question.Question] = option.Label;
                    }
                    if (_segmentOf.TryGetValue(item.Id, out var segment)) Draw(segment);
                };
                options.Children.Add(choice);
            }
            rows.Children.Add(options);

            // Or in the person's own words.
            var own = Field(picked.Count == 0 ? chosen.GetValueOrDefault(question.Question) ?? "" : "");
            own.Width = double.NaN;
            AutomationProperties.SetName(own, $"Your own answer to: {question.Question}");
            own.TextChanged += (_, _) =>
            {
                if (own.Text.Trim().Length > 0) chosen[question.Question] = own.Text.Trim();
                ShowReady();
            };
            rows.Children.Add(new TextBlock { Text = "Or say something else", Foreground = Brush("TextMuted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 4) });
            rows.Children.Add(own);
        }
        rows.Children.Add(send);
        ShowReady();
        var card = Card(rows);
        card.BorderBrush = Brush("Accent");
        return card;
    }
}
