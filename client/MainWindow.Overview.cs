using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>What an open project shows first: its stage, how far along it is, and the way into each part of it.</summary>
public partial class MainWindow
{
    private static readonly string[] ProjectSections = ["GDD", "Characters", "Levels", "Assets", "Mechanics", "References"];
    private const int SectionCoverWidth = 176;
    private const int SectionCoverHeight = 110;

    // Which part of which project is open, as the page is drawn again whenever anything syncs.
    private (string ProjectId, string Name)? _openSection;

    // Built once each and put back, as the page is drawn again whenever anything syncs, and a
    // board keeps where it's been moved and zoomed to.
    private readonly Dictionary<string, NodeCanvas> _boards = [];
    private bool _boardsFollowWindow;

    private double HeroHeight(Project project)
    {
        var inSection = _openSection is { } open && open.ProjectId == project.Id;
        var hasPicture = HeaderFor(project.Id) is not null;

        // Only as tall as a picture needs. Without one it's just room for what's in it.
        return (inSection, hasPicture) switch { (true, true) => 120, (true, false) => 60, (false, true) => 180, _ => 112 };
    }

    private NodeCanvas? OpenBoard(Project project) =>
        _openSection is { } open && open.ProjectId == project.Id ? _boards.GetValueOrDefault($"{project.Id}|{open.Name}") : null;

    private void AddBoard(StackPanel body, Project project, string name)
    {
        // The whole page, edge to edge, under the header.
        body.MaxWidth = double.PositiveInfinity;
        body.Margin = new Thickness(0);

        var key = $"{project.Id}|{name}";
        if (!_boards.TryGetValue(key, out var board)) board = _boards[key] = new NodeCanvas(_database, project.Id, name.ToLowerInvariant(), () => _ = _sync.SyncNowAsync());
        var hadFocus = board.IsKeyboardFocusWithin;
        (board.Parent as Panel)?.Children.Remove(board);

        board.Height = Math.Max(240, ProjectView.ActualHeight - HeroHeight(project));
        board.Reload();
        body.Children.Add(board);

        // Its keys work as soon as the page opens, without a click on it first. Not while a
        // window on top has the keyboard, such as project settings.
        if (hadFocus || MainContent.IsEnabled) _ = Dispatcher.InvokeAsync(board.Focus, System.Windows.Threading.DispatcherPriority.Loaded);

        if (_boardsFollowWindow) return;
        _boardsFollowWindow = true;
        ProjectView.SizeChanged += (_, _) =>
        {
            if (_openProject is { } open && OpenBoard(open) is { IsVisible: true } shown) shown.Height = Math.Max(240, ProjectView.ActualHeight - HeroHeight(open));
        };
    }

#if DEBUG
    internal void RedrawAfterCodeChange()
    {
        _boards.Clear(); // And boards would keep the code they were made with.
        _cards.Clear(); // Kept cards would keep what they did before the change.
        RefreshProjects();
        if (_openProject is { } open) RenderProject(open);
    }
#endif

    private void AddOverview(StackPanel body, Project project)
    {
        if (_openSection is { } open && open.ProjectId == project.Id)
        {
            AddSectionPage(body, project, open.Name);
            return;
        }

        var progress = Section("Progress");
        progress.Margin = new Thickness(0, 24, 0, 10);
        progress.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(progress);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { ProgressTile("Tasks done", 0), ProgressTile("Assets delivered", 0) },
        });

        var sections = new WrapPanel { Margin = new Thickness(0, 28, 0, 24), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var name in ProjectSections) sections.Children.Add(SectionCard(project, name));
        body.Children.Add(sections);
    }

    private StackPanel StageToggle(Project project)
    {
        var current = _database.ProjectStage(project.Id);
        var toggle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var stage in LocalDatabase.Stages)
        {
            // In the header, so the ones not chosen look like its other buttons.
            var choice = new Button { Style = (Style)FindResource(stage == current ? "PrimaryButton" : "OverlayButton"), Content = stage, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(choice, stage == current ? $"{stage}, the current stage" : stage);
            choice.Click += (_, _) =>
            {
                if (stage == current) return;
                _database.SetProjectStage(project.Id, stage);
                _ = _sync.SyncNowAsync();
                if (_openProject?.Id == project.Id) RenderProjectKeepingFocus(_openProject);
            };
            toggle.Children.Add(choice);
        }
        return toggle;
    }

    private Border ProgressTile(string label, int count) => new()
    {
        BorderBrush = (Brush)FindResource("Line"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(18, 12, 18, 14),
        Margin = new Thickness(0, 0, 12, 0),
        MinWidth = 176,
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = count.ToString(), FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong") },
                new TextBlock { Text = label, FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 2, 0, 0) },
            },
        },
    };

    private Button SectionCard(Project project, string name)
    {
        var cover = new Border
        {
            Width = SectionCoverWidth,
            Height = SectionCoverHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = PlaceholderFor($"{project.Id}:{name}"),
            Child = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };

        var card = new Button { Style = (Style)FindResource("ProjectCard"), Content = cover };
        AutomationProperties.SetName(card, name);

        void ShowActive() => cover.BorderBrush = card.IsMouseOver || card.IsKeyboardFocusWithin ? (Brush)FindResource("Accent") : Brushes.Transparent;
        card.MouseEnter += (_, _) => ShowActive();
        card.MouseLeave += (_, _) => ShowActive();
        card.IsKeyboardFocusWithinChanged += (_, _) => ShowActive();
        card.Click += (_, _) =>
        {
            _openSection = (project.Id, name);
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
        };
        return card;
    }

    private void AddSectionPage(StackPanel body, Project project, string name)
    {
        if (name == "GDD")
        {
            AddBoard(body, project, name);
            return;
        }

        var note = CenteredNote("Nothing here yet.");
        note.Margin = new Thickness(0, 40, 0, 40);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(note);
    }
}
