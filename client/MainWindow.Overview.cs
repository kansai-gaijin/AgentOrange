using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        _openSection is { } open && open.ProjectId == project.Id ? _boards.GetValueOrDefault($"{project.Id}|{_database.ProjectStage(project.Id)}|{open.Name}|{OpenCardOf(project)?.Id}") : null;

    // A card page's board is one card's own. Any other page has just the one.
    private void AddBoard(StackPanel body, Project project, string name, BoardCard? card = null)
    {
        // The whole page, edge to edge, under the header.
        body.MaxWidth = double.PositiveInfinity;
        body.Margin = new Thickness(0);

        // Each stage has a board of its own for this part of the project.
        _database.GiveBoardsAStage(project.Id);
        var stage = _database.ProjectStage(project.Id);
        var key = $"{project.Id}|{stage}|{name}|{card?.Id}";
        var named = card is null ? LocalDatabase.BoardKey(stage, name.ToLowerInvariant()) : LocalDatabase.CardBoardKey(card);
        if (!_boards.TryGetValue(key, out var board)) board = _boards[key] = new NodeCanvas(_database, project.Id, named, () => _ = _sync.SyncNowAsync());
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
        foreach (var stage in _database.ProjectStages(project.Id))
        {
            // In the header, so the ones not chosen look like its other buttons.
            var choice = new Button { Style = (Style)FindResource(stage.Id == current ? "PrimaryButton" : "OverlayButton"), Content = stage.Name, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(choice, stage.Id == current ? $"{stage.Name}, the current stage" : stage.Name);
            choice.Click += (_, _) =>
            {
                if (stage.Id == current) return;
                _database.SetProjectStage(project.Id, stage.Id);
                _ = _sync.SyncNowAsync();
                if (_openProject?.Id == project.Id) RenderProjectKeepingFocus(_openProject);
            };
            toggle.Children.Add(choice);
        }

        var manage = new Button { Style = (Style)FindResource("OverlayButton"), Content = "⋯", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Add, rename, copy or delete stages" };
        AutomationProperties.SetName(manage, "Manage stages");
        manage.Click += (_, _) => OpenStageMenu(manage, project);
        toggle.Children.Add(manage);
        return toggle;
    }

    // Stages work like branches kept in the project: add one, copy one over another, rename or delete.
    private void OpenStageMenu(Button under, Project project)
    {
        var stages = _database.ProjectStages(project.Id);
        var currentId = _database.ProjectStage(project.Id);
        var current = stages.First(stage => stage.Id == currentId);

        var rows = new StackPanel { Width = 320 };
        var menu = new Popup
        {
            PlacementTarget = under,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x32)),
                BorderBrush = (Brush)FindResource("ModalBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Child = rows,
            },
        };

        void Done()
        {
            menu.IsOpen = false;
            _ = _sync.SyncNowAsync();
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
        }

        TextBlock Label(string text, double above = 0) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, above, 0, 6), TextWrapping = TextWrapping.Wrap };
        WrapPanel Buttons(params Button[] buttons)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var button in buttons)
            {
                button.Margin = new Thickness(0, 0, 6, 6);
                row.Children.Add(button);
            }
            return row;
        }

        // A new stage, empty or starting as a copy of the one the project is at, the way a branch does.
        var problem = ProblemText();
        problem.Margin = new Thickness(0, 6, 0, 0);
        var newName = Field("");
        AutomationProperties.SetName(newName, "Name of the new stage");
        void Add(bool asCopy)
        {
            var name = newName.Text.Trim();
            if (_database.StageNameProblem(project.Id, name) is { } wrong)
            {
                ShowProblem(problem, wrong);
                return;
            }
            _database.SetProjectStage(project.Id, _database.AddStage(project.Id, name, asCopy ? current.Id : null));
            Done();
        }
        rows.Children.Add(Label("New stage"));
        rows.Children.Add(newName);
        rows.Children.Add(Buttons(ControlButton($"Add as a copy of {current.Name}", () => Add(asCopy: true)), ControlButton("Add empty", () => Add(asCopy: false))));
        rows.Children.Add(problem);

        var line = Divider();
        line.Margin = new Thickness(0, 10, 0, 12);
        rows.Children.Add(line);

        // The stage the project is at.
        var renameProblem = ProblemText();
        renameProblem.Margin = new Thickness(0, 6, 0, 0);
        var rename = Field(current.Name);
        AutomationProperties.SetName(rename, $"Name of {current.Name}");
        void Rename()
        {
            var name = rename.Text.Trim();
            if (name == current.Name)
            {
                menu.IsOpen = false;
                return;
            }
            if (_database.StageNameProblem(project.Id, name, renaming: current.Id) is { } wrong)
            {
                ShowProblem(renameProblem, wrong);
                return;
            }
            _database.RenameStage(project.Id, current.Id, name);
            Done();
        }
        rename.KeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Enter) return;
            pressed.Handled = true;
            Rename();
        };
        rows.Children.Add(Label($"Rename {current.Name}"));
        rows.Children.Add(rename);
        rows.Children.Add(Buttons(ControlButton("Rename", Rename)));
        rows.Children.Add(renameProblem);

        var others = stages.Where(stage => stage.Id != current.Id).ToList();
        if (others.Count > 0)
        {
            rows.Children.Add(Label($"Copy {current.Name} over another stage", above: 10));
            rows.Children.Add(Buttons([.. others.Select(other => ControlButton(other.Name, () =>
            {
                menu.IsOpen = false;
                if (!Dialogs.Confirm($"Make {other.Name} a copy of {current.Name}?\n\nEverything in {other.Name} now is deleted and replaced. {current.Name} stays as it is.")) return;
                _database.CopyStage(project.Id, current.Id, other.Id);
                Done();
            }))]));

            rows.Children.Add(Buttons(Clickable("DangerButton", $"Delete {current.Name}", () =>
            {
                menu.IsOpen = false;
                if (!Dialogs.Confirm($"Delete {current.Name} and everything in it?\n\nThis can't be undone. The other stages stay as they are.")) return;
                _database.DeleteStage(project.Id, current.Id);
                Done();
            })));
        }

        menu.Opened += (_, _) => newName.Focus();
        menu.IsOpen = true;
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
            _openCard = null;
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
        if (CardPages.ContainsKey(name))
        {
            if (OpenCardOf(project) is { } card) AddBoard(body, project, name, card);
            else AddCardPage(body, project, name);
            return;
        }

        var note = CenteredNote("Nothing here yet.");
        note.Margin = new Thickness(0, 40, 0, 40);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(note);
    }
}
