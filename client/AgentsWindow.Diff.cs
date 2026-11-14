using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// T3 Code's diff panel, copied from its code: a panel on the right of the conversation with a
/// "Diff" tab and a maximize button.
/// <list type="bullet">
/// <item>A 40 px header picks the scope: Changes (since the default branch), Uncommitted,
/// Latest turn, or any turn. For Changes it shows "head → base".</item>
/// <item>On the right: the totals, Refresh, collapse or expand every file, wrap and hide-whitespace.</item>
/// <item>Each file is a 32 px header (chevron, change icon, path, Copy path, −N +N) over its
/// lines. Files start collapsed, as T3's setting does, except the one asked for.</item>
/// <item>Lines are 13 on 20 in the code font, coloured as T3's pierre-dark theme, on about 6%
/// green or red, with a bar and the line number in the gutter. Hunks are joined by rows of
/// "N unmodified lines".</item>
/// <item>The panel is 540 px to begin with, 360 at least and 70% of the window at most, and its
/// width is remembered. Ctrl+D toggles it and Ctrl+W closes it.</item>
/// </list>
/// T3's split layout, file tree, commit scopes and line comments aren't here yet.
/// </summary>
public sealed partial class AgentsWindow
{
    private const double DiffDefaultWidth = 540;
    private const double DiffMinWidth = 360;

    private static readonly SolidColorBrush DiffAddLine = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0x10, 0xB9, 0x81)));
    private static readonly SolidColorBrush DiffRemoveLine = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFB, 0x41, 0x4A)));
    private static readonly SolidColorBrush DiffAddGutter = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0x10, 0xB9, 0x81)));
    private static readonly SolidColorBrush DiffRemoveGutter = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0xFB, 0x41, 0x4A)));
    private static readonly SolidColorBrush DiffAddBar = Freeze(new SolidColorBrush(Color.FromRgb(0x07, 0xC4, 0x80)));
    private static readonly SolidColorBrush DiffRemoveBar = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x2E, 0x3F)));
    private static readonly SolidColorBrush DiffModified = Freeze(new SolidColorBrush(Color.FromArgb(0xA6, 0x60, 0xA5, 0xFA)));
    private static readonly SolidColorBrush DiffNumber = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x78)));
    private static readonly SolidColorBrush DiffHunkBack = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)));
    private static readonly FontFamily DiffFont = new("Consolas, Cascadia Mono");

    private readonly Grid _chatAndPanel = new();
    private readonly ColumnDefinition _panelColumn = new() { Width = new GridLength(0) };
    private readonly Border _rightPanel = new() { Visibility = Visibility.Collapsed, BorderBrush = Ocean.Border, BorderThickness = new Thickness(1, 0, 0, 0), Background = Ocean.Canvas };
    private bool _panelMaximized;

    // What the panel shows: a scope, and the file to bring into view.
    private enum DiffScope { Turn, LatestTurn, Branch, Uncommitted }

    private DiffScope _diffScope = DiffScope.LatestTurn;
    private AgentItem? _diffTurn;
    private string? _diffReveal;
    private bool _diffWrap = true;
    private bool _diffHideWhitespace = true;
    private readonly HashSet<string> _diffOpenFiles = [];
    private readonly Dictionary<string, double> _panelWidths = [];

    private string PanelWidthsFile => Path.Combine(_main.AgentFolder, "panel-widths.json");

    private FrameworkElement BuildRightPanel(FrameworkElement conversationArea)
    {
        _chatAndPanel.ColumnDefinitions.Add(new ColumnDefinition());
        _chatAndPanel.ColumnDefinitions.Add(_panelColumn);
        Grid.SetColumn(_rightPanel, 1);
        _chatAndPanel.Children.Add(conversationArea);
        _chatAndPanel.Children.Add(_rightPanel);
        try
        {
            if (File.Exists(PanelWidthsFile) && JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(PanelWidthsFile)) is { } saved)
            {
                foreach (var (key, width) in saved) _panelWidths[key] = width;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't read the panel widths: {error.Message}");
        }

        // Ctrl+D toggles the diff, Ctrl+W closes the panel.
        PreviewKeyDown += (_, key) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBox) return;
            if (key.Key == Key.D && _open is { } open)
            {
                key.Handled = true;
                if (_rightPanel.Visibility == Visibility.Visible) ClosePanel();
                else ShowDiffPanel(open, null, null);
            }
            else if (key.Key == Key.W && _rightPanel.Visibility == Visibility.Visible)
            {
                key.Handled = true;
                ClosePanel();
            }
        };
        SizeChanged += (_, _) => SizePanel();
        return _chatAndPanel;
    }

    private double MostPanel() => Math.Max(DiffMinWidth, Math.Min(Math.Floor(ActualWidth * 0.7), _chatAndPanel.ActualWidth - DiffMinWidth));

    // The window's buttons sit over whichever is rightmost: the chat's header, or the right panel's tab bar.
    private void ShowHeaderRoom()
    {
        var panelOpen = _rightPanel.Visibility == Visibility.Visible;
        if (_headerBar is not null) _headerBar.Padding = new Thickness(_sidebar.Open ? 20 : 52, 0, panelOpen ? 20 : 150, 0);
    }

    private void SizePanel()
    {
        ShowHeaderRoom();
        if (_rightPanel.Visibility != Visibility.Visible)
        {
            _panelColumn.Width = new GridLength(0);
            return;
        }
        if (_panelMaximized)
        {
            _chatAndPanel.ColumnDefinitions[0].Width = new GridLength(0);
            _panelColumn.Width = new GridLength(1, GridUnitType.Star);
            return;
        }
        _chatAndPanel.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        var key = _open?.Id ?? "";
        var width = _panelWidths.TryGetValue(key, out var saved) ? saved : DiffDefaultWidth;
        _panelColumn.Width = new GridLength(Math.Clamp(width, DiffMinWidth, MostPanel()));
    }

    private void ClosePanel()
    {
        _rightPanel.Visibility = Visibility.Collapsed;
        _rightPanel.Child = null;
        _panelMaximized = false;
        SizePanel();
    }

    /// <summary>Opens the diff: a turn's (null for the latest), or the branch's when asked from the Changes row, with a file brought into view.</summary>
    private void ShowDiffPanel(AgentConversation conversation, AgentItem? turn, string? path, bool branch = false)
    {
        _diffScope = branch ? DiffScope.Branch : turn is null ? DiffScope.LatestTurn : DiffScope.Turn;
        _diffTurn = turn;
        _diffReveal = path;
        if (path is not null) _diffOpenFiles.Add(path);
        _rightPanel.Visibility = Visibility.Visible;
        SizePanel();
        DrawDiffPanel(conversation);
    }

    private void DrawDiffPanel(AgentConversation conversation)
    {
        var whole = new DockPanel();

        // The tab bar: T3's "Diff" tab, whose icon gives way to an X under the mouse, and maximize at the right.
        var tabIcon = new ContentControl { Content = AgentIcons.Line("file-diff", 12, Ocean.Foreground), Width = 16, Height = 16 };
        var tabClose = Ocean.Ghost(tabIcon, ClosePanel, 4, null, Ocean.RowHover);
        tabClose.Width = 16;
        tabClose.Height = 16;
        AutomationProperties.SetName(tabClose, "Close Diff");
        var tab = new Border
        {
            Height = 24,
            MaxWidth = 144,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 0, 8, 0),
            Background = Ocean.Accent,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { tabClose, WithLeftGap(Ocean.Text("Diff", Ocean.Foreground, 12), 2) } },
        };
        tab.MouseEnter += (_, _) => tabIcon.Content = AgentIcons.Line("x", 12, Ocean.Foreground);
        tab.MouseLeave += (_, _) => tabIcon.Content = AgentIcons.Line("file-diff", 12, Ocean.Foreground);
        var maximize = Ocean.Ghost(AgentIcons.Line(_panelMaximized ? "minimize-2" : "maximize-2", 16, Ocean.MutedText), () =>
        {
            _panelMaximized = !_panelMaximized;
            SizePanel();
            DrawDiffPanel(conversation);
        }, 8, null, Ocean.Accent);
        maximize.Width = 28;
        maximize.Height = 28;
        maximize.ToolTip = _panelMaximized ? "Restore panel size" : "Maximize panel";
        AutomationProperties.SetName(maximize, (string)maximize.ToolTip);
        var tabBar = new DockPanel { Height = TopBar, Margin = new Thickness(8, 0, 150, 0) };
        DockPanel.SetDock(maximize, Dock.Right);
        tabBar.Children.Add(maximize);
        tabBar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { tab } });
        DockPanel.SetDock(tabBar, Dock.Top);
        whole.Children.Add(tabBar);

        // The diff's own header: the scope, and on the right the totals and toggles.
        var files = new StackPanel();
        var totals = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var scope = Ocean.Ghost(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Ocean.Text(ScopeLabel(conversation), Ocean.Foreground, 12, FontWeights.Medium), WithLeftGap(AgentIcons.Line("chevron-down", 14, Ocean.MutedText), 4) },
        }, () => { }, 6, new Thickness(8, 0, 6, 0), Ocean.Accent, Ocean.Control);
        scope.Height = 24;
        AutomationProperties.SetName(scope, $"Diff scope: {ScopeLabel(conversation)}");
        scope.Click += (_, _) => PickScope(conversation, scope);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { scope } };
        if (_diffScope == DiffScope.Branch && _main.ProjectFolder(conversation.ProjectId) is { } projectFolder && RepoStates.TryGetValue(projectFolder, out var repo))
        {
            var refs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            refs.Children.Add(Ocean.Text(repo.Branch ?? "HEAD", Ocean.MutedText, 12));
            refs.Children.Add(WithLeftGap(AgentIcons.Line("arrow-right", 14, Ocean.MutedText), 4));
            refs.Children.Add(WithLeftGap(Ocean.Text(repo.DefaultBranch ?? "Automatic", Ocean.MutedText, 12), 4));
            refs.ToolTip = $"{repo.Branch ?? "HEAD"} → {repo.DefaultBranch ?? "Automatic"}";
            left.Children.Add(refs);
        }
        Button Toggle(string icon, string tip, bool pressed, Action click)
        {
            var button = Ocean.Ghost(AgentIcons.Line(icon, 14, pressed ? Ocean.Foreground : Ocean.MutedText), click, 6, null, Ocean.Accent, pressed ? Ocean.Accent : null);
            button.Width = 28;
            button.Height = 28;
            button.ToolTip = tip;
            AutomationProperties.SetName(button, tip);
            return button;
        }
        var allOpen = false;
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(totals);
        right.Children.Add(Toggle("refresh-cw", "Refresh diff", false, () => DrawDiffPanel(conversation)));
        Button? collapse = null;
        collapse = Toggle("chevrons-up-down", "Expand all files", false, () =>
        {
            allOpen = !allOpen;
            foreach (var section in files.Children.OfType<Expander>()) section.IsExpanded = allOpen;
            collapse!.Content = AgentIcons.Line(allOpen ? "chevrons-down-up" : "chevrons-up-down", 14, Ocean.MutedText);
            collapse.ToolTip = allOpen ? "Collapse all files" : "Expand all files";
        });
        right.Children.Add(collapse);
        right.Children.Add(Toggle("text-wrap", _diffWrap ? "Disable line wrapping" : "Enable line wrapping", _diffWrap, () =>
        {
            _diffWrap = !_diffWrap;
            DrawDiffPanel(conversation);
        }));
        right.Children.Add(Toggle("pilcrow", _diffHideWhitespace ? "Show whitespace changes" : "Hide whitespace changes", _diffHideWhitespace, () =>
        {
            _diffHideWhitespace = !_diffHideWhitespace;
            DrawDiffPanel(conversation);
        }));
        var header = new DockPanel { Height = 40, Margin = new Thickness(8, 0, 8, 0) };
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        header.Children.Add(left);
        var headerBar = new Border { BorderBrush = Ocean.Border60, BorderThickness = new Thickness(0, 1, 0, 1), Child = header };
        DockPanel.SetDock(headerBar, Dock.Top);
        whole.Children.Add(headerBar);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = files };
        whole.Children.Add(scroll);
        files.Children.Add(DiffMessage("Loading changes..."));

        // The edge: dragged left to widen the panel.
        var edgeLine = new Border { Width = 1, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center };
        var edge = new Border { Width = 8, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-4, 0, 0, 0), Cursor = Cursors.SizeWE, Child = edgeLine, Visibility = _panelMaximized ? Visibility.Collapsed : Visibility.Visible };
        edge.MouseEnter += (_, _) => edgeLine.Background = Ocean.Border;
        edge.MouseLeave += (_, _) => { if (!edge.IsMouseCaptured) edgeLine.Background = Brushes.Transparent; };
        double? startX = null;
        var startWidth = 0.0;
        var moved = false;
        edge.MouseLeftButtonDown += (_, press) =>
        {
            startX = press.GetPosition(this).X;
            startWidth = _panelColumn.ActualWidth;
            moved = false;
            edge.CaptureMouse();
            press.Handled = true;
        };
        edge.MouseMove += (_, move) =>
        {
            if (startX is not { } from || !edge.IsMouseCaptured) return;
            var now = move.GetPosition(this).X;
            if (!moved && Math.Abs(now - from) < 2) return;
            moved = true;
            edgeLine.Background = Ocean.Primary;
            var width = Math.Clamp(startWidth - (now - from), DiffMinWidth, MostPanel());
            _panelColumn.Width = new GridLength(width);
            _panelWidths[conversation.Id] = width;
        };
        edge.MouseLeftButtonUp += (_, _) =>
        {
            edge.ReleaseMouseCapture();
            startX = null;
            edgeLine.Background = edge.IsMouseOver ? Ocean.Border : Brushes.Transparent;
            if (!moved) return;
            try
            {
                File.WriteAllText(PanelWidthsFile, JsonSerializer.Serialize(_panelWidths));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Couldn't save the panel widths: {error.Message}");
            }
        };
        _rightPanel.Child = new Grid { Children = { whole, edge } };

        _ = LoadDiffAsync(conversation, files, totals, scroll);
    }

    private static FrameworkElement WithLeftGap(FrameworkElement element, double gap)
    {
        element.Margin = new Thickness(gap, 0, 0, 0);
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private static TextBlock DiffMessage(string words) => new()
    {
        Text = words,
        Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0x96, 0x9C, 0xA6)),
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(16, 48, 16, 16),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
    };

    // The turns with checkpoints, oldest first, numbered as T3 numbers them.
    private static List<AgentItem> CheckpointedTurns(AgentConversation conversation) =>
        [.. conversation.Items.Where(item => item.Kind == AgentItemKind.Assistant && item.CheckpointBefore is not null && item.CheckpointAfter is not null)];

    private string ScopeLabel(AgentConversation conversation) => _diffScope switch
    {
        DiffScope.Branch => "Changes",
        DiffScope.Uncommitted => "Uncommitted",
        DiffScope.Turn when _diffTurn is { } turn && CheckpointedTurns(conversation).IndexOf(turn) is >= 0 and var at => $"Turn {at + 1}",
        _ => "Latest turn",
    };

    // T3's scope menu: Changes, Uncommitted and Latest turn, then each turn with when it ended.
    private void PickScope(AgentConversation conversation, Button by)
    {
        var rows = new StackPanel();
        Popup? menu = null;
        void Item(string label, bool chosen, string? detail, Action pick)
        {
            var line = new DockPanel();
            if (detail is not null)
            {
                var when = Ocean.Text(detail, Ocean.MutedText, 12);
                when.Margin = new Thickness(16, 0, 0, 0);
                DockPanel.SetDock(when, Dock.Right);
                line.Children.Add(when);
            }
            var mark = AgentIcons.Line("check", 14, chosen ? Ocean.Foreground : Brushes.Transparent);
            mark.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(mark, Dock.Left);
            line.Children.Add(mark);
            line.Children.Add(Ocean.Text(label, Ocean.Foreground, 14));
            rows.Children.Add(Ocean.MenuRow(line, () =>
            {
                menu!.IsOpen = false;
                pick();
                DrawDiffPanel(conversation);
            }));
        }
        Item("Changes", _diffScope == DiffScope.Branch, null, () => _diffScope = DiffScope.Branch);
        Item("Uncommitted", _diffScope == DiffScope.Uncommitted, null, () => _diffScope = DiffScope.Uncommitted);
        Item("Latest turn", _diffScope == DiffScope.LatestTurn, null, () => _diffScope = DiffScope.LatestTurn);
        var turns = CheckpointedTurns(conversation);
        if (turns.Count > 0) rows.Children.Add(new Border { Height = 1, Background = Ocean.Border70, Margin = new Thickness(8, 4, 8, 4) });
        for (var at = turns.Count - 1; at >= 0; at--)
        {
            var turn = turns[at];
            Item($"Turn {at + 1}", _diffScope == DiffScope.Turn && _diffTurn == turn, turn.At.ToLocalTime().ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture), () =>
            {
                _diffScope = DiffScope.Turn;
                _diffTurn = turn;
            });
        }
        menu = Ocean.Menu(by, PlacementMode.Bottom, new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows }, 220, 4);
        menu.IsOpen = true;
    }

    private async Task LoadDiffAsync(AgentConversation conversation, StackPanel files, StackPanel totals, ScrollViewer scroll)
    {
        if (await _main.GitFolderAsync(conversation.ProjectId) is not { Folder: { } folder })
        {
            files.Children.Clear();
            files.Children.Add(DiffMessage("Turn diffs are unavailable because this project is not a git repository."));
            return;
        }
        string patch;
        switch (_diffScope)
        {
            case DiffScope.Branch:
                patch = await Task.Run(() => GitTools.BranchDiffAsync(folder, _diffHideWhitespace));
                break;
            case DiffScope.Uncommitted:
                patch = await Task.Run(() => GitTools.UncommittedDiffAsync(folder, _diffHideWhitespace));
                break;
            default:
                var turn = _diffScope == DiffScope.Turn ? _diffTurn : CheckpointedTurns(conversation).LastOrDefault();
                if (turn is null)
                {
                    files.Children.Clear();
                    files.Children.Add(DiffMessage("No completed turns yet."));
                    return;
                }
                patch = await Task.Run(() => GitTools.DiffBetweenAsync(folder, turn.CheckpointBefore!, turn.CheckpointAfter!, null, _diffHideWhitespace));
                break;
        }

        var parsed = ParsePatch(patch);
        files.Children.Clear();
        totals.Children.Clear();
        if (parsed.Count == 0)
        {
            files.Children.Add(DiffMessage("No net changes in this selection."));
            return;
        }
        totals.Children.Add(new TextBlock { Text = $"+{Count(parsed.Sum(file => file.Added))}", FontFamily = Ocean.Mono, FontSize = 11, Foreground = Addition, Margin = new Thickness(0, 0, 4, 0) });
        totals.Children.Add(new TextBlock { Text = $"−{Count(parsed.Sum(file => file.Removed))}", FontFamily = Ocean.Mono, FontSize = 11, Foreground = Deletion });
        FrameworkElement? revealed = null;
        foreach (var file in parsed)
        {
            var section = DiffFile(file);
            files.Children.Add(section);
            if (file.Path == _diffReveal) revealed = section;
        }
        if (revealed is not null)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            var at = revealed.TransformToAncestor(files).Transform(new Point(0, 0));
            scroll.ScrollToVerticalOffset(at.Y);
        }
    }

    private sealed record DiffLine(char Kind, string Text, int? Old, int? New);

    private sealed class DiffHunk
    {
        public int OldStart;
        public int OldLength;
        public readonly List<DiffLine> Lines = [];
    }

    private sealed class DiffFileInfo
    {
        public string Path = "";
        public string? OldPath;
        public string Change = "modified"; // new, deleted, renamed, modified
        public bool Binary;
        public int Added;
        public int Removed;
        public readonly List<DiffHunk> Hunks = [];
    }

    private static List<DiffFileInfo> ParsePatch(string patch)
    {
        var files = new List<DiffFileInfo>();
        DiffFileInfo? file = null;
        DiffHunk? hunk = null;
        int oldLine = 0, newLine = 0;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                file = new DiffFileInfo();
                var names = DiffNames().Match(line);
                file.Path = names.Success ? names.Groups[2].Value : line[11..];
                files.Add(file);
                hunk = null;
                continue;
            }
            if (file is null) continue;
            if (hunk is null)
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) file.Change = "new";
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) file.Change = "deleted";
                else if (line.StartsWith("rename from ", StringComparison.Ordinal)) { file.OldPath = line[12..]; file.Change = "renamed"; }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal)) file.Path = line[10..];
                else if (line.StartsWith("Binary files", StringComparison.Ordinal)) file.Binary = true;
            }
            if (HunkStart().Match(line) is { Success: true } start)
            {
                hunk = new DiffHunk { OldStart = int.Parse(start.Groups[1].Value), OldLength = start.Groups[2].Success ? int.Parse(start.Groups[2].Value) : 1 };
                oldLine = hunk.OldStart;
                newLine = int.Parse(start.Groups[3].Value);
                file.Hunks.Add(hunk);
                continue;
            }
            if (hunk is null || line.Length == 0 && raw.Length == 0) continue;
            switch (line.Length > 0 ? line[0] : ' ')
            {
                case '+':
                    hunk.Lines.Add(new DiffLine('+', line[1..], null, newLine++));
                    file.Added++;
                    break;
                case '-':
                    hunk.Lines.Add(new DiffLine('-', line[1..], oldLine++, null));
                    file.Removed++;
                    break;
                case ' ':
                    hunk.Lines.Add(new DiffLine(' ', line.Length > 0 ? line[1..] : "", oldLine++, newLine++));
                    break;
            }
        }
        return files;
    }

    // One file: T3's 32 px header over its lines, which start collapsed unless it's the one asked for.
    private Expander DiffFile(DiffFileInfo file)
    {
        var (icon, tone) = file.Change switch
        {
            "new" => ("file-plus", (Brush)Addition),
            "deleted" => ("file-minus", Deletion),
            "renamed" => ("file-symlink", DiffModified),
            _ => ("file-diff", DiffModified),
        };
        var chevron = AgentIcons.Line("chevron-right", 14, Ocean.MutedText);
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        var rotate = new RotateTransform(0);
        chevron.RenderTransform = rotate;
        chevron.Margin = new Thickness(0, 0, 6, 0);
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (file.OldPath is { } old)
        {
            name.Children.Add(new TextBlock { Text = old, Opacity = 0.7, FontSize = 12, Foreground = Ocean.Foreground, VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(WithLeftGap(AgentIcons.Line("arrow-right", 12, Ocean.MutedText), 4));
        }
        name.Children.Add(new TextBlock { Text = file.Path, FontSize = 12, Foreground = Ocean.Foreground, Margin = new Thickness(file.OldPath is null ? 0 : 4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file.Path });
        Button? copy = null;
        copy = Ocean.Ghost(AgentIcons.Line("copy", 12, Ocean.MutedText), () =>
        {
            try
            {
                Clipboard.SetText(file.Path);
                copy!.Content = AgentIcons.Line("check", 12, Ocean.MutedText);
                copy.ToolTip = "Copied";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program has the clipboard.
            }
        }, 4, null, Ocean.Accent);
        copy.Width = 20;
        copy.Height = 20;
        copy.Margin = new Thickness(4, 0, 0, 0);
        copy.ToolTip = "Copy path";
        AutomationProperties.SetName(copy, "Copy path");
        var counts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!(file.Change == "new" && file.Added == 0))
        {
            counts.Children.Add(new TextBlock { Text = $"−{file.Removed}", FontFamily = Ocean.Mono, FontSize = 11, Foreground = Deletion, Margin = new Thickness(0, 0, 6, 0) });
            counts.Children.Add(new TextBlock { Text = $"+{file.Added}", FontFamily = Ocean.Mono, FontSize = 11, Foreground = Addition });
        }
        var header = new DockPanel { Height = 32, Margin = new Thickness(8, 0, 12, 0) };
        DockPanel.SetDock(counts, Dock.Right);
        header.Children.Add(counts);
        var change = AgentIcons.Line(icon, 14, tone);
        change.Margin = new Thickness(0, 0, 6, 0);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { chevron, change, name, copy } });

        var body = new StackPanel();
        var section = new Expander
        {
            Header = header,
            Content = body,
            IsExpanded = _diffOpenFiles.Contains(file.Path),
            Template = DiffExpanderTemplate(),
        };
        AutomationProperties.SetName(section, file.Path);
        void Fill()
        {
            if (body.Children.Count > 0) return;
            if (file.Binary || file.Hunks.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = file.Binary ? "Binary file not shown." : "No content changes.", Foreground = Ocean.MutedText, FontSize = 11, Margin = new Thickness(36, 6, 12, 8) });
                return;
            }
            var language = Path.GetExtension(file.Path).TrimStart('.');
            var previousEnd = 1;
            foreach (var hunk in file.Hunks)
            {
                var unchanged = hunk.OldStart - previousEnd;
                if (unchanged > 0) body.Children.Add(Unmodified(unchanged));
                foreach (var line in hunk.Lines) body.Children.Add(DiffRow(line, language));
                previousEnd = hunk.OldStart + hunk.OldLength;
            }
        }
        section.Expanded += (_, _) =>
        {
            rotate.Angle = 90;
            _diffOpenFiles.Add(file.Path);
            Fill();
        };
        section.Collapsed += (_, _) =>
        {
            rotate.Angle = 0;
            _diffOpenFiles.Remove(file.Path);
        };
        if (section.IsExpanded)
        {
            rotate.Angle = 90;
            Fill();
        }
        return section;
    }

    // The header is the whole button, as T3's: clicking it opens or closes the file.
    private static ControlTemplate DiffExpanderTemplate()
    {
        var template = new ControlTemplate(typeof(Expander));
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetValue(ToggleButton.CursorProperty, Cursors.Hand);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(Expander.IsExpanded)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent, Mode = System.Windows.Data.BindingMode.TwoWay });
        var face = new ControlTemplate(typeof(ToggleButton));
        var faceBorder = new FrameworkElementFactory(typeof(Border), "Face");
        faceBorder.SetValue(Border.BackgroundProperty, Ocean.Canvas);
        faceBorder.SetValue(Border.BorderBrushProperty, Ocean.Border60);
        faceBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        faceBorder.AppendChild(presenter);
        face.VisualTree = faceBorder;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, Ocean.RowHover, "Face"));
        face.Triggers.Add(over);
        toggle.SetValue(Control.TemplateProperty, face);
        toggle.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(Expander.Header)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        stack.AppendChild(toggle);
        var content = new FrameworkElementFactory(typeof(ContentPresenter), "Body");
        content.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding(nameof(Expander.Content)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        content.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(Expander.IsExpanded)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent, Converter = new BooleanToVisibilityConverter() });
        stack.AppendChild(content);
        template.VisualTree = stack;
        return template;
    }

    // T3's hunk separator: 24 high, "N unmodified lines" in the sans font at 11.
    private static FrameworkElement Unmodified(int lines) => new Border
    {
        Height = 24,
        Background = DiffHunkBack,
        Child = new TextBlock { Text = $"{lines} unmodified {(lines == 1 ? "line" : "lines")}", FontSize = 11, Foreground = Ocean.MutedText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(44, 0, 0, 0) },
    };

    // A line: a 4 px bar and the line number in the gutter, then the code in its colours, on green or red when it changed.
    private FrameworkElement DiffRow(DiffLine line, string language)
    {
        var added = line.Kind == '+';
        var removed = line.Kind == '-';
        var bar = new Border { Width = 4, Background = added ? DiffAddBar : removed ? DiffRemoveBar : Brushes.Transparent };
        if (removed) bar.Background = new DrawingBrush(new GeometryDrawing(DiffRemoveBar, null, new RectangleGeometry(new Rect(0, 0, 4, 2)))) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 4), ViewportUnits = BrushMappingMode.Absolute };
        var number = new TextBlock
        {
            Text = (line.New ?? line.Old)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            FontFamily = DiffFont,
            FontSize = 13,
            Foreground = added ? DiffAddBar : removed ? DiffRemoveBar : DiffNumber,
            TextAlignment = TextAlignment.Right,
            Width = 36,
            Padding = new Thickness(0, 0, 8, 0),
        };
        var gutter = new StackPanel { Orientation = Orientation.Horizontal, Background = added ? DiffAddGutter : removed ? DiffRemoveGutter : Brushes.Transparent, Children = { bar, number } };
        var code = new TextBlock { FontFamily = DiffFont, FontSize = 13, LineHeight = 20, TextWrapping = _diffWrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Padding = new Thickness(8, 0, 8, 0) };
        foreach (var (text, brush) in CodeColors.Color(line.Text.Replace("\t", "  "), language)) code.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = brush });
        var row = new DockPanel { Background = added ? DiffAddLine : removed ? DiffRemoveLine : Brushes.Transparent, MinHeight = 20 };
        DockPanel.SetDock(gutter, Dock.Left);
        row.Children.Add(gutter);
        row.Children.Add(code);
        return row;
    }

    [GeneratedRegex(@"^diff --git a/(.+?) b/(.+)$")]
    private static partial Regex DiffNames();

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkStart();
}
