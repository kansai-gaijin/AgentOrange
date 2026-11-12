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
        var back = ActionButton("← Overview");
        back.HorizontalAlignment = HorizontalAlignment.Center;
        back.Margin = new Thickness(0, 20, 0, 0);
        back.Click += (_, _) =>
        {
            _openSection = null;
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
        };
        body.Children.Add(back);

        body.Children.Add(new TextBlock { Text = name, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        var note = CenteredNote("Nothing here yet.");
        note.Margin = new Thickness(0, 40, 0, 40);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(note);
    }
}
