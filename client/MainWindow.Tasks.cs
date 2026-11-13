using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// A project's tasks are its assets that have a date to deliver them by. Nothing else makes one,
/// and nothing is ticked: a task is done when its asset is delivered, which only a person does.
/// The Tasks page lists them by when they're due, in the stage the project is at.
/// </summary>
public partial class MainWindow
{
    private const string TasksPage = "Tasks";

    private sealed record AssetTask(BoardCard Asset, AssetDetails Details, DateOnly Due, bool Done);

    // Dates are days on this PC's calendar, not moments, so one due today is due all day.
    internal static DateOnly? DueOf(AssetDetails details) =>
        DateOnly.TryParseExact(details.Due ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) ? due : null;

    internal static string DueText(DateOnly due) => due.ToString(due.Year == DateTime.Today.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private List<AssetTask> ProjectTasks(Project project) =>
    [
        .. _database.ListCards(project.Id, _database.ProjectStage(project.Id), AssetsPart)
            .Select(card => (Card: card, Details: _database.GetAssetDetails(card.Id)))
            .Where(each => DueOf(each.Details) is not null)
            .Select(each => new AssetTask(each.Card, each.Details, DueOf(each.Details)!.Value, _database.GetAssetDeliverable(each.Card.Id) is not null))
            .OrderBy(task => task.Due)
            .ThenBy(task => task.Asset.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    private void OpenTasks(Project project)
    {
        _openSection = (project.Id, TasksPage);
        _openCard = null;
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
    }

    private void AddTasksPage(StackPanel body, Project project)
    {
        var tasks = ProjectTasks(project);
        body.MaxWidth = 900;
        body.Margin = new Thickness(40, 22, 40, 30);

        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                new TextBlock { Text = TasksPage, Foreground = (Brush)FindResource("TextStrong"), FontSize = 24, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = tasks.Count == 0 ? "" : $"{tasks.Count(task => task.Done)} of {tasks.Count} done", Foreground = (Brush)FindResource("TextMuted"), FontSize = 15, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 4) },
            },
        });
        body.Children.Add(new TextBlock
        {
            Text = tasks.Count == 0
                ? "No tasks yet. Give an asset a date to deliver it by, and it's a task here."
                : "Each is an asset with a date to deliver it by, and it's done once it's delivered.",
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var today = Today;
        (string Name, Func<AssetTask, bool> Holds)[] groups =
        [
            ("Overdue", task => !task.Done && task.Due < today),
            ("Today", task => !task.Done && task.Due == today),
            ("This week", task => !task.Done && task.Due > today && task.Due < today.AddDays(7)),
            ("Later", task => !task.Done && task.Due >= today.AddDays(7)),
            ("Done", task => task.Done),
        ];
        foreach (var (name, holds) in groups)
        {
            var inGroup = tasks.Where(holds).ToList();
            if (inGroup.Count == 0) continue;
            var heading = Section(name);
            heading.Margin = new Thickness(0, 18, 0, 6);
            if (name == "Overdue") heading.Foreground = (Brush)FindResource("Danger");
            body.Children.Add(heading);
            foreach (var task in inGroup) body.Children.Add(TaskRow(project, task, overdue: name == "Overdue"));
        }
    }

    private Button TaskRow(Project project, AssetTask task, bool overdue)
    {
        var name = CardName(task.Asset, AssetsPage);
        var what = AssetTypeName(task.Details.Type) ?? "No type";
        var owner = AssetOwnerChoices(_database, project.Id, task.Asset.Board.Split('/')[0]).FirstOrDefault(choice => choice.Card.Id == task.Details.Owner);
        if (owner.Card is not null) what += $" · {CardName(owner.Card, owner.Page)}";

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = task.Done ? (Brush)FindResource("Accent") : Brushes.Transparent,
            Stroke = (Brush)FindResource(task.Done ? "Accent" : "TextMuted"),
            StrokeThickness = 1.3,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var due = new TextBlock { Text = DueText(task.Due), FontSize = 13, Foreground = (Brush)FindResource(overdue ? "Danger" : "TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var row = new DockPanel();
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(due, Dock.Right);
        row.Children.Add(dot);
        row.Children.Add(due);
        row.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(task.Done ? "TextMuted" : "TextStrong"), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = what, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) },
            },
        });

        var button = new Button { Style = (Style)FindResource("HeaderButton"), Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Margin = new Thickness(-7, 0, -7, 2) };
        AutomationProperties.SetName(button, $"{name}, due {DueText(task.Due)}{(task.Done ? ", delivered" : overdue ? ", overdue" : "")}");
        button.Click += (_, _) => OpenCardBoard(project, AssetsPart, task.Asset.Id);
        return button;
    }
}
