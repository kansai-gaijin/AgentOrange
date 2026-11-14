using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// A project's tasks, in the stage it's at. Two kinds: ones people make, with notes, a day to have
/// them done by and a milestone, ticked off by hand; and assets with a date to deliver them by, done
/// once delivered, which only a person does. Milestones hold tasks, with what they're for and what
/// has to be true for them to be done. The page shows them by milestone, or by when they're due.
/// </summary>
public partial class MainWindow
{
    private const string TasksPage = "Tasks";

    private sealed record AssetTask(BoardCard Asset, AssetDetails Details, DateOnly Due, bool Done);

    private bool _tasksByDate; // Or by milestone, the way the page first shows them.

    // Dates are days on this PC's calendar, not moments, so one due today is due all day.
    internal static DateOnly? DueOf(AssetDetails details) => DayOf(details.Due);

    private static DateOnly? DayOf(string? text) =>
        DateOnly.TryParseExact(text ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static string? DayText(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string DueText(DateOnly due) => due.ToString(due.Year == DateTime.Today.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private List<AssetTask> AssetTasks(Project project) =>
    [
        .. _database.ListCards(project.Id, _database.ProjectStage(project.Id), AssetsPart)
            .Select(card => (Card: card, Details: _database.GetAssetDetails(card.Id)))
            .Where(each => DueOf(each.Details) is not null)
            .Select(each => new AssetTask(each.Card, each.Details, DueOf(each.Details)!.Value, _database.GetAssetDeliverable(each.Card.Id) is not null))
            .OrderBy(task => task.Due)
            .ThenBy(task => task.Asset.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    private List<(string Id, ProjectTask Task)> MadeTasks(Project project) => _database.ListTasks(project.Id, _database.ProjectStage(project.Id));

    /// <summary>How many tasks are done, of how many, of both kinds, in the stage the project is at.</summary>
    internal (int Done, int All) TaskCounts(Project project)
    {
        var assets = AssetTasks(project);
        var made = MadeTasks(project);
        return (assets.Count(task => task.Done) + made.Count(each => each.Task.Done), assets.Count + made.Count);
    }

    private void OpenTasks(Project project)
    {
        _openSection = (project.Id, TasksPage);
        _openCard = null;
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
    }

    // The page

    private void AddTasksPage(StackPanel body, Project project)
    {
        var stage = _database.ProjectStage(project.Id);
        var assets = AssetTasks(project);
        var made = MadeTasks(project);
        var milestones = _database.ListMilestones(project.Id, stage);
        var (doneCount, allCount) = TaskCounts(project);
        body.MaxWidth = 900;
        body.Margin = new Thickness(40, 22, 40, 30);

        var addTask = ControlButton("Add task", () => { });
        addTask.Click += (_, _) => OpenTaskForm(addTask, project, stage, null, null);
        var addMilestone = TextButton("Add milestone", () => { });
        addMilestone.Click += (_, _) => OpenMilestoneForm(addMilestone, project, stage, null);
        var view = TextButton(_tasksByDate ? "By milestone" : "By date", () =>
        {
            _tasksByDate = !_tasksByDate;
            RedrawProject(project);
        });
        AutomationProperties.SetName(view, _tasksByDate ? "Show by milestone" : "Show by date");

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { view, addMilestone, addTask } };
        foreach (var each in actions.Children.OfType<FrameworkElement>()) each.Margin = new Thickness(8, 0, 0, 0);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = TasksPage, Foreground = (Brush)FindResource("TextStrong"), FontSize = 24, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = allCount == 0 ? "" : $"{doneCount} of {allCount} done", Foreground = (Brush)FindResource("TextMuted"), FontSize = 15, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 4) },
            },
        });
        body.Children.Add(header);
        if (allCount == 0 && milestones.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "No tasks yet. Add one, or a milestone to gather them under. An asset with a date to deliver it by is a task here too.",
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            return;
        }

        if (_tasksByDate) AddTasksByDate(body, project, assets, made, milestones);
        else AddTasksByMilestone(body, project, stage, assets, made, milestones);
    }

    private void AddTasksByMilestone(StackPanel body, Project project, string stage, List<AssetTask> assets, List<(string Id, ProjectTask Task)> made, List<(string Id, Milestone Milestone)> milestones)
    {
        foreach (var (id, milestone) in milestones)
        {
            var inIt = made.Where(each => each.Task.Milestone == id).ToList();
            var done = inIt.Count(each => each.Task.Done);

            var edit = TextButton("Edit", () => { });
            edit.Click += (_, _) => OpenMilestoneForm(edit, project, stage, (id, milestone));
            AutomationProperties.SetName(edit, $"Edit {milestone.Name}");
            var add = TextButton("Add task", () => { });
            add.Click += (_, _) => OpenTaskForm(add, project, stage, null, id);
            AutomationProperties.SetName(add, $"Add a task to {milestone.Name}");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { add, edit } };

            var title = new DockPanel { Margin = new Thickness(0, 26, 0, 2) };
            DockPanel.SetDock(buttons, Dock.Right);
            title.Children.Add(buttons);
            title.Children.Add(new WrapPanel
            {
                Children =
                {
                    new TextBlock { Text = milestone.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(inIt.Count > 0 && done == inIt.Count ? "TextMuted" : "TextStrong") },
                    new TextBlock { Text = inIt.Count == 0 ? "" : done == inIt.Count ? "Done" : $"{done} of {inIt.Count}", FontSize = 13, Foreground = (Brush)FindResource(inIt.Count > 0 && done == inIt.Count ? "Accent" : "TextMuted"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 2) },
                },
            });
            body.Children.Add(title);
            if (milestone.Goal is { Length: > 0 } goal) body.Children.Add(Note(goal, 0));
            foreach (var (taskId, task) in inIt) body.Children.Add(MadeTaskRow(project, stage, taskId, task, overdue: false));
            if (milestone.DoneWhen is { Length: > 0 } doneWhen) body.Children.Add(Note($"Done when: {doneWhen}", 6));
        }

        var loose = made.Where(each => each.Task.Milestone is null || milestones.All(milestone => milestone.Id != each.Task.Milestone)).ToList();
        if (loose.Count > 0)
        {
            body.Children.Add(Heading(milestones.Count > 0 ? "Other tasks" : "Tasks"));
            foreach (var (taskId, task) in loose) body.Children.Add(MadeTaskRow(project, stage, taskId, task, overdue: false));
        }
        if (assets.Count > 0)
        {
            body.Children.Add(Heading("Assets to deliver"));
            foreach (var task in assets) body.Children.Add(AssetTaskRow(project, task, overdue: !task.Done && task.Due < Today));
        }
    }

    private void AddTasksByDate(StackPanel body, Project project, List<AssetTask> assets, List<(string Id, ProjectTask Task)> made, List<(string Id, Milestone Milestone)> milestones)
    {
        var stage = _database.ProjectStage(project.Id);
        var today = Today;
        (string Name, Func<DateOnly?, bool, bool> Holds)[] groups =
        [
            ("Overdue", (due, done) => !done && due < today),
            ("Today", (due, done) => !done && due == today),
            ("This week", (due, done) => !done && due > today && due < today.AddDays(7)),
            ("Later", (due, done) => !done && due >= today.AddDays(7)),
            ("No date", (due, done) => !done && due is null),
            ("Done", (_, done) => done),
        ];
        // On the same day, or with none, tasks keep the order of their milestones and their places in them.
        var milestoneAt = milestones.Select((each, at) => (each.Id, at)).ToDictionary(each => each.Id, each => each.at);
        var milestoneName = milestones.ToDictionary(each => each.Id, each => each.Milestone.Name);
        foreach (var (name, holds) in groups)
        {
            var rows = new List<(DateOnly? Due, int Milestone, double Order, FrameworkElement Row)>();
            foreach (var (id, task) in made.Where(each => holds(DayOf(each.Task.Due), each.Task.Done)))
            {
                var inMilestone = task.Milestone is { } milestoneId && milestoneAt.TryGetValue(milestoneId, out var at) ? at : int.MaxValue;
                var shownIn = task.Milestone is { } shown && milestoneName.TryGetValue(shown, out var called) ? called : null;
                rows.Add((DayOf(task.Due), inMilestone, task.Order, MadeTaskRow(project, stage, id, task, overdue: name == "Overdue", shownIn)));
            }
            foreach (var task in assets.Where(each => holds(each.Due, each.Done)))
            {
                rows.Add((task.Due, int.MaxValue, double.MaxValue, AssetTaskRow(project, task, overdue: name == "Overdue")));
            }
            if (rows.Count == 0) continue;

            var heading = Heading(name);
            if (name == "Overdue") heading.Foreground = (Brush)FindResource("Danger");
            body.Children.Add(heading);
            foreach (var row in rows.OrderBy(each => each.Due ?? DateOnly.MaxValue).ThenBy(each => each.Milestone).ThenBy(each => each.Order)) body.Children.Add(row.Row);
        }
    }

    private TextBlock Heading(string text)
    {
        var heading = Section(text);
        heading.Margin = new Thickness(0, 26, 0, 6);
        return heading;
    }

    private TextBlock Note(string text, double above) => new()
    {
        Text = text,
        FontSize = 13,
        Foreground = (Brush)FindResource("TextMuted"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, above, 0, 6),
    };

    // A task someone made: a tick to do it, and a click on the rest to change it.
    private FrameworkElement MadeTaskRow(Project project, string stage, string id, ProjectTask task, bool overdue, string? milestoneName = null)
    {
        var tick = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.4),
            BorderBrush = (Brush)FindResource(task.Done ? "Accent" : "TextMuted"),
            Background = task.Done ? (Brush)FindResource("Accent") : Brushes.Transparent,
            Child = task.Done ? new TextBlock { Text = "✓", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("ContentBackground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
        };
        var tickButton = new Button { Style = (Style)FindResource("HeaderButton"), Content = tick, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-6, 0, 4, 0) };
        AutomationProperties.SetName(tickButton, $"{(task.Done ? "Not done" : "Done")}: {task.Title}");
        tickButton.Click += (_, _) =>
        {
            _database.SaveTask(project.Id, id, task with { Done = !task.Done });
            RedrawProject(project);
        };

        var due = DayOf(task.Due);
        var dueText = new TextBlock { Text = due is { } day ? DueText(day) : "", FontSize = 13, Foreground = (Brush)FindResource(overdue ? "Danger" : "TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = task.Title, FontSize = 14, Foreground = (Brush)FindResource(task.Done ? "TextMuted" : "TextStrong"), TextWrapping = TextWrapping.Wrap, TextDecorations = task.Done ? TextDecorations.Strikethrough : null });
        // Away from its milestone, it says which one it's in.
        var aside = string.Join(" · ", new[] { milestoneName, task.Notes?.Split('\n')[0] }.Where(each => each is { Length: > 0 }));
        if (aside.Length > 0)
        {
            words.Children.Add(new TextBlock { Text = aside, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        }
        var inside = new DockPanel();
        DockPanel.SetDock(dueText, Dock.Right);
        inside.Children.Add(dueText);
        inside.Children.Add(words);
        var open = new Button { Style = (Style)FindResource("HeaderButton"), Content = inside, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 4, 6, 4) };
        AutomationProperties.SetName(open, task.Title);
        open.Click += (_, _) => OpenTaskForm(open, project, stage, (id, task), task.Milestone);

        var row = new DockPanel { Margin = new Thickness(-1, 0, -7, 1) };
        DockPanel.SetDock(tickButton, Dock.Left);
        row.Children.Add(tickButton);
        row.Children.Add(open);
        return row;
    }

    private Button AssetTaskRow(Project project, AssetTask task, bool overdue)
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
            Margin = new Thickness(3, 0, 13, 0),
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
                new TextBlock { Text = $"Asset to deliver · {what}", FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) },
            },
        });

        var button = new Button { Style = (Style)FindResource("HeaderButton"), Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(-7, 0, -7, 2) };
        AutomationProperties.SetName(button, $"{name}, due {DueText(task.Due)}{(task.Done ? ", delivered" : overdue ? ", overdue" : "")}");
        button.Click += (_, _) => OpenCardBoard(project, AssetsPart, task.Asset.Id);
        return button;
    }

    // Forms

    /// <summary>Adds a task, or changes one: what it is, notes, the day to have it done by and its milestone.</summary>
    private void OpenTaskForm(UIElement by, Project project, string stage, (string Id, ProjectTask Task)? existing, string? milestoneId)
    {
        var task = existing?.Task;
        var milestones = _database.ListMilestones(project.Id, stage);
        var rows = new StackPanel { Width = 340, Margin = new Thickness(8, 6, 8, 6) };
        var asked = NewMenu(by, atMouse: false, Scrolling(rows));
        var problem = ProblemText();
        problem.Margin = new Thickness(0, 6, 0, 0);

        rows.Children.Add(new TextBlock { Text = task is null ? "New task" : "Task", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 10) });

        var title = Field(task?.Title ?? "");
        title.Width = double.NaN;
        AutomationProperties.SetName(title, "Task");
        rows.Children.Add(FormLabel("What's to be done"));
        rows.Children.Add(title);

        var notes = Field(task?.Notes ?? "");
        notes.Width = double.NaN;
        notes.AcceptsReturn = true;
        notes.TextWrapping = TextWrapping.Wrap;
        notes.MinHeight = 60;
        notes.MaxHeight = 200;
        notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        AutomationProperties.SetName(notes, "Notes");
        rows.Children.Add(FormLabel("Notes", above: 12));
        rows.Children.Add(notes);

        var due = DayOf(task?.Due);
        var (dueButton, dueList) = DayPicker("Done by", due, picked => due = picked);
        rows.Children.Add(FormLabel("Done by", above: 12));
        rows.Children.Add(dueButton);
        rows.Children.Add(dueList);

        var milestone = new ComboBox { Style = (Style)FindResource("Choice"), Width = double.NaN };
        AutomationProperties.SetName(milestone, "Milestone");
        milestone.Items.Add(new ComboBoxItem { Content = "No milestone", Tag = null });
        foreach (var (id, each) in milestones) milestone.Items.Add(new ComboBoxItem { Content = each.Name, Tag = id });
        milestone.SelectedItem = milestone.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == milestoneId) ?? milestone.Items[0];
        rows.Children.Add(FormLabel("Milestone", above: 12));
        rows.Children.Add(milestone);

        void Save()
        {
            var typed = title.Text.Trim();
            if (typed.Length == 0)
            {
                ShowProblem(problem, "Say what's to be done.");
                title.Focus();
                return;
            }
            var chosen = (string?)(milestone.SelectedItem as ComboBoxItem)?.Tag;
            var order = task is not null && task.Milestone == chosen
                ? task.Order
                : _database.ListTasks(project.Id, stage).Where(each => each.Task.Milestone == chosen).Select(each => each.Task.Order).DefaultIfEmpty(0).Max() + 1;
            var notesText = notes.Text.Trim();
            _database.SaveTask(project.Id, existing?.Id, new ProjectTask(stage, typed, notesText.Length > 0 ? notesText : null, DayText(due), task?.Done ?? false, chosen, order));
            asked.IsOpen = false;
            RedrawProject(project);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ControlButton(task is null ? "Add" : "Save", Save));
        if (existing is { } kept)
        {
            var delete = TextButton("Delete", () =>
            {
                asked.IsOpen = false;
                _database.DeleteTask(kept.Id);
                RedrawProject(project);
            });
            delete.Foreground = (Brush)FindResource("Danger");
            delete.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(delete);
        }
        rows.Children.Add(buttons);
        rows.Children.Add(problem);

        title.KeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Enter) return;
            pressed.Handled = true;
            Save();
        };
        asked.PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Escape || milestone.IsDropDownOpen) return;
            pressed.Handled = true;
            if (dueList.IsKeyboardFocusWithin)
            {
                dueList.Visibility = Visibility.Collapsed;
                dueButton.Focus();
            }
            else
            {
                asked.IsOpen = false;
            }
        };
        asked.Opened += (_, _) => title.Focus();
        asked.IsOpen = true;
    }

    /// <summary>Adds a milestone, or changes one: its name, what it's for, and when it's done.</summary>
    private void OpenMilestoneForm(UIElement by, Project project, string stage, (string Id, Milestone Milestone)? existing)
    {
        var milestone = existing?.Milestone;
        var rows = new StackPanel { Width = 340, Margin = new Thickness(8, 6, 8, 6) };
        var asked = NewMenu(by, atMouse: false, Scrolling(rows));
        var problem = ProblemText();
        problem.Margin = new Thickness(0, 6, 0, 0);

        TextBox Box(string label, string? value, bool lines)
        {
            var box = Field(value ?? "");
            box.Width = double.NaN;
            if (lines)
            {
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.MinHeight = 60;
                box.MaxHeight = 200;
                box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            AutomationProperties.SetName(box, label);
            rows.Children.Add(FormLabel(label, above: rows.Children.Count > 1 ? 12 : 0));
            rows.Children.Add(box);
            return box;
        }

        rows.Children.Add(new TextBlock { Text = milestone is null ? "New milestone" : "Milestone", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 10) });
        var name = Box("Name", milestone?.Name, lines: false);
        var goal = Box("What it's for", milestone?.Goal, lines: true);
        var doneWhen = Box("Done when", milestone?.DoneWhen, lines: true);

        static string? Kept(TextBox box) => box.Text.Trim() is { Length: > 0 } text ? text : null;

        void Save()
        {
            var typed = name.Text.Trim();
            if (typed.Length == 0)
            {
                ShowProblem(problem, "Give it a name.");
                name.Focus();
                return;
            }
            var order = milestone?.Order ?? _database.ListMilestones(project.Id, stage).Select(each => each.Milestone.Order).DefaultIfEmpty(0).Max() + 1;
            _database.SaveMilestone(project.Id, existing?.Id, new Milestone(stage, typed, Kept(goal), Kept(doneWhen), order));
            asked.IsOpen = false;
            RedrawProject(project);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ControlButton(milestone is null ? "Add" : "Save", Save));
        if (existing is { } kept)
        {
            var delete = TextButton("Delete", () =>
            {
                if (!Dialogs.Confirm($"Delete {kept.Milestone.Name}?\n\nIts tasks stay, without a milestone.")) return;
                asked.IsOpen = false;
                _database.DeleteMilestone(project.Id, kept.Id);
                RedrawProject(project);
            });
            delete.Foreground = (Brush)FindResource("Danger");
            delete.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(delete);
        }
        rows.Children.Add(buttons);
        rows.Children.Add(problem);

        name.KeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Enter) return;
            pressed.Handled = true;
            Save();
        };
        asked.PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Escape) return;
            pressed.Handled = true;
            asked.IsOpen = false;
        };
        asked.Opened += (_, _) => name.Focus();
        asked.IsOpen = true;
    }

    /// <summary>
    /// A day picked from a month that opens under its button, with the months either side a click
    /// away, and "No date". Shared by every form with a date.
    /// </summary>
    private (Button Button, Border List) DayPicker(string name, DateOnly? initial, Action<DateOnly?> picked)
    {
        var chosen = initial;
        var button = DropButton(name, out var shown);
        var list = OpenUnder();
        var month = new DateOnly((chosen ?? Today).Year, (chosen ?? Today).Month, 1);

        void Show()
        {
            shown.Text = chosen is { } day ? DueText(day) : "No date";
            shown.Foreground = (Brush)FindResource(chosen is null ? "TextMuted" : "TextStrong");
        }
        void Pick(DateOnly? day)
        {
            chosen = day;
            picked(day);
            Show();
            list.Visibility = Visibility.Collapsed;
            button.Focus();
        }
        void ShowMonth()
        {
            Button Step(string arrow, string stepName, int by)
            {
                var step = TextButton(arrow, () =>
                {
                    month = month.AddMonths(by);
                    ShowMonth();
                });
                AutomationProperties.SetName(step, stepName);
                return step;
            }
            var back = Step("‹", "Month before", -1);
            var next = Step("›", "Month after", 1);
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(back, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            header.Children.Add(back);
            header.Children.Add(next);
            header.Children.Add(new TextBlock { Text = month.ToString("MMMM yyyy", CultureInfo.InvariantCulture), FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

            // The week starts as this PC's calendar starts it.
            var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            var days = new UniformGrid { Columns = 7 };
            for (var i = 0; i < 7; i++)
            {
                var weekday = (DayOfWeek)(((int)firstDay + i) % 7);
                days.Children.Add(new TextBlock { Text = CultureInfo.InvariantCulture.DateTimeFormat.GetShortestDayName(weekday), FontSize = 11, Foreground = (Brush)FindResource("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
            }
            for (var i = 0; i < ((int)month.DayOfWeek - (int)firstDay + 7) % 7; i++) days.Children.Add(new Border());
            for (var day = month; day.Month == month.Month; day = day.AddDays(1))
            {
                var thisDay = day;
                var isChosen = thisDay == chosen;
                var number = new TextBlock { Text = thisDay.Day.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource(isChosen ? "ContentBackground" : thisDay == Today ? "Accent" : "TextNormal"), FontWeight = isChosen || thisDay == Today ? FontWeights.Bold : FontWeights.Normal };
                var dayButton = new Button
                {
                    Style = (Style)FindResource("HeaderButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new Border { Background = isChosen ? (Brush)FindResource("Accent") : Brushes.Transparent, CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 1, 0, 1), Child = number },
                };
                AutomationProperties.SetName(dayButton, thisDay.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture) + (isChosen ? ", chosen" : ""));
                dayButton.Click += (_, _) => Pick(thisDay);
                days.Children.Add(dayButton);
            }

            var none = TextButton("No date", () => Pick(null));
            none.HorizontalAlignment = HorizontalAlignment.Left;
            none.Margin = new Thickness(0, 4, 0, 0);
            list.Child = new StackPanel { Children = { header, days, none } };
        }
        button.Click += (_, _) =>
        {
            var opening = list.Visibility != Visibility.Visible;
            list.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            if (!opening) return;
            month = new DateOnly((chosen ?? Today).Year, (chosen ?? Today).Month, 1);
            ShowMonth();
            ScrollTo(list);
        };
        Show();
        return (button, list);
    }
}
