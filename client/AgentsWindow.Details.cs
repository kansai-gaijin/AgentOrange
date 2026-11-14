using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// T3 Code's thread details panel, copied from its code: a card under the header's right end,
/// opened by the "Toggle thread details" button (Ctrl+Shift+B). A Workspace section has the
/// machine and Open in; a Version Control section has the branch with its ref picker, the git
/// actions (the one that fits, with the rest in a menu) and the Changes row. Git status is
/// refreshed when the window comes back into focus and fetched every 30 seconds, as T3's server
/// does. T3's project scripts and Snooze aren't wanted here.
/// </summary>
public sealed partial class AgentsWindow
{
    private const double DetailsWidth = 280;

    // T3's panel colours over the glass: foreground at 80% for rows, white at 8% under the mouse.
    private static readonly SolidColorBrush RowWords = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFA, 0xFF)));
    private static readonly SolidColorBrush RowHover = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush Rule65 = Freeze(new SolidColorBrush(Color.FromArgb(0xA6, 0x40, 0x55, 0x67)));
    private static readonly SolidColorBrush Addition = Freeze(new SolidColorBrush(Color.FromRgb(0x05, 0xDF, 0x72)));
    private static readonly SolidColorBrush Deletion = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x64, 0x67)));

    private static readonly Dictionary<string, RepoState> RepoStates = [];
    private static readonly HashSet<string> RepoAsked = [];

    private Border? _headerBar;
    private readonly Grid _dialogs = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _toasts = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16), Width = 356 };
    private readonly Border _detailsLayer = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, TopBar - 4, 12, 0) };
    private Action? _redrawDetails;

    // A git action under way, or just finished, as T3's panel shows it.
    private sealed class GitRun
    {
        public string Status = "";
        public DateTimeOffset PhaseStarted = DateTimeOffset.UtcNow;
        public string? SuccessTitle;
        public string? SuccessDescription;
        public DateTimeOffset SuccessUntil;
    }

    private readonly Dictionary<string, GitRun> _gitRuns = [];

    private void BuildDetailsParts(Grid layout)
    {
        // In the chat's column, under its header, as T3 anchors it there.
        _chatAndPanel.Children.Add(_detailsLayer);
        _detailsLayer.PreviewKeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape || key.Handled) return;
            key.Handled = true;
            CloseDetails();
        };
        Grid.SetColumnSpan(_dialogs, 2);
        layout.Children.Add(_dialogs);
        Grid.SetColumnSpan(_toasts, 2);
        layout.Children.Add(_toasts);

        // T3's server fetches every 30 seconds while anything shows git status; status is read again on focus.
        var fetcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        fetcher.Tick += (_, _) => { if (_open is { } open) _ = RefreshRepoAsync(open.ProjectId, fetch: true); };
        fetcher.Start();
        Closed += (_, _) => fetcher.Stop();
        Activated += (_, _) => { if (_open is { } open) _ = RefreshRepoAsync(open.ProjectId, fetch: false); };

        // Ctrl+Shift+B toggles the panel, Ctrl+O opens the project in the favourite app.
        PreviewKeyDown += (_, key) =>
        {
            if (KeyState.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key.Key == Key.B)
            {
                key.Handled = true;
                ToggleDetails();
            }
            else if (KeyState.Modifiers == ModifierKeys.Control && key.Key == Key.O && _open is { } open && _main.ProjectFolder(open.ProjectId) is { } folder)
            {
                key.Handled = true;
                OpenIn(FavoriteOpener(), folder);
            }
        };
    }

    /// <summary>Reads how the project's folder stands, once Git may run there, and redraws what shows it.</summary>
    private async Task RefreshRepoAsync(string projectId, bool fetch)
    {
        if (await _main.GitFolderAsync(projectId) is not { Folder: { } folder }) return;
        var state = await Task.Run(() => GitTools.RepoStateAsync(folder, fetch));
        RepoStates[folder] = state;
        _redrawDetails?.Invoke();
        ShowConversations();
    }

    // The header's thread details button: T3's square-menu toggle, pressed while the panel is open.
    private FrameworkElement DetailsToggle()
    {
        var pressed = DetailsOpen;
        var button = Ocean.Ghost(AgentIcons.Line("square-menu", 16, Ocean.Foreground), ToggleDetails, 8, null, Ocean.Accent, pressed ? Ocean.Accent : null);
        button.Width = 28;
        button.Height = 28;
        button.Margin = new Thickness(0, 0, 4, 0);
        ((FrameworkElement)button.Content).Opacity = 0.8;
        button.ToolTip = "Toggle thread details (Ctrl+Shift+B)";
        ToolTipService.SetPlacement(button, PlacementMode.Bottom);
        AutomationProperties.SetName(button, "Toggle thread details panel");
        return button;
    }

    // T3's panel is a toggle: it stays open until its button (or Ctrl+Shift+B, or Escape) closes it,
    // sitting in the window under the header's right end, so it never floats over other apps.
    private bool DetailsOpen => _detailsLayer.Visibility == Visibility.Visible;

    private void CloseDetails()
    {
        if (!DetailsOpen) return;
        _detailsLayer.Visibility = Visibility.Collapsed;
        _detailsLayer.Child = null;
        _redrawDetails = null;
        ShowHeader();
    }

    private void ToggleDetails()
    {
        if (DetailsOpen)
        {
            CloseDetails();
            return;
        }
        if (_open is null) return;
        var layers = new Grid { Width = DetailsWidth };
        _detailsLayer.Child = new Border
        {
            Background = Ocean.Glass,
            BorderBrush = Ocean.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 32, ShadowDepth = 10, Direction = 270, Opacity = 0.55 },
            Child = layers,
        };
        void Redraw()
        {
            // Always about the open conversation, which can change while it's open.
            if (_open is { } open) DrawDetails(open, layers);
        }
        _redrawDetails = Redraw;
        Redraw();
        _detailsLayer.Visibility = Visibility.Visible;
        ShowHeader();
        _ = RefreshRepoAsync(_open.ProjectId, fetch: true);
    }

    // T3's Cw button in panel mode: 32 high, 8 round, 13 px medium words at 80%, a wash under the mouse.
    private static Button PanelButton(UIElement content, Action click, string name, Thickness? padding = null, CornerRadius? corners = null)
    {
        var button = Ocean.Ghost(content, click, 8, padding ?? new Thickness(10, 0, 10, 0), RowHover);
        if (corners is { } round) button.Template = Ocean.Template(round, RowHover);
        button.Height = 32;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static TextBlock RowText(string words, Brush? brush = null)
    {
        var text = Ocean.Text(words, brush ?? RowWords, 13, FontWeights.Medium);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        return text;
    }

    private static FrameworkElement RowIcon(string icon, double size = 16)
    {
        var glyph = AgentIcons.Line(icon, size, Ocean.MutedText);
        glyph.Margin = new Thickness(0, 0, 10, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }

    private static DockPanel IconRow(FrameworkElement icon, UIElement words)
    {
        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(words);
        return row;
    }

    // T3's split row: the main part, a divider that shows on hover, and a 32 px chevron.
    private static Grid SplitRow(Button primary, Button secondary)
    {
        primary.Template = Ocean.Template(new CornerRadius(8, 0, 0, 8), RowHover);
        secondary.Template = Ocean.Template(new CornerRadius(0, 8, 8, 0), RowHover);
        secondary.Width = 32;
        secondary.Padding = new Thickness(0);
        secondary.HorizontalContentAlignment = HorizontalAlignment.Center;
        var divider = new Border { Width = 1, Height = 16, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        var group = new Grid { Background = Brushes.Transparent };
        group.ColumnDefinitions.Add(new ColumnDefinition());
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(secondary, 2);
        group.Children.Add(primary);
        group.Children.Add(divider);
        group.Children.Add(secondary);
        group.MouseEnter += (_, _) => divider.Background = Rule65;
        group.MouseLeave += (_, _) => divider.Background = Brushes.Transparent;
        return group;
    }

    private static FrameworkElement Chevron() => AgentIcons.Line("chevron-down", 16, Ocean.MutedText);

    // A menu over the panel, put inside it so opening it doesn't count as clicking away.
    private static Popup PanelMenu(Panel layers, UIElement by, UIElement content, double width)
    {
        var menu = Ocean.Menu(by, PlacementMode.Bottom, content, width, 4);
        ((Border)menu.Child).Width = width + 2;
        layers.Children.Add(menu);
        menu.Closed += (_, _) => layers.Children.Remove(menu);
        return menu;
    }

    private void DrawDetails(AgentConversation conversation, Grid layers)
    {
        var body = new StackPanel();
        layers.Children.Clear();
        layers.Children.Add(body);
        var folder = _main.ProjectFolder(conversation.ProjectId);

        // Workspace: this machine, and opening the folder.
        var workspace = new StackPanel { Margin = new Thickness(8, 8, 8, 10) };
        var machineName = Environment.MachineName;
        var folderIcon = AgentIcons.Line("folder", 16, Ocean.MutedText);
        folderIcon.IsHitTestVisible = true;
        var folderSpot = new Border { Background = Brushes.Transparent, Child = folderIcon, ToolTip = folder ?? "This project has no folder on this PC yet." };
        DockPanel.SetDock(folderSpot, Dock.Right);
        var machineRow = new DockPanel { Height = 32, Margin = new Thickness(10, 0, 10, 0), Background = Brushes.Transparent };
        machineRow.Children.Add(folderSpot);
        var server = AgentIcons.Line("server", 18, Ocean.MutedText);
        server.Margin = new Thickness(-1, 0, 9, 0);
        machineRow.Children.Add(IconRow(server, RowText(machineName)));
        AutomationProperties.SetName(machineRow, $"Run context: {machineName}, Current checkout");
        if (folder is not null)
        {
            machineRow.MouseRightButtonUp += (_, click) =>
            {
                click.Handled = true;
                Popup? menu = null;
                var copy = Ocean.MenuRow(Ocean.Text("Copy full path", Ocean.Foreground, 13), () =>
                {
                    menu!.IsOpen = false;
                    Clipboard.SetText(folder);
                    Toast("Path copied", null, error: false);
                });
                menu = PanelMenu(layers, machineRow, copy, 180);
                menu.Placement = PlacementMode.MousePoint;
                menu.IsOpen = true;
            };
        }
        workspace.Children.Add(machineRow);
        if (folder is not null) workspace.Children.Add(OpenInRow(layers, folder));
        body.Children.Add(workspace);

        if (folder is null) return;
        RepoStates.TryGetValue(folder, out var state);
        _gitRuns.TryGetValue(folder, out var run);

        // Version Control: the branch, the git actions and the changes, under a line.
        var version = new StackPanel { Margin = new Thickness(8, 8, 8, 10) };
        if (state is { Problem: not null, Branch: null, HasChanges: false } && !state.HasOrigin)
        {
            // Not a repo: T3 offers to start one.
            version.Children.Add(PanelButton(IconRow(RowIcon("git-branch-plus", 14), RowText("Initialize Git")), () => _ = InitializeGitAsync(conversation, folder), "Initialize Git"));
        }
        else
        {
            version.Children.Add(BranchRow(conversation, layers, folder, state));
            if (state?.PullRequestUrl is { } address)
            {
                var number = address.TrimEnd('/').Split('/').LastOrDefault() ?? "";
                var pr = PanelButton(IconRow(RowIcon("git-pull-request-arrow"), RowText($"Pull request #{number}")), () => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose(), $"Pull request #{number}");
                pr.ToolTip = address;
                version.Children.Add(pr);
            }
            version.Children.Add(GitActionsRow(conversation, layers, folder, state, run));
            version.Children.Add(ChangesRow(state, () => ShowDiffPanel(conversation, null, null, branch: true)));
        }
        body.Children.Add(new Border { BorderBrush = Rule65, BorderThickness = new Thickness(0, 1, 0, 0), Child = version });
    }

    // Open in: the favourite app, and a menu of the others found on this PC. Picking one only makes it the favourite.
    private sealed record Opener(string Id, string Label, string? Program);

    private static List<Opener> Openers()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        (string Id, string Label, string[] Places)[] known =
        [
            ("cursor", "Cursor", [Path.Combine(local, "Programs", "cursor", "Cursor.exe")]),
            ("trae", "Trae", [Path.Combine(local, "Programs", "Trae", "Trae.exe")]),
            ("kiro", "Kiro", [Path.Combine(local, "Programs", "Kiro", "Kiro.exe")]),
            ("vscode", "VS Code", [Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(programs, "Microsoft VS Code", "Code.exe")]),
            ("vscode-insiders", "VS Code Insiders", [Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe"), Path.Combine(programs, "Microsoft VS Code Insiders", "Code - Insiders.exe")]),
            ("vscodium", "VSCodium", [Path.Combine(local, "Programs", "VSCodium", "VSCodium.exe"), Path.Combine(programs, "VSCodium", "VSCodium.exe")]),
            ("zed", "Zed", [Path.Combine(local, "Programs", "Zed", "Zed.exe"), Path.Combine(local, "Programs", "Zed", "zed.exe")]),
            ("antigravity", "Antigravity", [Path.Combine(local, "Programs", "Antigravity", "Antigravity.exe")]),
        ];
        var found = known.Select(each => (each.Id, each.Label, Program: each.Places.FirstOrDefault(File.Exists))).Where(each => each.Program is not null).Select(each => new Opener(each.Id, each.Label, each.Program)).ToList();
        found.Add(new Opener("file-manager", "File Explorer", null));
        return found;
    }

    private string FavoriteFile => Path.Combine(_main.AgentFolder, "open-in.json");

    private Opener FavoriteOpener()
    {
        var all = Openers();
        try
        {
            if (File.Exists(FavoriteFile) && JsonSerializer.Deserialize<string>(File.ReadAllText(FavoriteFile)) is { } id && all.FirstOrDefault(each => each.Id == id) is { } chosen) return chosen;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't read the favourite app: {error.Message}");
        }
        return all[^1];
    }

    private void OpenIn(Opener opener, string folder)
    {
        try
        {
            if (opener.Program is { } program) Process.Start(new ProcessStartInfo(program) { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
            else Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
            Toast($"Opened in {opener.Label}", null, error: false);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Toast("Unable to open file", error.Message, error: true);
        }
    }

    private static FrameworkElement OpenerIcon(Opener opener) => RowIcon(opener.Program is null ? "folder-closed" : "square-arrow-out-up-right");

    private FrameworkElement OpenInRow(Grid layers, string folder)
    {
        var favorite = FavoriteOpener();
        var primary = PanelButton(IconRow(OpenerIcon(favorite), RowText($"Open in {favorite.Label}")), () =>
        {
            CloseDetails();
            OpenIn(favorite, folder);
        }, $"Open in {favorite.Label}");
        Button? chooser = null;
        Grid? row = null;
        chooser = PanelButton(Chevron(), () =>
        {
            var rows = new StackPanel();
            Popup? menu = null;
            foreach (var opener in Openers())
            {
                var line = new DockPanel();
                if (opener.Id == favorite.Id)
                {
                    var keys = Shortcut("Ctrl+O");
                    DockPanel.SetDock(keys, Dock.Right);
                    line.Children.Add(keys);
                }
                line.Children.Add(IconRow(OpenerIcon(opener), Ocean.Text(opener.Label, Ocean.Foreground, 14)));
                rows.Children.Add(Ocean.MenuRow(line, () =>
                {
                    menu!.IsOpen = false;
                    try
                    {
                        File.WriteAllText(FavoriteFile, JsonSerializer.Serialize(opener.Id));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Write($"Couldn't save the favourite app: {error.Message}");
                    }
                    _redrawDetails?.Invoke();
                }));
            }
            menu = PanelMenu(layers, row!, rows, row!.ActualWidth);
            menu.IsOpen = true;
        }, "Choose editor");
        row = SplitRow(primary, chooser);
        AutomationProperties.SetName(row, "Open in editor");
        return row;
    }

    // T3's kbd: muted 12 px words on a faint wash.
    private static Border Shortcut(string keys) => new()
    {
        Height = 20,
        MinWidth = 20,
        Padding = new Thickness(4, 0, 4, 0),
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFA, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = keys, Foreground = Ocean.MutedText, FontSize = 12, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center },
    };

    // The branch, as T3's select row: the name and a chevron that opens the ref picker.
    private FrameworkElement BranchRow(AgentConversation conversation, Grid layers, string folder, RepoState? state)
    {
        var line = new DockPanel();
        var chevronPart = new DockPanel { Width = 32 };
        var divider = new Border { Width = 1, Height = 16, Background = Brushes.Transparent };
        DockPanel.SetDock(divider, Dock.Left);
        chevronPart.Children.Add(divider);
        var chevron = Chevron();
        chevron.HorizontalAlignment = HorizontalAlignment.Center;
        chevronPart.Children.Add(chevron);
        DockPanel.SetDock(chevronPart, Dock.Right);
        line.Children.Add(chevronPart);
        line.Children.Add(IconRow(RowIcon("git-branch"), RowText(state?.Branch ?? "Select ref")));
        Button? button = null;
        button = PanelButton(line, () => PickRef(conversation, layers, button!, folder, state), $"Branch: {state?.Branch ?? "none"}", new Thickness(10, 0, 0, 0));
        button.MouseEnter += (_, _) => divider.Background = Rule65;
        button.MouseLeave += (_, _) => divider.Background = Brushes.Transparent;
        return button;
    }

    // T3's ref picker: "Search refs...", the refs with a tag for the current, remote and default
    // ones, and "Create new ref" for a name that isn't there.
    private void PickRef(AgentConversation conversation, Grid layers, Button by, string folder, RepoState? state)
    {
        var search = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ocean.Foreground,
            CaretBrush = Ocean.Foreground,
            SelectionBrush = Ocean.Focus,
            FontSize = 14,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(search, "Search refs");
        var hint = Ocean.Text("Search refs...", Ocean.Placeholder, 14);
        hint.IsHitTestVisible = false;
        var searchIcon = AgentIcons.Line("search", 16, new SolidColorBrush(Color.FromArgb(0x8C, 0x96, 0x9C, 0xA6)));
        searchIcon.Margin = new Thickness(0, 0, 8, 0);
        var searchRow = new DockPanel { Height = 24 };
        DockPanel.SetDock(searchIcon, Dock.Left);
        searchRow.Children.Add(searchIcon);
        searchRow.Children.Add(new Grid { Children = { hint, search } });
        var list = new StackPanel { Margin = new Thickness(4) };
        List<string>? all = null;
        var highlighted = 0;
        var choices = new List<(string Label, Action Pick)>();
        Popup? menu = null;

        void Run(Func<Task<GitResult>> step, string failure)
        {
            menu!.IsOpen = false;
            _ = step().ContinueWith(task => Dispatcher.Invoke(() =>
            {
                if (!task.Result.Worked) Toast(failure, task.Result.Value, error: true);
                _ = RefreshRepoAsync(conversation.ProjectId, fetch: false);
            }), TaskScheduler.Default);
        }

        void DrawList()
        {
            list.Children.Clear();
            choices.Clear();
            var words = search.Text.Trim();
            if (all is null)
            {
                list.Children.Add(new TextBlock { Text = "Loading refs...", Foreground = Ocean.MutedText, FontSize = 14, Margin = new Thickness(8, 6, 8, 6) });
                return;
            }
            var defaultName = state?.DefaultBranch is { } fallback && fallback.StartsWith("origin/", StringComparison.Ordinal) ? fallback[7..] : null;
            foreach (var name in all.Where(name => words.Length == 0 || name.Contains(words, StringComparison.OrdinalIgnoreCase)))
            {
                var target = name;
                choices.Add((name, () =>
                {
                    if (target == state?.Branch) menu!.IsOpen = false;
                    else Run(() => GitTools.SwitchBranchAsync(folder, target), "Failed to switch ref.");
                }));
            }
            if (words.Length > 0 && !all.Contains(words))
            {
                choices.Add(($"Create new ref \"{words}\"", () => Run(() => GitTools.CreateBranchAsync(folder, words), "Failed to create and switch ref.")));
            }
            if (choices.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "No refs found.", Foreground = Ocean.MutedText, FontSize = 14, Margin = new Thickness(8, 6, 8, 6) });
                return;
            }
            highlighted = Math.Clamp(highlighted, 0, choices.Count - 1);
            for (var at = 0; at < choices.Count; at++)
            {
                var (label, pick) = choices[at];
                var line = new DockPanel();
                var tag = label == state?.Branch ? "current" : label.StartsWith("origin/", StringComparison.Ordinal) ? "remote" : label == defaultName ? "default" : null;
                if (tag is not null)
                {
                    var tagText = Ocean.Text(tag, new SolidColorBrush(Color.FromArgb(0x73, 0x96, 0x9C, 0xA6)), 10);
                    tagText.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(tagText, Dock.Right);
                    line.Children.Add(tagText);
                }
                var name = Ocean.Text(label, Ocean.Foreground, 14);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                line.Children.Add(name);
                var item = Ocean.Ghost(line, pick, 4, new Thickness(8, 0, 8, 0), Wash9, at == highlighted ? Wash9 : null);
                item.MinHeight = 28;
                item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(item, label);
                list.Children.Add(item);
            }
        }

        search.TextChanged += (_, _) =>
        {
            hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            highlighted = 0;
            DrawList();
        };
        search.PreviewKeyDown += (_, key) =>
        {
            if (key.Key == Key.Down && choices.Count > 0) highlighted = (highlighted + 1) % choices.Count;
            else if (key.Key == Key.Up && choices.Count > 0) highlighted = (highlighted - 1 + choices.Count) % choices.Count;
            else if (key.Key == Key.Enter && choices.Count > 0) choices[highlighted].Pick();
            else return;
            key.Handled = true;
            if (menu!.IsOpen) DrawList();
        };
        var content = new StackPanel
        {
            Children =
            {
                new Border { Padding = new Thickness(12, 10, 12, 8), BorderBrush = Ocean.Border70, BorderThickness = new Thickness(0, 0, 0, 1), Child = searchRow },
                new ScrollViewer { MaxHeight = 224, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
            },
        };
        menu = PanelMenu(layers, by, content, by.ActualWidth);
        ((Border)menu.Child).Padding = new Thickness(0);
        DrawList();
        menu.IsOpen = true;
        Dispatcher.InvokeAsync(() => Keyboard.Focus(search), DispatcherPriority.Input);
        _ = GitTools.BranchesAsync(folder).ContinueWith(task => Dispatcher.Invoke(() =>
        {
            all = task.Result;
            DrawList();
        }), TaskScheduler.Default);
    }

    // The quick action's icon, as T3's Mz picks it.
    private static FrameworkElement ActionIcon(GitAction action, string label)
    {
        if (action is GitAction.PushPr or GitAction.CreatePr or GitAction.CommitPushPr) return AgentIcons.GitHub(16, Ocean.MutedText);
        var name = action switch
        {
            GitAction.Pull => "cloud-download",
            GitAction.Push or GitAction.CommitPush => "cloud-upload",
            GitAction.Commit => "git-commit-horizontal",
            _ => label switch { "Commit" => "git-commit-horizontal", "Push" => "cloud-upload", _ => "info" },
        };
        return AgentIcons.Line(name, 16, Ocean.MutedText);
    }

    private FrameworkElement GitActionsRow(AgentConversation conversation, Grid layers, string folder, RepoState? state, GitRun? run)
    {
        var busy = run is not null && run.SuccessTitle is null;
        var plan = PlanFor(state, busy);
        Button primary;
        if (busy)
        {
            // Under way: a spinner, what it's doing, and how long this part has taken.
            var spinner = AgentIcons.Line("loader-circle", 16, RowWords);
            spinner.RenderTransformOrigin = new Point(0.5, 0.5);
            var turn = new RotateTransform();
            spinner.RenderTransform = turn;
            turn.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            primary = PanelButton(IconRow(WithGap(spinner, 12), RowText(run!.Status)), () => { }, run.Status);
            primary.IsHitTestVisible = false;
            var timer = Ocean.Text(Elapsed(run.PhaseStarted), Ocean.MutedText, 11);
            timer.VerticalAlignment = VerticalAlignment.Center;
            timer.Margin = new Thickness(0, 0, 10, 0);
            var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (_, _) => timer.Text = Elapsed(run.PhaseStarted);
            tick.Start();
            timer.Unloaded += (_, _) => tick.Stop();
            var running = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)) };
            running.ColumnDefinitions.Add(new ColumnDefinition());
            running.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(timer, 1);
            running.Children.Add(primary);
            running.Children.Add(timer);
            return new Border { CornerRadius = new CornerRadius(8), Child = running };
        }
        if (run?.SuccessTitle is { } title && run.SuccessUntil > DateTimeOffset.UtcNow)
        {
            // Done: T3 says so for 10 seconds, the chevron still there.
            var check = AgentIcons.Line("check", 14, Ocean.Success);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { RowText(title) } };
            if (run.SuccessDescription is { Length: > 0 } said) words.Children.Add(new TextBlock { Text = said, Foreground = Ocean.MutedText, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            primary = PanelButton(IconRow(WithGap(check, 12), words), () => { }, title);
            primary.Height = double.NaN;
            primary.MinHeight = 32;
            primary.Padding = new Thickness(10, 6, 10, 6);
            primary.IsHitTestVisible = false;
            var wait = run.SuccessUntil - DateTimeOffset.UtcNow;
            _ = Task.Delay(wait).ContinueWith(_ => Dispatcher.Invoke(() => _redrawDetails?.Invoke()), TaskScheduler.Default);
        }
        else
        {
            primary = PanelButton(IconRow(WithGap(ActionIcon(plan.Action, plan.Label), 10), RowText(plan.Label)), () =>
            {
                if (plan.Action == GitAction.None)
                {
                    Toast(plan.Label, plan.Hint, error: false);
                    return;
                }
                StartGitAction(conversation, folder, plan.Action, null, featureBranch: false, fromMenu: false);
            }, plan.Label);
            if (plan.Action == GitAction.None)
            {
                primary.Opacity = 0.64;
                primary.ToolTip = plan.Hint ?? "This action is currently unavailable.";
                ToolTipService.SetPlacement(primary, PlacementMode.Bottom);
            }
        }

        Grid? group = null;
        Button? options = null;
        options = PanelButton(Chevron(), () =>
        {
            _ = RefreshRepoAsync(conversation.ProjectId, fetch: false);
            var rows = new StackPanel();
            Popup? menu = null;
            foreach (var (label, icon, action, why) in GitMenu(state, busy))
            {
                FrameworkElement glyph = icon == "git-pull-request" ? AgentIcons.GitHub(16, Ocean.MutedText) : AgentIcons.Line(icon == "arrow-up-from-line" ? "cloud-upload" : icon, 16, Ocean.MutedText);
                glyph.Opacity = 0.8;
                var item = Ocean.MenuRow(IconRow(WithGap(glyph, 8), Ocean.Text(label, Ocean.Foreground, 14)), () =>
                {
                    if (why is not null) return;
                    menu!.IsOpen = false;
                    StartGitAction(conversation, folder, action, null, featureBranch: false, fromMenu: true);
                });
                item.MinHeight = 28;
                item.Padding = new Thickness(8, 4, 8, 4);
                if (why is not null)
                {
                    item.Opacity = 0.64;
                    item.Cursor = Cursors.No;
                    item.ToolTip = why;
                    ToolTipService.SetPlacement(item, PlacementMode.Left);
                }
                AutomationProperties.SetName(item, why is null ? label : $"{label}: {why}");
                rows.Children.Add(item);
            }
            if (state is { Branch: null }) rows.Children.Add(WarningText($"Detached HEAD: create and check out a branch to enable push and pull request actions."));
            else if (state is { HasChanges: false, Behind: > 0, Ahead: 0 }) rows.Children.Add(WarningText("Behind upstream. Pull/rebase first."));
            if (state?.Problem is { } problem && state.Branch is null) rows.Children.Add(new TextBlock { Text = problem, Foreground = Ocean.Error, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 6) });
            menu = PanelMenu(layers, group!, rows, group!.ActualWidth);
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }, "Git action options");
        group = SplitRow(primary, options);
        AutomationProperties.SetName(group, "Git actions");
        return group;
    }

    private static TextBlock WarningText(string words) => new() { Text = words, Foreground = Ocean.Warning, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 6) };

    private static FrameworkElement WithGap(FrameworkElement icon, double gap)
    {
        icon.Margin = new Thickness(0, 0, gap, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    private static string Elapsed(DateTimeOffset since)
    {
        var seconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - since).TotalSeconds);
        return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
    }

    // Changes: what's changed since the default branch, or what isn't committed yet when there's none.
    private static FrameworkElement ChangesRow(RepoState? state, Action open)
    {
        var line = new DockPanel();
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        var added = state?.Insertions ?? 0;
        var removed = state?.Deletions ?? 0;
        counts.Children.Add(new TextBlock { Text = $"+{Count(added)}", FontFamily = Ocean.Mono, FontSize = 13, Foreground = added > 0 ? Addition : Ocean.MutedText, Margin = new Thickness(0, 0, 8, 0) });
        counts.Children.Add(new TextBlock { Text = $"-{Count(removed)}", FontFamily = Ocean.Mono, FontSize = 13, Foreground = removed > 0 ? Deletion : Ocean.MutedText });
        DockPanel.SetDock(counts, Dock.Right);
        line.Children.Add(counts);
        var against = state?.DefaultBranch ?? state?.Upstream;
        var words = new StackPanel { Orientation = Orientation.Horizontal, Children = { RowText("Changes") } };
        var detail = RowText(against is null ? "Uncommitted" : $"vs {against}", Ocean.MutedText);
        detail.FontWeight = FontWeights.Normal;
        detail.Margin = new Thickness(6, 0, 0, 0);
        detail.ToolTip = detail.Text;
        words.Children.Add(detail);
        line.Children.Add(IconRow(RowIcon("file-diff"), words));
        return PanelButton(line, open, "Changes");
    }

    // As T3 shortens them: 999, 1.2k, 3m.
    private static string Count(int number) => number switch
    {
        >= 1_000_000 => $"{number / 1_000_000.0:0.#}m",
        >= 1_000 => $"{number / 1_000.0:0.#}k",
        _ => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private void Toast(string title, string? description, bool error)
    {
        var words = new StackPanel { Children = { Ocean.Text(title, Ocean.Foreground, 14, FontWeights.Medium) } };
        if (description is { Length: > 0 }) words.Children.Add(Ocean.Wrapped(description, Ocean.MutedText, 13, 18));
        var icon = AgentIcons.Line(error ? "circle-alert" : "circle-check", 16, error ? Ocean.Error : Ocean.Success);
        icon.Margin = new Thickness(0, 2, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        Border? toast = null;
        var close = Ocean.IconButton("x", () => _toasts.Children.Remove(toast), "Close", 24, 14, Ocean.MutedText);
        close.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(close);
        row.Children.Add(words);
        toast = new Border
        {
            Background = Ocean.Popover,
            BorderBrush = Ocean.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 8, 12),
            Margin = new Thickness(0, 8, 0, 0),
            Child = row,
        };
        AutomationProperties.SetName(toast, description is null ? title : $"{title}: {description}");
        _toasts.Children.Add(toast);
        if (!error) _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => Dispatcher.Invoke(() => _toasts.Children.Remove(toast)), TaskScheduler.Default);
    }

    private async Task InitializeGitAsync(AgentConversation conversation, string folder)
    {
        var made = await GitTools.InitializeAsync(folder);
        if (!made.Worked) Toast("Git initialization failed", made.Value, error: true);
        await RefreshRepoAsync(conversation.ProjectId, fetch: false);
    }
}
