using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// The agents' window, made after T3 Code's chat, in its "ocean" dark theme and its Inter type:
/// conversations down the side with what each is doing, the open one's timeline, and the box to
/// write to it, which also says what the agent will be told the person is looking at.
///
/// As in T3 Code, each turn's work sits under a "Working for 12s" line while it goes, and folds
/// into "Worked for 1m 05s" once it's done, leaving the answer. Tools used one after another are
/// one line, opened to see each. Streamed words are put on screen every 50 ms, only the part of
/// the timeline that changed is drawn again, only the latest turns are drawn, and it follows the
/// end unless it's been scrolled up.
/// </summary>
public sealed partial class AgentsWindow : Window
{
    private const int TurnsAtATime = 20;
    private const double FollowsWithin = 40;
    private const double ColumnWidth = 736;
    private const double SidebarWidth = 256;
    private const double TopBar = 52;

    // Inter, bundled, as T3 Code bundles it, with Segoe UI for anything it lacks.
    internal static readonly FontFamily Inter = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Inter, Segoe UI");

    private static readonly (string? Id, string Name)[] Models =
        [(null, "Default model"), ("claude-opus-5-5", "Opus 5.5"), ("claude-sonnet-5-5", "Sonnet 5.5"), ("claude-fable-5-2", "Fable 5.2"), ("claude-haiku-5-5", "Haiku 5.5")];

    // Claude Code's own permission modes, in the words the person sees.
    private static readonly (string Id, string Name, string Icon, string Meaning)[] Permissions =
    [
        ("default", "Ask first", "shield-question", "Asks before it edits a file or runs anything."),
        ("acceptEdits", "Edit freely", "square-pen", "Edits the project's files and uses the editors freely. Asks before anything else."),
        ("plan", "Plan only", "map", "Looks around and makes a plan, but changes nothing."),
        ("bypassPermissions", "Full access", "lock-open", "Does whatever it needs without asking. Only for work you'd trust it with alone."),
    ];

    private readonly MainWindow _main;
    private readonly StackPanel _list = new();
    private readonly StackPanel _settledList = new();
    private readonly TextBox _search;
    private readonly StackPanel _timeline = new();
    private readonly Border _pendingHandoff = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(48, 0, 48, 0) };
    private readonly Border _earlier = new();
    private readonly ScrollViewer _scroll;
    private readonly Button _jump;
    private readonly TextBox _input;
    private readonly TextBlock _placeholder;
    private readonly Button _send;
    private readonly StackPanel _controls = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _headerTools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sees = Ocean.Text("", Ocean.Secondary, 12);
    private readonly Border _note = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
    private readonly Border _composer;
    private readonly StackPanel _banners = new();
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private AgentConversation? _open;
    private int _turnsShown = TurnsAtATime;
    private bool _follow = true;
    private bool _showSettled;
    private readonly HashSet<AgentItem> _changed = [];
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<string, Segment> _segmentOf = [];
    private readonly HashSet<string> _openTools = [];
    private readonly HashSet<Segment> _openGroups = [];
    private readonly HashSet<string> _openTurns = []; // Turns unfolded, by the message that began them.
    private readonly Dictionary<string, Dictionary<string, string>> _choices = []; // Answers being picked, by question request.

    // A run of the timeline drawn as one thing: a message, a request, the tools used between messages,
    // or the line a turn's work sits under.
    private sealed class Segment
    {
        public required AgentItemKind Kind { get; init; }
        public List<AgentItem> Items { get; } = [];
        public FrameworkElement Element { get; set; } = new Border();
        public AgentMarkdown? Reply { get; set; }
        public bool IsTurnLine { get; init; }
        public TextBlock? TurnWords { get; set; }
        public FrameworkElement? Meta { get; set; }
        public bool WasStreaming { get; set; }
    }

    public AgentsWindow(MainWindow main)
    {
        _main = main;
        Resources.MergedDictionaries.Add(Ocean.Resources());
        Title = "Agents";
        Width = 1180;
        Height = 780;
        MinWidth = 760;
        MinHeight = 480;
        Background = Ocean.Canvas;
        Foreground = Ocean.Foreground;
        FontFamily = Inter;
        FontSize = 14;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        UseLayoutRounding = true;

        // Its own frame, as T3 Code has: the top bars are where it's dragged from, with its own buttons.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TopBar,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // Down the side. The name sits right of T3's sidebar toggle.
        LoadSidebar();
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(52, 0, 0, 0) };
        brand.Children.Add(Ocean.Text("Agent", Ocean.Foreground, 14, FontWeights.Medium));
        brand.Children.Add(Ocean.Text(" Orange", Ocean.MutedText, 14, FontWeights.Medium));
        var top = new Grid { Height = TopBar, Children = { brand } };

        _search = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ocean.Foreground,
            CaretBrush = Ocean.Foreground,
            SelectionBrush = Ocean.Focus,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_search, "Search conversations");
        var searchHint = Ocean.Text("Search", Ocean.SidebarMuted, 14, FontWeights.Medium);
        searchHint.IsHitTestVisible = false;
        _search.TextChanged += (_, _) =>
        {
            searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowConversations();
        };
        var searchIcon = AgentIcons.Line("search", 16, Ocean.SidebarIcon);
        searchIcon.Margin = new Thickness(0, 0, 8, 0);
        var searchRow = new DockPanel();
        DockPanel.SetDock(searchIcon, Dock.Left);
        searchRow.Children.Add(searchIcon);
        searchRow.Children.Add(new Grid { Children = { searchHint, _search } });
        var searchField = new Border { Height = 32, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 0, 8, 0), Background = Brushes.Transparent, Child = searchRow };
        searchField.MouseEnter += (_, _) => searchField.Background = Ocean.RowHover;
        searchField.MouseLeave += (_, _) => searchField.Background = _search.IsKeyboardFocusWithin ? Ocean.RowHover : Brushes.Transparent;
        _search.GotKeyboardFocus += (_, _) => searchField.Background = Ocean.RowHover;
        _search.LostKeyboardFocus += (_, _) => searchField.Background = Brushes.Transparent;
        var write = Ocean.IconButton("square-pen", NewConversation, "New conversation", 28, 16, Ocean.SidebarIcon, Ocean.RowHover);
        write.Margin = new Thickness(4, 0, 0, 0);
        var searchLine = new DockPanel { Margin = new Thickness(8, 0, 8, 4) };
        DockPanel.SetDock(write, Dock.Right);
        searchLine.Children.Add(write);
        searchLine.Children.Add(searchField);

        var usage = Ocean.IconButton("chart-no-axes-column", ShowUsagePage, "Usage", 32, 16, Ocean.SidebarIcon, Ocean.RowHover);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 8), Children = { usage } };
        BuildRowParts(footer);

        var sideScroll = BuildList();
        var side = new DockPanel { Background = Ocean.Sidebar };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(searchLine, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        side.Children.Add(top);
        side.Children.Add(searchLine);
        side.Children.Add(footer);
        side.Children.Add(sideScroll);
        var sideEdge = new Border { BorderBrush = Ocean.SidebarDivider, BorderThickness = new Thickness(0, 0, 1, 0), Child = side };

        // The open conversation's bar: where it is, and what can be done with it.
        var headerRow = new DockPanel();
        DockPanel.SetDock(_headerTools, Dock.Right);
        headerRow.Children.Add(_headerTools);
        headerRow.Children.Add(_crumbs);
        var header = new Border { Height = TopBar, Padding = new Thickness(20, 0, 150, 0), Background = Ocean.Canvas, Child = headerRow };
        _headerBar = header;

        var earlier = Ocean.Ghost(Ocean.Text("Show earlier messages", Ocean.MutedText, 12, FontWeights.Medium), () =>
        {
            _turnsShown += TurnsAtATime;
            DrawAll();
        }, 6, new Thickness(8, 4, 8, 4));
        earlier.HorizontalAlignment = HorizontalAlignment.Center;
        _earlier.Child = earlier;
        _earlier.Margin = new Thickness(0, 0, 0, 12);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { MaxWidth = ColumnWidth + 96, Margin = new Thickness(0, 16, 0, 24), Children = { _earlier, _timeline, _pendingHandoff } },
        };
        _timeline.Margin = new Thickness(48, 0, 48, 0);

        // Scrolled up, it stays put, with a way back to the end. Back near the end, it follows again.
        _jump = Ocean.IconButton("chevron-down", () =>
        {
            _follow = true;
            _scroll.ScrollToEnd();
        }, "Go to the latest", 32, 16, Ocean.Foreground);
        _jump.Background = Ocean.Glass;
        _jump.BorderBrush = Ocean.GlassBorder;
        _jump.BorderThickness = new Thickness(1);
        _jump.Template = Ocean.Template(16, Ocean.Accent);
        _jump.HorizontalAlignment = HorizontalAlignment.Center;
        _jump.VerticalAlignment = VerticalAlignment.Bottom;
        _jump.Margin = new Thickness(0, 0, 0, 12);
        _jump.Visibility = Visibility.Collapsed;
        _scroll.ScrollChanged += (_, changed) =>
        {
            if (changed.ExtentHeightChange == 0) _follow = _scroll.VerticalOffset >= _scroll.ScrollableHeight - FollowsWithin;
            _jump.Visibility = _follow || _scroll.ScrollableHeight <= 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        var timelineArea = new Grid { Children = { _scroll, _jump } };

        // The box: T3 Code's composer, its words two lines at least and 208 px at most.
        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 45.5,
            MaxHeight = 208,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ocean.Foreground,
            CaretBrush = Ocean.Foreground,
            SelectionBrush = Ocean.Focus,
            FontSize = 14,
            Padding = new Thickness(0),
        };
        TextBlock.SetLineHeight(_input, 22.75);
        TextBlock.SetLineStackingStrategy(_input, LineStackingStrategy.BlockLineHeight);
        AutomationProperties.SetName(_input, "Message for the agent");
        _placeholder = Ocean.Text("Ask anything. Paste or drop pictures and files", Ocean.Placeholder, 14, lineHeight: 22.75);
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Top;

        _send = Ocean.Ghost(new Border(), SendOrStop, 16, hover: Ocean.PrimaryHover);
        _send.Width = 32;
        _send.Height = 32;

        var attach = Ocean.IconButton("paperclip", PickFiles, "Send pictures or files (or paste, or drop them here)", 28, 16, Ocean.MutedText);
        var shoot = Ocean.IconButton("camera", () => _ = ShootAsync(), $"Take a screenshot for the agent ({ScreenshotHotkey.Keys}, from anywhere)", 28, 16, Ocean.MutedText);
        shoot.Margin = new Thickness(2, 0, 8, 0);
        var eye = AgentIcons.Line("eye", 14, Ocean.Secondary);
        eye.Margin = new Thickness(0, 0, 6, 0);
        var sees = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { eye, _sees } };
        sees.ToolTip = "What the agent is told you're looking at in Agent Orange, with each message";

        var footerRow = new DockPanel { Margin = new Thickness(8, 0, 8, 8), Height = 32, LastChildFill = true };
        DockPanel.SetDock(attach, Dock.Left);
        DockPanel.SetDock(shoot, Dock.Left);
        DockPanel.SetDock(_send, Dock.Right);
        DockPanel.SetDock(_controls, Dock.Right);
        _controls.Margin = new Thickness(0, 0, 8, 0);
        footerRow.Children.Add(attach);
        footerRow.Children.Add(shoot);
        footerRow.Children.Add(_send);
        footerRow.Children.Add(_controls);
        footerRow.Children.Add(sees);

        var body = new StackPanel { Margin = new Thickness(16, 16, 16, 8), Children = { _tray, new Grid { Children = { _placeholder, _input } } } };
        var surface = new DockPanel();
        DockPanel.SetDock(footerRow, Dock.Bottom);
        surface.Children.Add(footerRow);
        surface.Children.Add(body);
        _composer = new Border
        {
            Background = Ocean.Composer,
            BorderBrush = Ocean.ComposerOutline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = surface,
        };
        Panel.SetZIndex(_composer, 2);
        var composerLane = new StackPanel { MaxWidth = ColumnWidth, Margin = new Thickness(48, 4, 48, 20), Children = { _note, _banners, _composer } };

        var conversationArea = new DockPanel { Background = Ocean.Canvas };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(composerLane, Dock.Bottom);
        conversationArea.Children.Add(header);
        conversationArea.Children.Add(composerLane);
        conversationArea.Children.Add(timelineArea);

        var layout = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(SidebarWidth) }, new ColumnDefinition() } };
        var chat = BuildRightPanel(conversationArea);
        Grid.SetColumn(chat, 1);
        layout.Children.Add(sideEdge);
        BuildSidebarEdge(layout, sideEdge, layout.ColumnDefinitions[0]);
        layout.Children.Add(chat);

        var caption = CaptionButtons();
        Grid.SetColumnSpan(caption, 2);
        layout.Children.Add(caption);
        Grid.SetColumnSpan(_overlay, 2);
        layout.Children.Add(_overlay);
        BuildDetailsParts(layout);
        Content = layout;
        StateChanged += (_, _) => layout.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        _input.PreviewKeyDown += (_, pressed) =>
        {
            // A picture or files on the clipboard go with the message, rather than in as words.
            if (pressed.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PasteAttachments())
            {
                pressed.Handled = true;
                return;
            }

            // Enter sends, and Shift+Enter starts a new line.
            if (pressed.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            pressed.Handled = true;
            if (_input.Text.Trim().Length > 0 || _pending.Count > 0) SendOrStop();
        };
        _input.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowSendButton();
        };
        AllowDrop = true;
        DragOver += (_, dragged) =>
        {
            dragged.Effects = dragged.Data.GetDataPresent(DataFormats.FileDrop) && _open is not null ? DragDropEffects.Copy : DragDropEffects.None;
            dragged.Handled = true;
        };
        Drop += (_, dropped) =>
        {
            if (dropped.Data.GetData(DataFormats.FileDrop) is string[] files) AttachFiles(files);
        };
        PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key == Key.Escape && _overlay.Visibility == Visibility.Visible)
            {
                _overlay.Visibility = Visibility.Collapsed;
                pressed.Handled = true;
            }
        };
        _flush.Tick += (_, _) => Flush();
        _tick.Tick += (_, _) =>
        {
            ShowSees();
            ShowTurns();
            ShowConversationStates();
        };
        _tick.Start();
        Activated += (_, _) =>
        {
            ShowSees();
            if (_open is not null) _open.Unseen = false;
        };
        Closed += (_, _) =>
        {
            Watch(null);
            _flush.Stop();
            _tick.Stop();
        };

        BuildUsageParts();
        ShowSees();
        ShowConversations();
        Show(_main.Conversations.Where(each => !each.Settled).OrderByDescending(conversation => conversation.Updated).FirstOrDefault());
    }

    internal bool IsLookingAt(AgentConversation conversation) => IsActive && _open == conversation;

    // Minimise, maximise and close, as Windows draws them, in the top bar.
    private FrameworkElement CaptionButtons()
    {
        Button Caption(string glyph, string name, Action click, bool close = false)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = Ocean.Soft, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var button = Ocean.Ghost(icon, click, 0, hover: close ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
            button.Width = 46;
            button.Height = 40;
            button.ToolTip = name;
            AutomationProperties.SetName(button, name);
            if (close) button.MouseEnter += (_, _) => icon.Foreground = Brushes.White;
            if (close) button.MouseLeave += (_, _) => icon.Foreground = Ocean.Soft;
            return button;
        }
        var maximise = Caption("", "Maximise", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        StateChanged += (_, _) => ((TextBlock)maximise.Content).Text = WindowState == WindowState.Maximized ? "" : "";
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                Caption("", "Minimise", () => WindowState = WindowState.Minimized),
                maximise,
                Caption("", "Close", Close, close: true),
            },
        };
    }

    private static string Ago(DateTimeOffset when)
    {
        var gone = DateTimeOffset.UtcNow - when;
        if (gone < TimeSpan.FromMinutes(1)) return "now";
        if (gone < TimeSpan.FromHours(1)) return $"{(int)gone.TotalMinutes}m";
        if (gone < TimeSpan.FromDays(1)) return $"{(int)gone.TotalHours}h";
        return $"{(int)gone.TotalDays}d";
    }

    // As T3 Code says how long: 5s, 2m 05s, 1h 03m.
    private static string Took(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? $"{Math.Max(0, (int)span.TotalSeconds)}s"
        : span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s"
        : $"{(int)span.TotalHours}h {span.Minutes:00}m";

    // A project's mark: its first letter on a tile of its own colour, as T3 Code's monograms are.
    private static FrameworkElement Monogram(string name, double size)
    {
        var hue = (uint)name.Aggregate(17, (hash, ch) => unchecked(hash * 31 + ch)) % 360;
        var color = FromHue(hue);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.25),
            Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B)),
            Child = new TextBlock
            {
                Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
                FontFamily = Ocean.Mono,
                FontWeight = FontWeights.Bold,
                FontSize = size * 0.52,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static Color FromHue(double hue)
    {
        double s = 0.6, l = 0.66;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private bool IsClaude(AgentConversation conversation) => _main.AgentProviderFor(conversation)?.Kind != ProviderKind.Codex;

    // Down the side

    /// <summary>Every conversation, the latest first, each with its project and what it's doing. The settled ones wait at the bottom.</summary>
    internal void ShowConversations()
    {
        _list.Children.Clear();
        var search = _search.Text.Trim();
        bool Matches(AgentConversation conversation) => search.Length == 0
            || conversation.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || _main.ProjectName(conversation.ProjectId).Contains(search, StringComparison.OrdinalIgnoreCase);
        foreach (var empty in _main.Conversations.Where(each => each != _open && IsDraft(each)).ToList())
        {
            _main.DeleteConversation(empty); // A draft left behind, which T3 would never have listed.
        }
        var conversations = _main.Conversations.Where(each => !IsDraft(each) && Matches(each)).ToList();

        var active = conversations.Where(each => !each.Settled).OrderByDescending(each => each.Updated).ToList();
        if (active.Count == 0)
        {
            var none = Ocean.Wrapped(search.Length > 0 ? "Nothing matches." : "No conversations yet. Start one with the pen above.", Ocean.SidebarMuted, 12.5, 18);
            none.Margin = new Thickness(10, 8, 10, 0);
            _list.Children.Add(none);
        }
        _statusSlots.Clear();
        _rowOrder.Clear();
        _rowOrder.AddRange(active);
        for (var index = 0; index < active.Count; index++) _list.Children.Add(ConversationRow(active[index], index));

        ShowSettledShelf([.. conversations.Where(each => each.Settled).OrderByDescending(each => each.SettledAt ?? each.Updated)]);
    }

    // Its hover card: the title, the project and the model.
    private FrameworkElement ConversationTip(AgentConversation conversation, string name, string project)
    {
        StackPanel Row(FrameworkElement icon, string text)
        {
            icon.Margin = new Thickness(0, 0, 8, 0);
            return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 0, 0), Children = { icon, Ocean.Text(text, new SolidColorBrush(Color.FromArgb(0xBF, 0xFF, 0xFA, 0xFF)), 12) } };
        }
        var model = ModelChoices(conversation).FirstOrDefault(each => each.Id == conversation.Model).Name ?? conversation.Model ?? "Default model";
        var glyph = AgentIcons.Provider(IsClaude(conversation), 12, Ocean.Foreground);
        glyph.Opacity = 0.6;
        return new StackPanel
        {
            Margin = new Thickness(2, 4, 2, 4),
            Children =
            {
                Ocean.Text(name, Ocean.Foreground, 12, FontWeights.Medium),
                Row(Monogram(project, 12), project),
                Row(glyph, model),
            },
        };
    }

    private void OfferActions(UIElement by, AgentConversation conversation)
    {
        Popup? menu = null;
        var settle = Ocean.MenuRow(Ocean.IconText(conversation.Settled ? "refresh-cw" : "check", conversation.Settled ? "Unsettle" : "Settle", Ocean.Foreground), () =>
        {
            menu!.IsOpen = false;
            if (!conversation.Settle(!conversation.Settled)) ShowNote("Stop it, or answer what it's asking, before settling it.");
            ShowHeader();
            ShowConversations();
        });
        settle.ToolTip = "Settled conversations are done with: put away at the bottom of the list, with their agent ended. Writing to one takes it back out.";
        var delete = Ocean.MenuRow(Ocean.IconText("x", "Delete", Ocean.Error), () =>
        {
            menu!.IsOpen = false;
            if (!Dialogs.Confirm("Delete this conversation?\n\nWhat was said in it is gone from Agent Orange. Anything the agent changed stays changed.")) return;
            _main.DeleteConversation(conversation);
            if (_open == conversation) Show(_main.Conversations.Where(each => !each.Settled).OrderByDescending(each => each.Updated).FirstOrDefault());
            ShowConversations();
        });
        var line = new Border { Height = 1, Background = Ocean.Border, Margin = new Thickness(8, 4, 8, 4) };
        menu = Ocean.Menu(by, PlacementMode.MousePoint, new StackPanel { Children = { settle, line, delete } });
        menu.IsOpen = true;
    }

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
        var rows = new StackPanel();
        Popup? menu = null;
        rows.Children.Add(new TextBlock { Text = projects.Count == 0 ? "Make a project first." : "Which project is it about?", Foreground = Ocean.MutedText, FontSize = 12, Margin = new Thickness(8, 6, 8, 6) });
        foreach (var each in projects)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var mark = Monogram(each.Name, 16);
            mark.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(mark);
            row.Children.Add(Ocean.Text(each.Name, Ocean.Foreground, 14));
            rows.Children.Add(Ocean.MenuRow(row, () =>
            {
                menu!.IsOpen = false;
                Show(_main.NewConversation(each));
                ShowConversations();
            }));
        }
        menu = Ocean.Menu(_list, PlacementMode.Relative, rows, 220);
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

    // Nothing has been said or sent in it yet, so it is only a draft, as T3 treats a new thread.
    private static bool IsDraft(AgentConversation conversation) => conversation.Items.Count == 0 && !conversation.IsWorking;

    private void Show(AgentConversation? conversation)
    {
        Watch(conversation);
        _redrawDetails?.Invoke();
        _turnsShown = TurnsAtATime;
        _openTools.Clear();
        _openGroups.Clear();
        _openTurns.Clear();
        if (conversation is not null) conversation.Unseen = false;

        ShowHeader();
        _composer.IsEnabled = conversation is not null;
        DrawAll();
        ShowSendButton();
        ShowControls();
        ShowConversations();
        ShowNote(null);
        _stepsOpen = conversation?.Steps?.Any(step => step.Status != "completed") == true;
        ShowUsage();
        _follow = true;
        ScrollToEnd();
        if (conversation is not null) _ = Dispatcher.InvokeAsync(() => _input.Focus(), DispatcherPriority.Input);
    }

    // The breadcrumb, as T3 Code's: the project, a slash, and the conversation's title.
    private void ShowHeader()
    {
        _crumbs.Children.Clear();
        _headerTools.Children.Clear();
        if (_open is not { } conversation)
        {
            _crumbs.Children.Add(Ocean.Text("Agents", Ocean.Foreground, 14, FontWeights.Medium));
            return;
        }
        var project = _main.ProjectName(conversation.ProjectId);
        var mark = Monogram(project, 14);
        mark.Margin = new Thickness(0, 0, 6, 0);
        var projectName = Ocean.Text(project, Ocean.MutedText, 14, FontWeights.Medium);
        projectName.MaxWidth = 160;
        _crumbs.Children.Add(mark);
        _crumbs.Children.Add(projectName);
        _crumbs.Children.Add(Ocean.Text("  /  ", Ocean.Secondary, 14));
        _crumbs.Children.Add(Ocean.Text(conversation.Title.Length > 0 ? OneLine(conversation.Title) : "New conversation", Ocean.Foreground, 14, FontWeights.Medium));

        // T3 Code's thread details toggle.
        _headerTools.Children.Add(DetailsToggle());


    }

    private void ShowSees() => _sees.Text = _main.ViewLabel();

    private void ShowNote(string? problem)
    {
        _note.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is null) return;
        _note.Background = Ocean.ErrorSurface;
        var words = Ocean.Wrapped(problem, Ocean.Error, 13, 20);
        var icon = AgentIcons.Line("circle-alert", 16, Ocean.Error);
        icon.Margin = new Thickness(0, 2, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(words);
        _note.Child = row;
    }

    // T3 Code's send button: a 32 px circle in the theme's action colour with its own arrow, or red with
    // a square to stop. While it works, something typed goes after what it's doing.
    private void ShowSendButton()
    {
        var typed = _input.Text.Trim().Length > 0 || _pending.Count > 0;
        var stops = _open is { IsWorking: true } && !typed;
        var can = stops || typed;
        _send.IsEnabled = can;
        _send.Background = stops ? Ocean.Destructive : can ? Ocean.Primary : Ocean.SendDisabled;
        _send.Template = Ocean.Template(16, stops ? Ocean.DestructiveHover : can ? Ocean.PrimaryHover : Ocean.SendDisabled);
        if (stops)
        {
            _send.Content = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(1.5), Background = Brushes.White };
        }
        else
        {
            var arrow = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M7 11.5V2.5M7 2.5L3 6.5M7 2.5L11 6.5"),
                Stroke = can ? Ocean.OnPrimary : Ocean.MutedText,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            _send.Content = new Canvas { Width = 14, Height = 14, Children = { arrow } };
        }
        AutomationProperties.SetName(_send, stops ? "Stop the agent" : "Send");
        _send.ToolTip = stops ? "Stop" : "Send (Enter)";
    }

    private async void SendOrStop()
    {
        if (_open is not { } conversation) return;
        var text = _input.Text.Trim();
        if (text.Length == 0 && _pending.Count == 0)
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
        var attachments = _pending.ToList();
        _pending.Clear();
        ShowTray();
        _follow = true;
        conversation.Send(text, attachments, _main.ViewLabel(), _main.DescribeView(), () => _main.PrepareAgent(conversation));
        ShowHeader();
        ShowConversations();
        ShowTurns();
    }

    private void WorkingChanged()
    {
        ShowSendButton();
        ShowControls();
        ShowUsage();
        ShowTurns();
        if (_open is not null && IsActive) _open.Unseen = false;
        if (_follow) ScrollToEnd();
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
                // Only the words, for words still arriving, and of those only the block they're arriving in.
                if (segment.Reply is { } reply && item.Kind == AgentItemKind.Assistant) reply.Show(item.Text);
                else redraw.Add(segment);
            }
            else
            {
                Append(item, redraw);
            }
        }
        _changed.Clear();
        foreach (var segment in redraw) Draw(segment);
        ShowTurns();
        ShowQuestions();
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
            _timeline.Children.Add(Welcome("No conversation open", "Start one with the pen at the top of the list. It's about the project open in the main window."));
            ShowQuestions();
            return;
        }

        var turns = _open.Items.Select((item, at) => (item, at)).Where(each => each.item.Kind == AgentItemKind.User).Select(each => each.at).ToList();
        var from = turns.Count > _turnsShown ? turns[^_turnsShown] : 0;
        _earlier.Visibility = from > 0 ? Visibility.Visible : Visibility.Collapsed;
        var redraw = new HashSet<Segment>();
        foreach (var item in _open.Items.Skip(from)) Append(item, redraw);
        foreach (var segment in redraw) Draw(segment);
        ShowTurns();
        ShowQuestions();

        if (_open.Items.Count == 0)
        {
            _timeline.Children.Add(Welcome("What should we make?", $"Ask for anything about {_main.ProjectName(_open.ProjectId)}. The agent does the work in the tools, and each time you send something it's told what you're looking at in Agent Orange."));
        }
    }

    private FrameworkElement Welcome(string title, string said)
    {
        var words = Ocean.Wrapped(said, Ocean.MutedText, 14);
        words.TextAlignment = TextAlignment.Center;
        words.MaxWidth = 460;
        words.Margin = new Thickness(0, 10, 0, 0);
        var heading = Ocean.Text(title, Ocean.Foreground, 24, FontWeights.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel { Margin = new Thickness(0, 140, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Children = { heading, words } };
    }

    // Tools used one after another go together, between whatever comes before and after them. A message
    // from the person begins a turn, with the line its work sits under right after it.
    private void Append(AgentItem item, HashSet<Segment> redraw)
    {
        if (_segments.Count == 0 && _timeline.Children.Count > 0) _timeline.Children.Clear(); // The welcome.
        if (item.Kind is AgentItemKind.Tool or AgentItemKind.Reasoning && _segments.LastOrDefault() is { Kind: AgentItemKind.Tool, IsTurnLine: false } tools)
        {
            tools.Items.Add(item);
            _segmentOf[item.Id] = tools;
            redraw.Add(tools);
            return;
        }

        var segment = new Segment { Kind = item.Kind == AgentItemKind.Reasoning ? AgentItemKind.Tool : item.Kind };
        segment.Items.Add(item);
        _segments.Add(segment);
        _segmentOf[item.Id] = segment;
        _timeline.Children.Add(segment.Element);
        redraw.Add(segment);

        if (item.Kind != AgentItemKind.User) return;
        var line = new Segment { Kind = AgentItemKind.Notice, IsTurnLine = true };
        line.Items.Add(item);
        line.Element = TurnLine(line);
        _segments.Add(line);
        _timeline.Children.Add(line.Element);
    }

    private void Draw(Segment segment)
    {
        if (segment.IsTurnLine) return;
        var at = _timeline.Children.IndexOf(segment.Element);
        var item = segment.Items[0];
        // A reply redraws only its words, unless the turn's changed-files card has come or gone under it.
        if (segment.Kind == AgentItemKind.Assistant && segment.Reply is { } reply && segment.Element is StackPanel shown && (item.Changes is { Count: > 0 }) == shown.Children.Count > 2 && item.Streaming == segment.WasStreaming)
        {
            reply.Show(item.Text);
            return;
        }
        var visibility = segment.Element.Visibility;
        segment.Element = segment.Kind switch
        {
            AgentItemKind.User => UserMessage(item),
            AgentItemKind.Assistant => AssistantMessage(segment, item),
            AgentItemKind.Tool => ToolGroup(segment),
            AgentItemKind.Approval => Approval(item),
            AgentItemKind.Question => AskedRow(item),
            AgentItemKind.Plan => PlanCard(item),
            AgentItemKind.Problem => Problem(item.Text),
            AgentItemKind.Handoff => HandoffRow(item.From, item.To, pending: false),
            AgentItemKind.Fork => ForkRow(item),
            _ => Notice(item.Text),
        };
        segment.Element.Visibility = visibility;
        if (at < 0) return;
        _timeline.Children.RemoveAt(at);
        _timeline.Children.Insert(at, segment.Element);
    }

    // Turns

    // Under the person's message: "Working for 12s" while the agent works, then "Worked for 1m 05s",
    // which folds away what it did on the way, leaving its answer, as T3 Code's turns do.
    private FrameworkElement TurnLine(Segment line)
    {
        var words = Ocean.Text("", Ocean.MutedText, 14, lineHeight: 22.75);
        line.TurnWords = words;
        var chevron = AgentIcons.Line("chevron-right", 14, Ocean.MutedText);
        chevron.Margin = new Thickness(4, 0, 0, 0);
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform(0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { words, chevron } };
        var button = Ocean.Ghost(row, () =>
        {
            var id = line.Items[0].Id;
            if (!_openTurns.Remove(id)) _openTurns.Add(id);
            ShowTurns();
        }, 6, new Thickness(4, 0, 4, 0));
        button.Height = 24;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Template = Ocean.Template(6, Brushes.Transparent);
        button.MouseEnter += (_, _) => words.Foreground = Ocean.Foreground;
        button.MouseLeave += (_, _) => words.Foreground = Ocean.MutedText;
        return new Border
        {
            BorderBrush = Ocean.Border60,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 4, 0, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = button,
            Visibility = Visibility.Collapsed,
        };
    }

    private void ShowTurns()
    {
        if (_open is null) return;
        var lastTurn = _segments.LastOrDefault(segment => segment.IsTurnLine);
        Segment? line = null;
        var inTurn = new List<Segment>();

        void Finish()
        {
            if (line is null) return;
            var current = line == lastTurn && _open.IsWorking;
            var answer = current ? null : inTurn.LastOrDefault(segment => segment.Kind == AgentItemKind.Assistant);
            var lastReply = inTurn.LastOrDefault(segment => segment.Kind == AgentItemKind.Assistant);
            foreach (var reply in inTurn.Where(segment => segment.Kind == AgentItemKind.Assistant))
            {
                if (reply.Element is FrameworkElement shown) shown.Margin = new Thickness(0, 0, 0, reply == lastReply ? 16 : 8);

                // T3 keeps the meta row showing under a finished turn's last reply.
                if (reply.Meta is { } meta)
                {
                    meta.Tag = reply == lastReply && !current;
                    meta.Opacity = meta.Tag is true ? 1 : 0;
                }
            }
            var hideable = inTurn.Where(segment => segment != answer && !NeedsPerson(segment) && segment.Kind is not (AgentItemKind.Problem or AgentItemKind.Handoff or AgentItemKind.Fork)).ToList();
            var started = line.Items[0].At;
            var ended = inTurn.Where(segment => segment.Kind is not (AgentItemKind.Handoff or AgentItemKind.Fork)).SelectMany(segment => segment.Items).Select(item => item.At).DefaultIfEmpty(started).Max();
            var element = (Border)line.Element;
            var button = (Button)element.Child;
            var chevron = (FrameworkElement)((StackPanel)button.Content).Children[1];

            if (current)
            {
                element.Visibility = Visibility.Visible;
                line.TurnWords!.Text = _open.IsWaiting ? "Waiting for you" : $"{_open.Retrying ?? "Working"} for {Took(DateTimeOffset.UtcNow - _open.WorkingSince)}";
                chevron.Visibility = Visibility.Collapsed;
                button.IsHitTestVisible = false;
                foreach (var segment in inTurn) segment.Element.Visibility = Visibility.Visible;
                return;
            }
            var unfolded = _openTurns.Contains(line.Items[0].Id);
            element.Visibility = hideable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            line.TurnWords!.Text = $"Worked for {Took(ended - started)}";
            chevron.Visibility = Visibility.Visible;
            ((RotateTransform)chevron.RenderTransform).Angle = unfolded ? 90 : 0;
            button.IsHitTestVisible = true;
            AutomationProperties.SetName(button, $"{line.TurnWords.Text}, {(unfolded ? "showing what it did" : "click to see what it did")}");
            foreach (var segment in inTurn) segment.Element.Visibility = unfolded || !hideable.Contains(segment) ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var segment in _segments)
        {
            if (segment.IsTurnLine)
            {
                Finish();
                line = segment;
                inTurn = [];
                continue;
            }
            if (segment.Kind == AgentItemKind.User) continue;
            inTurn.Add(segment);
        }
        Finish();
    }

    private static bool NeedsPerson(Segment segment) =>
        segment.Items.Any(item => item.RequestState == AgentRequestState.Waiting && item.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.Plan);

    // Messages

    private FrameworkElement UserMessage(AgentItem item)
    {
        var message = new StackPanel { Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (item.Attachments is { Count: > 0 } attachments)
        {
            var sent = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4), MaxWidth = ColumnWidth * 0.8 };
            foreach (var attachment in attachments)
            {
                var face = SentFace(attachment, attachments.Count(each => each.IsImage) == 1);
                face.Margin = new Thickness(4, 0, 0, 4);
                sent.Children.Add(face);
            }
            message.Children.Add(sent);
        }
        if (item.Text.Length > 0)
        {
            var words = new TextBox
            {
                Text = item.Text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = Ocean.Foreground,
                SelectionBrush = Ocean.Focus,
                FontSize = 14,
                Cursor = Cursors.IBeam,
            };
            TextBlock.SetLineHeight(words, 22.75);
            TextBlock.SetLineStackingStrategy(words, LineStackingStrategy.BlockLineHeight);
            message.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = ColumnWidth * 0.8,
                Background = Ocean.Message,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(12),
                Child = words,
            });
        }

        // Under it, faint until it's pointed at: when it was sent, and what the agent was told the person was looking at.
        var meta = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 4, 0) };
        if (item.Automatic)
        {
            meta.Children.Add(Ocean.IconText("refresh-cw", "Sent for you once the usage limit reset", Ocean.MutedText, 12, 12));
        }
        else
        {
            if (item.View is { Length: > 0 } view)
            {
                var seen = Ocean.IconText("eye", view, Ocean.MutedText, 12, 12, gap: 4);
                seen.ToolTip = "What you were looking at in Agent Orange, which the agent was told";
                meta.Children.Add(seen);
                meta.Children.Add(Ocean.Text("  ·  ", Ocean.MutedText, 12));
            }
            meta.Children.Add(Ocean.Text(item.At.ToLocalTime().ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture), Ocean.MutedText, 12));
        }
        meta.Opacity = item.Automatic ? 1 : 0;
        message.Children.Add(meta);
        message.Background = Brushes.Transparent;
        if (!item.Automatic)
        {
            message.MouseEnter += (_, _) => meta.Opacity = 1;
            message.MouseLeave += (_, _) => meta.Opacity = 0;
        }
        return message;
    }

    private FrameworkElement AssistantMessage(Segment segment, AgentItem item)
    {
        var reply = new AgentMarkdown(new AgentMarkdown.Look(
            Ocean.Body, Ocean.Foreground, Ocean.MutedText, Ocean.Foreground, Ocean.CodeBack, Ocean.InlineCode, Ocean.Border, Ocean.Info,
            Inter, Ocean.Mono, 14, 22.75) { Rule = Ocean.Border60, Hover = Ocean.Accent });
        reply.View.SelectionBrush = Ocean.Focus;
        reply.Preview = ShowLightbox;
        reply.PictureMenu = PictureMenu;
        segment.WasStreaming = item.Streaming;
        reply.Show(item.Text);
        segment.Reply = reply;
        // T3: px-1 py-0.5, with 8 under it, or 16 under a turn's last one (set as turns are laid out).
        var words = new Border { Padding = new Thickness(4, 2, 4, 2), Child = reply.View };
        if (_open is not { } conversation) return new Border { Margin = new Thickness(0, 0, 0, 8), Child = words };

        // T3's order: the words, the turn's changed files, then the copy, fork and time row.
        var whole = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent, Children = { words } };
        if (item.Changes is { Count: > 0 }) whole.Children.Add(ChangesCard(conversation, item));
        var meta = ReplyMeta(conversation, item);
        meta.Margin = new Thickness(4, 6, 4, 0);
        segment.Meta = meta;
        whole.Children.Add(meta);
        whole.MouseEnter += (_, _) => meta.Opacity = 1;
        whole.MouseLeave += (_, _) => meta.Opacity = meta.Tag is true ? 1 : 0;
        return whole;
    }

    // Work, as T3 Code's work log: a 24 px row with a 16 px icon in the secondary colour.

    private static string ToolIcon(string? name) => name switch
    {
        "Read" or "NotebookRead" => "eye",
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "square-pen",
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" => "terminal",
        "Grep" or "Glob" => "search",
        "WebFetch" or "WebSearch" => "globe",
        "TodoWrite" => "list-checks",
        "Task" or "Agent" => "bot",
        _ when name?.StartsWith("mcp__", StringComparison.Ordinal) == true => "hammer",
        _ => "wrench",
    };

    private FrameworkElement WorkRow(string icon, string words, Brush iconBrush, Action? click = null, bool live = false, bool chevron = false, bool opened = false, Brush? wordsBrush = null)
    {
        var glyph = AgentIcons.Line(icon, 16, iconBrush, 2);
        glyph.Opacity = iconBrush == Ocean.Secondary ? 0.7 : 1;
        var cell = new Grid { Width = 24, Height = 24, Children = { glyph } };
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var label = Ocean.Text(words, wordsBrush ?? Ocean.Secondary, 14, lineHeight: 22.75);
        if (live) Ocean.Shine(label, wordsBrush ?? Ocean.Secondary);
        var row = new DockPanel { MinHeight = 24 };
        DockPanel.SetDock(cell, Dock.Left);
        row.Children.Add(cell);
        if (chevron)
        {
            var mark = AgentIcons.Line("chevron-right", 12, Ocean.Secondary);
            mark.Opacity = 0.7;
            mark.Margin = new Thickness(6, 0, 0, 0);
            mark.RenderTransformOrigin = new Point(0.5, 0.5);
            mark.RenderTransform = new RotateTransform(opened ? 90 : 0);
            DockPanel.SetDock(mark, Dock.Right);
            row.Children.Add(mark);
        }
        label.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(label);
        if (click is null) return new Border { Padding = new Thickness(2), Child = row };
        var button = Ocean.Ghost(row, click, 6, new Thickness(2, 1, 2, 1), Brushes.Transparent);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.MouseEnter += (_, _) => { if (!live) label.Foreground = Ocean.Foreground; };
        button.MouseLeave += (_, _) => { if (!live) label.Foreground = wordsBrush ?? Ocean.Secondary; };
        AutomationProperties.SetName(button, words);
        return button;
    }

    // One line for the lot once they're done, or each, while they work. Opened, a line each, and each opens to what it gave back.
    private FrameworkElement ToolGroup(Segment segment)
    {
        var working = _open?.IsWorking == true && segment == _segments.LastOrDefault(each => !each.IsTurnLine);
        var opened = _openGroups.Contains(segment) || working;
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var tools = segment.Items.Where(each => each.Kind == AgentItemKind.Tool).ToList();

        // T3: a single entry stays a row of its own; a run of them is one line that opens.
        if (!working && segment.Items.Count == 1)
        {
            group.Children.Add(ToolLine(segment, segment.Items[0], working: false));
            return group;
        }
        if (!working)
        {
            var failed = tools.Count(tool => tool.ToolState == AgentToolState.Failed);
            var kinds = tools.Select(tool => ToolIcon(tool.Tool)).Distinct().ToList();
            var summary = Summary(segment.Items) + (failed > 0 ? $", {failed} failed" : "");
            var icon = tools.Count == 0 ? "brain" : kinds.Count == 1 ? kinds[0] : "hammer";
            group.Children.Add(WorkRow(icon, summary, Ocean.Secondary, () =>
            {
                if (!_openGroups.Remove(segment)) _openGroups.Add(segment);
                Draw(segment);
            }, chevron: true, opened: opened, wordsBrush: failed > 0 ? Ocean.Error : null));
        }
        if (!opened) return group;
        var list = new StackPanel { Margin = new Thickness(working ? 0 : 12, 0, 0, 0) };
        foreach (var item in segment.Items)
        {
            // While it works, its newest thought shows as words above a "Thinking" line, as T3's live row.
            if (working && item.Kind == AgentItemKind.Reasoning && item.Streaming && item == segment.Items[^1])
            {
                var thought = Ocean.Wrapped(Flatten(item.Text), Ocean.MutedText, 14, 22.75);
                thought.MaxHeight = 22.75 * 4;
                thought.TextTrimming = TextTrimming.CharacterEllipsis;
                thought.Margin = new Thickness(4, 0, 4, 4);
                if (item.Text.Trim().Length > 0) list.Children.Add(thought);
                list.Children.Add(WorkRow("brain", "Thinking", Ocean.Secondary, live: true));
                continue;
            }
            list.Children.Add(ToolLine(segment, item, working));
        }
        group.Children.Add(list);
        return group;
    }

    // Reasoning as one line of plain words: T3 flattens its markdown into a single truncated line.
    private static string Flatten(string markdown) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(markdown, @"[*_`#>\[\]]|\((https?://[^)]*)\)", ""), @"\s+", " ").Trim();

    private FrameworkElement ToolLine(Segment segment, AgentItem tool, bool working)
    {
        if (tool.Kind == AgentItemKind.Reasoning) return ThoughtLine(segment, tool);
        var running = working && tool.ToolState == AgentToolState.Running;
        var failed = tool.ToolState == AgentToolState.Failed;
        var words = tool.Text.Replace("  ", " ");
        var hasDetail = tool.Detail is { Length: > 0 };
        var row = WorkRow(failed ? "circle-x" : ToolIcon(tool.Tool), words, failed ? Ocean.Error : Ocean.Secondary, hasDetail ? () =>
        {
            if (!_openTools.Remove(tool.Id)) _openTools.Add(tool.Id);
            Draw(segment);
        } : null, live: running, chevron: hasDetail, opened: _openTools.Contains(tool.Id));
        if (!_openTools.Contains(tool.Id) || tool.Detail is not { Length: > 0 } detail) return row;
        var output = new TextBox
        {
            Text = detail,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = Ocean.Secondary,
            FontFamily = Ocean.Mono,
            FontSize = 13,
            MaxHeight = 256,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return new StackPanel { Children = { row, new Border { Margin = new Thickness(32, 2, 0, 6), Padding = new Thickness(0, 0, 0, 8), Child = output } } };
    }

    // T3's reasoning row: the brain and the thought on one line; opened, "Thought" (or "Thinking"
    // while it's still coming) with the whole of it below, at most 384 high.
    private FrameworkElement ThoughtLine(Segment segment, AgentItem thought)
    {
        var opened = _openTools.Contains(thought.Id);
        var row = WorkRow("brain", opened ? (thought.Streaming ? "Thinking" : "Thought") : Flatten(thought.Text), Ocean.Secondary, () =>
        {
            if (!_openTools.Remove(thought.Id)) _openTools.Add(thought.Id);
            Draw(segment);
        }, live: thought.Streaming && opened, chevron: true, opened: opened);
        if (!opened) return row;
        var reply = new AgentMarkdown(new AgentMarkdown.Look(
            Ocean.MutedText, Ocean.Body, Ocean.MutedText, Ocean.Foreground, Ocean.CodeBack, Ocean.InlineCode, Ocean.Border, Ocean.Info,
            Inter, Ocean.Mono, 14, 22.75) { Rule = Ocean.Border60, Hover = Ocean.Accent });
        reply.View.SelectionBrush = Ocean.Focus;
        reply.Show(thought.Text);
        var scroll = new ScrollViewer { MaxHeight = 384, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = reply.View, Margin = new Thickness(28, 4, 2, 6) };
        return new StackPanel { Children = { row, scroll } };
    }

    // T3's summary of a run of work, leaving its thinking out: "Ran 3 commands and read 1 file",
    // "Ran 2 commands, read 1 file, and performed 1 other action", or "Thought (×3)" when it only thought.
    private static string Summary(List<AgentItem> items)
    {
        static string KindOf(string name) => name switch
        {
            "Bash" or "PowerShell" or "BashOutput" => "command",
            "Read" or "NotebookRead" => "read",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "change",
            "WebFetch" or "WebSearch" => "web",
            _ => "tool",
        };
        static string Plural(int count, string one, string many) => count == 1 ? one : many;
        static string Said(string kind, int count) => kind switch
        {
            "command" => $"ran {count} {Plural(count, "command", "commands")}",
            "read" => $"read {count} {Plural(count, "file", "files")}",
            "change" => $"changed {count} {Plural(count, "file", "files")}",
            "web" => $"searched the web {count} {Plural(count, "time", "times")}",
            _ => $"used {count} {Plural(count, "tool", "tools")}",
        };

        var tools = items.Where(item => item.Kind == AgentItemKind.Tool).ToList();
        if (tools.Count == 0)
        {
            var thoughts = items.Count(item => item.Kind == AgentItemKind.Reasoning);
            return thoughts > 1 ? $"Thought (×{thoughts})" : "Thought";
        }
        var kinds = tools.GroupBy(tool => KindOf(tool.Tool ?? "")).Select(kind => (kind.Key, Count: kind.Count())).ToList();
        var parts = kinds.Take(2).Select(kind => Said(kind.Key, kind.Count)).ToList();
        var others = kinds.Skip(2).Sum(kind => kind.Count);
        var said = others > 0
            ? $"{string.Join(", ", parts)}, and performed {others} other {Plural(others, "action", "actions")}"
            : string.Join(" and ", parts);
        return char.ToUpperInvariant(said[0]) + said[1..];
    }

    private FrameworkElement Problem(string text)
    {
        var icon = AgentIcons.Line("circle-alert", 16, Ocean.Error);
        icon.Margin = new Thickness(4, 3, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 10) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(Ocean.Wrapped(text, Ocean.Error, 14));
        return row;
    }

    // T3 Code's context handoff row: a line either side of "Context handoff", and the login and model
    // it went on from, an arrow, and the one it went on with. "pending" until the next message is sent.
    private FrameworkElement HandoffRow(AgentEndpoint? from, AgentEndpoint? to, bool pending)
    {
        var middle = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 4, 8, 4) };
        var icon = AgentIcons.Line("arrow-right-left", 12, Ocean.MutedText);
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        middle.Children.Add(icon);
        middle.Children.Add(Ocean.Text(pending ? "Context handoff pending" : "Context handoff", Ocean.MutedText, 11, FontWeights.Medium));
        var chips = new WrapPanel { Opacity = 0.7, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (from is not null)
        {
            chips.Children.Add(EndpointChip(from));
            var arrow = AgentIcons.Line("arrow-right", 12, Ocean.MutedText);
            arrow.Margin = new Thickness(6, 0, 6, 0);
            arrow.VerticalAlignment = VerticalAlignment.Center;
            chips.Children.Add(arrow);
        }
        if (to is not null) chips.Children.Add(EndpointChip(to));
        middle.Children.Add(chips);

        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border { Height = 1, Background = Ocean.Border70, VerticalAlignment = VerticalAlignment.Center };
        var right = new Border { Height = 1, Background = Ocean.Border70, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        row.Children.Add(left);
        row.Children.Add(middle);
        row.Children.Add(right);
        AutomationProperties.SetName(row, $"{(pending ? "Context handoff pending" : "Context handoff")}{(from is null ? "" : $" from {from.Login}, {ModelName(from)}")}{(to is null ? "" : $" to {to.Login}, {ModelName(to)}")}");
        return row;
    }

    private FrameworkElement EndpointChip(AgentEndpoint endpoint)
    {
        var glyph = AgentIcons.Provider(endpoint.Claude, 12, Ocean.Foreground);
        glyph.Margin = new Thickness(0, 0, 6, 0);
        var model = ModelName(endpoint);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            ToolTip = $"{endpoint.Login} · {model}",
            Children = { glyph, Ocean.Text($"{endpoint.Login} · {model}", Ocean.MutedText, 11, FontWeights.Medium) },
        };
    }

    // A model's own name: Claude's from the list, Codex's as its login listed them, its default when none was picked.
    private static string ModelName(AgentEndpoint endpoint)
    {
        if (endpoint.Claude) return Models.FirstOrDefault(each => each.Id == endpoint.Model).Name ?? endpoint.Model ?? "Default model";
        var listed = AgentConversation.CodexModels.TryGetValue(endpoint.ProviderId, out var models) ? models : [];
        var model = endpoint.Model is null ? listed.FirstOrDefault(each => each.IsDefault) : listed.FirstOrDefault(each => each.Id == endpoint.Model);
        return model?.Name ?? endpoint.Model ?? "Default model";
    }

    // Under the timeline, before the next message: the switch of login it'll carry everything across.
    private void ShowPendingHandoff()
    {
        if (_open is { PendingFrom: { } from, PendingTo: { } to } conversation)
        {
            _pendingHandoff.Child = HandoffRow(from, to with { Model = conversation.Model }, pending: true);
            _pendingHandoff.Visibility = Visibility.Visible;
        }
        else
        {
            _pendingHandoff.Child = null;
            _pendingHandoff.Visibility = Visibility.Collapsed;
        }
    }

    private FrameworkElement Notice(string text)
    {
        var row = WorkRow("info", text, Ocean.Secondary);
        row.Margin = new Thickness(0, 0, 0, 8);
        if (row is Border { Child: DockPanel dock } && dock.Children.OfType<TextBlock>().FirstOrDefault() is { } words)
        {
            words.TextWrapping = TextWrapping.Wrap;
            words.TextTrimming = TextTrimming.None;
        }
        return row;
    }

    // Something it wants to do that needs the person's yes first, as T3 Code's approval row: the
    // command, then "Waiting for approval" with the answers as words to click.
    private FrameworkElement Approval(AgentItem item)
    {
        if (item.RequestState != AgentRequestState.Waiting)
        {
            var (icon, said, brush) = item.RequestState switch
            {
                AgentRequestState.Allowed => ("check", "Allowed", Ocean.Secondary),
                AgentRequestState.Denied => ("x", "Not allowed", Ocean.Error),
                _ => ("circle", "No longer waiting", Ocean.Secondary),
            };
            var answered = WorkRow(icon, $"{said}: {item.Text}", brush);
            answered.Margin = new Thickness(0, 0, 0, 8);
            return answered;
        }

        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var top = new DockPanel { MinHeight = 24 };
        var shield = AgentIcons.Line("shield", 16, Ocean.WarningStrong, 2);
        var cell = new Grid { Width = 24, Height = 24, Children = { shield } };
        shield.HorizontalAlignment = HorizontalAlignment.Center;
        DockPanel.SetDock(cell, Dock.Left);
        top.Children.Add(cell);
        var command = Ocean.Wrapped(item.Text, Ocean.Foreground, 12, 20);
        command.FontFamily = Ocean.Mono;
        command.Margin = new Thickness(6, 2, 0, 0);
        command.MaxHeight = 160;
        top.Children.Add(command);
        rows.Children.Add(new Border { Padding = new Thickness(2), Child = top });

        var actions = new WrapPanel { Margin = new Thickness(32, 2, 0, 0) };
        actions.Children.Add(Ocean.IconText("shield", "Waiting for approval", Ocean.WarningStrong, 12, 12, gap: 4));
        void Add(string words, Action click, bool strong, string? tip = null)
        {
            actions.Children.Add(Ocean.Text("  ·  ", Ocean.MutedText, 12));
            actions.Children.Add(InlineButton(words, click, strong, tip));
        }
        Add($"Allow {item.Tool}", () => _open?.Answer(item, allow: true), true);
        if (item.Suggestions is { Count: > 0 }) Add("Always allow", () => _open?.AllowAlways(item), true, "Allow it, and the same again for the rest of this conversation without asking");
        Add("Don't allow", () => _open?.Answer(item, allow: false), false);
        if (item.Detail is { Length: > 0 }) Add(_openTools.Contains(item.Id) ? "Hide details" : "Details", () =>
        {
            if (!_openTools.Remove(item.Id)) _openTools.Add(item.Id);
            if (_segmentOf.TryGetValue(item.Id, out var segment)) Draw(segment);
        }, false);
        rows.Children.Add(actions);
        if (item.Detail is { Length: > 0 } detail && _openTools.Contains(item.Id))
        {
            var shown = Ocean.Wrapped(detail, Ocean.Secondary, 12, 18);
            shown.FontFamily = Ocean.Mono;
            rows.Children.Add(new Border { Margin = new Thickness(32, 8, 0, 0), Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), Background = Ocean.CodeBack, BorderBrush = Ocean.Border, BorderThickness = new Thickness(1), Child = shown });
        }
        return rows;
    }

    // T3 Code's inline button: words that underline under the mouse.
    private static FrameworkElement InlineButton(string words, Action click, bool strong, string? tip = null)
    {
        var text = Ocean.Text(words, strong ? Ocean.Foreground : Ocean.MutedText, 12, FontWeights.Medium);
        text.Cursor = Cursors.Hand;
        text.ToolTip = tip;
        text.MouseEnter += (_, _) =>
        {
            text.TextDecorations = TextDecorations.Underline;
            if (!strong) text.Foreground = Ocean.Foreground;
        };
        text.MouseLeave += (_, _) =>
        {
            text.TextDecorations = null;
            if (!strong) text.Foreground = Ocean.MutedText;
        };
        text.MouseLeftButtonUp += (_, _) => click();
        AutomationProperties.SetName(text, words);
        return text;
    }

    // Its questions are answered above the box; here, a line for each once it's asked.
    private FrameworkElement AskedRow(AgentItem item)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (item.RequestState == AgentRequestState.Waiting)
        {
            rows.Children.Add(WorkRow("message-circle-question", "Asked you something, below", Ocean.Indigo, wordsBrush: Ocean.Indigo));
            return rows;
        }
        foreach (var question in item.Questions ?? [])
        {
            var answer = item.Answers?.GetValueOrDefault(question.Question);
            rows.Children.Add(WorkRow("message-circle-question", answer is null ? $"Asked: {question.Question}" : $"{question.Question}  →  {answer}", Ocean.Secondary));
        }
        return rows;
    }
}
