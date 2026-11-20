using System.Globalization;
using System.IO;
using System.Text.Json;
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
/// has to be true for them to be done. The page shows them by milestone, each folding away under
/// its header, or by when they're due. Finished tasks are left out unless asked for. The page is
/// read away from the window, and only the lines in view are built.
/// </summary>
public partial class MainWindow
{
    private const string TasksPage = "Tasks";
    private const double TasksWidth = 900;

    private sealed record AssetTask(BoardCard Asset, AssetDetails Details, DateOnly Due, bool Done, string What);

    private bool _tasksByDate; // Or by milestone, the way the page first shows them.

    // Dates are days on this PC's calendar, not moments, so one due today is due all day.
    internal static DateOnly? DueOf(AssetDetails details) => DayOf(details.Due);

    private static DateOnly? DayOf(string? text) =>
        DateOnly.TryParseExact(text ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static string? DayText(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string DueText(DateOnly due) => due.ToString(due.Year == DateTime.Today.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>
    /// What the Tasks page shows, read at once: both kinds of task, the milestones, the cards tasks
    /// can be about, and on this PC which milestones are open and whether finished tasks show.
    /// </summary>
    private sealed record TasksData(
        string Stage,
        List<AssetTask> Assets,
        List<(string Id, ProjectTask Task)> Made,
        List<(string Id, Milestone Milestone)> Milestones,
        Dictionary<string, (BoardCard Card, string Page)> Cards,
        Dictionary<string, bool> Open,
        bool ShowFinished)
    {
        public int DoneCount => Assets.Count(task => task.Done) + Made.Count(each => each.Task.Done);

        public int AllCount => Assets.Count + Made.Count;
    }

    private TasksData ReadTasks(string projectId, string stage)
    {
        var stageCards = _database.ListStageCards(projectId, stage);
        var owners = OwnerIndex(stageCards);
        var details = _database.ListAssetDetails(projectId);
        var delivered = _database.DeliveredAssets(projectId);
        var assets = (stageCards.GetValueOrDefault(AssetsPart) ?? [])
            .Select(card => (Card: card, Details: details.GetValueOrDefault(card.Id) ?? new AssetDetails()))
            .Where(each => DueOf(each.Details) is not null)
            .Select(each => new AssetTask(each.Card, each.Details, DueOf(each.Details)!.Value, AssetStatus.Of(each.Details, delivered.Contains(each.Card.Id)) == AssetStatus.Delivered, AssetWhat(each.Details, owners)))
            .OrderBy(task => task.Due)
            .ThenBy(task => task.Asset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new TasksData(stage, assets, _database.ListTasks(projectId, stage), _database.ListMilestones(projectId, stage), owners, MilestonesOpen(projectId), ShowsFinished(projectId));
    }

    // What an asset is, and what it belongs to.
    private static string AssetWhat(AssetDetails details, Dictionary<string, (BoardCard Card, string Page)> owners)
    {
        var what = AssetTypeName(details.Type) ?? "No type";
        if (details.Owner is { } ownerId && owners.TryGetValue(ownerId, out var owner)) what += $" · {CardName(owner.Card, owner.Page)}";
        return what;
    }

    /// <summary>How many tasks are done, of how many, of both kinds, in the stage the project is at.</summary>
    internal (int Done, int All) TaskCounts(Project project)
    {
        var data = ReadTasks(project.Id, _database.ProjectStage(project.Id));
        return (data.DoneCount, data.AllCount);
    }

    private void OpenTasks(Project project)
    {
        _openSection = (project.Id, TasksPage);
        _openCard = null;
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
    }

    // Kept on this PC, for each project: which milestones were opened or closed, and whether finished tasks show.

    private static string OpenMilestonesSetting(string projectId) => $"tasks_milestones_open:{projectId}";

    private static string ShowFinishedSetting(string projectId) => $"tasks_show_finished:{projectId}";

    private Dictionary<string, bool> MilestonesOpen(string projectId)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(_database.GetSetting(OpenMilestonesSetting(projectId)) is { Length: > 0 } kept ? kept : "{}") ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void SetMilestoneOpen(string projectId, string milestoneId, bool open) =>
        _database.ChangeSetting(OpenMilestonesSetting(projectId), kept =>
        {
            Dictionary<string, bool> choices;
            try
            {
                choices = JsonSerializer.Deserialize<Dictionary<string, bool>>(kept is { Length: > 0 } ? kept : "{}") ?? [];
            }
            catch (JsonException)
            {
                choices = [];
            }
            choices[milestoneId] = open;
            return JsonSerializer.Serialize(choices);
        });

    private bool ShowsFinished(string projectId) => _database.GetSetting(ShowFinishedSetting(projectId)) == "1";

    // Open until it's closed. One whose tasks are all done starts closed, as there's nothing left in it to do.
    private static bool IsOpen(TasksData data, string milestoneId, int done, int count) =>
        data.Open.TryGetValue(milestoneId, out var open) ? open : !(count > 0 && done == count);

    // The page

    /// <summary>The Tasks page of a stage as last read, and the list showing it, kept so it keeps its place.</summary>
    private sealed class TasksPageView(string projectId, LazyList list)
    {
        public string ProjectId { get; } = projectId;

        public LazyList List { get; } = list;

        public StackPanel Heading { get; } = new() { MaxWidth = TasksWidth, Margin = new Thickness(40, 22, 40, 0) };

        public TasksData? Data { get; set; }
    }

    private readonly Dictionary<string, TasksPageView> _tasksPageViews = [];

    private void AddTasksPage(StackPanel body, Project project)
    {
        var stage = _database.ProjectStage(project.Id);
        var key = $"{project.Id}|{stage}";
        if (!_tasksPageViews.TryGetValue(key, out var view)) view = _tasksPageViews[key] = new TasksPageView(project.Id, new LazyList());

        // Edge to edge, so the list scrolls at the window's edge, with the tasks in the middle.
        body.MaxWidth = double.PositiveInfinity;
        body.Margin = new Thickness(0);
        (view.Heading.Parent as Panel)?.Children.Remove(view.Heading);
        body.Children.Add(view.Heading);
        AddFittedList(body, view.List);

        // What was read last time shows straight away, and what's read now replaces it.
        FillTasksHeading(project, view);
        ShowTasks(project, view);
        LoadPage($"tasks|{key}", () => ReadTasks(project.Id, stage), data =>
        {
            // A read that began before the person flipped "show finished" mustn't flip it back.
            view.Data = data with { ShowFinished = ShowsFinished(project.Id) };
            if (_openProject?.Id != project.Id) return;
            FillTasksHeading(project, view);
            ShowTasks(project, view);
        });
    }

    private void FillTasksHeading(Project project, TasksPageView view)
    {
        var data = view.Data;
        var stage = data?.Stage ?? _database.ProjectStage(project.Id);
        var addTask = ControlButton("Add task", () => { });
        addTask.Click += (_, _) => OpenTaskForm(addTask, project, stage, null, null);
        var addMilestone = TextButton("Add milestone", () => { });
        addMilestone.Click += (_, _) => OpenMilestoneForm(addMilestone, project, stage, null);
        var byDate = TextButton(_tasksByDate ? "By milestone" : "By date", () =>
        {
            _tasksByDate = !_tasksByDate;
            FillTasksHeading(project, view);
            ShowTasks(project, view);
        });
        AutomationProperties.SetName(byDate, _tasksByDate ? "Show by milestone" : "Show by date");

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

        // Finished tasks are left out until asked for, and that's kept for the project.
        if (data is { DoneCount: > 0 })
        {
            var finished = TextButton(data.ShowFinished ? "Hide finished" : $"Show {data.DoneCount} finished", () =>
            {
                var show = !ShowsFinished(project.Id);
                _database.SetSetting(ShowFinishedSetting(project.Id), show ? "1" : "");
                if (view.Data is { } shown) view.Data = shown with { ShowFinished = show };
                FillTasksHeading(project, view);
                ShowTasks(project, view);
            });
            AutomationProperties.SetName(finished, data.ShowFinished ? "Hide finished tasks" : "Show finished tasks");
            actions.Children.Add(finished);
        }
        actions.Children.Add(byDate);
        actions.Children.Add(addMilestone);
        actions.Children.Add(addTask);
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
                new TextBlock { Text = data is not { AllCount: > 0 } ? "" : $"{data.DoneCount} of {data.AllCount} done", Foreground = (Brush)FindResource("TextMuted"), FontSize = 15, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 4) },
            },
        });
        view.Heading.Children.Clear();
        view.Heading.Children.Add(header);
        if (data is { AllCount: 0, Milestones.Count: 0 })
        {
            view.Heading.Children.Add(new TextBlock
            {
                Text = "No tasks yet. Add one, or a milestone to gather them under. An asset with a date to deliver it by is a task here too.",
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }
    }

    private void ShowTasks(Project project, TasksPageView view)
    {
        if (view.Data is not { } data) return;
        var lines = _tasksByDate ? TasksByDate(project, view, data) : TasksByMilestone(project, view, data);
        if (!data.ShowFinished && data.AllCount > 0 && data.DoneCount == data.AllCount) lines.Add(NoteLine("all-done", "Every task is done. Show finished to see them.", TasksWidth));
        lines.Add(EndLine());
        view.List.Show(lines);
        if (_taskToFocus is { } focus && focus.Until > DateTime.UtcNow) FocusTask(view, focus.Id);
    }

    private List<ListLine> TasksByMilestone(Project project, TasksPageView view, TasksData data)
    {
        var lines = new List<ListLine>();
        var milestoneIds = data.Milestones.Select(each => each.Id).ToHashSet();
        foreach (var (id, milestone) in data.Milestones)
        {
            var inIt = data.Made.Where(each => each.Task.Milestone == id).ToList();
            var done = inIt.Count(each => each.Task.Done);
            var open = IsOpen(data, id, done, inIt.Count);
            lines.Add(new ListLine($"m:{id}", $"{milestone}|{done}|{inIt.Count}|{open}|{data.Milestones.Count}", () => InPage(MilestoneHeader(project, view, data, id, milestone, done, inIt.Count, open), TasksWidth)));
            if (!open) continue;

            if (milestone.Goal is { Length: > 0 } goal) lines.Add(new ListLine($"goal:{id}", goal, () => InPage(Note(goal, 0), TasksWidth)));
            if (milestone.Notes is { Length: > 0 } notes) lines.Add(new ListLine($"notes:{id}", notes, () => InPage(Note($"Notes: {notes}", 0), TasksWidth)));
            if (milestone.Handoff is { } handoff) lines.Add(new ListLine($"handoff:{id}", handoff.ToString(), () => InPage(Note($"Handoff note, {HandoffShown(handoff)}", 0), TasksWidth)));
            var shown = data.ShowFinished ? inIt : inIt.Where(each => !each.Task.Done).ToList();
            foreach (var (taskId, task) in shown) lines.Add(TaskLine(project, view, data, taskId, task, overdue: false));
            if (shown.Count == 0 && inIt.Count > 0)
            {
                var all = inIt.Count == 1 ? "Its task is done." : $"All {inIt.Count} of its tasks are done.";
                lines.Add(new ListLine($"finished:{id}", all, () => InPage(Note(all, 0), TasksWidth)));
            }
            if (milestone.DoneWhen is { Length: > 0 } doneWhen) lines.Add(new ListLine($"when:{id}", doneWhen, () => InPage(Note($"Done when: {doneWhen}", 6), TasksWidth)));
        }

        var loose = data.Made.Where(each => each.Task.Milestone is not { } milestoneId || !milestoneIds.Contains(milestoneId)).Where(each => data.ShowFinished || !each.Task.Done).ToList();
        if (loose.Count > 0)
        {
            var title = milestoneIds.Count > 0 ? "Other tasks" : "Tasks";
            lines.Add(new ListLine("loose", title, () =>
            {
                // A task dropped on it leaves its milestone.
                var looseHeading = new Border { Child = Heading(title), BorderBrush = (Brush)FindResource("Accent"), Background = Brushes.Transparent };
                DropOn(looseHeading, TaskDrag, (moved, _) => PlaceTask(project, data.Stage, moved, null, int.MaxValue));
                return InPage(looseHeading, TasksWidth);
            }));
            foreach (var (taskId, task) in loose) lines.Add(TaskLine(project, view, data, taskId, task, overdue: false));
        }

        var assets = data.Assets.Where(task => data.ShowFinished || !task.Done).ToList();
        if (assets.Count > 0)
        {
            lines.Add(new ListLine("assets", "", () => InPage(Heading("Assets to deliver"), TasksWidth)));
            foreach (var task in assets) lines.Add(AssetTaskLine(project, task, overdue: !task.Done && task.Due < Today));
        }
        return lines;
    }

    private List<ListLine> TasksByDate(Project project, TasksPageView view, TasksData data)
    {
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
        var milestoneAt = data.Milestones.Select((each, at) => (each.Id, at)).ToDictionary(each => each.Id, each => each.at);
        var milestoneName = data.Milestones.ToDictionary(each => each.Id, each => each.Milestone.Name);
        var lines = new List<ListLine>();
        foreach (var (name, holds) in groups)
        {
            if (name == "Done" && !data.ShowFinished) continue;
            var rows = new List<(DateOnly? Due, int Milestone, double Order, ListLine Line)>();
            foreach (var (id, task) in data.Made.Where(each => holds(DayOf(each.Task.Due), each.Task.Done)))
            {
                var inMilestone = task.Milestone is { } milestoneId && milestoneAt.TryGetValue(milestoneId, out var at) ? at : int.MaxValue;
                var shownIn = task.Milestone is { } shown && milestoneName.TryGetValue(shown, out var called) ? called : null;
                rows.Add((DayOf(task.Due), inMilestone, task.Order, TaskLine(project, view, data, id, task, overdue: name == "Overdue", shownIn)));
            }
            foreach (var task in data.Assets.Where(each => holds(each.Due, each.Done)))
            {
                rows.Add((task.Due, int.MaxValue, double.MaxValue, AssetTaskLine(project, task, overdue: name == "Overdue")));
            }
            if (rows.Count == 0) continue;

            lines.Add(new ListLine($"group:{name}", name, () =>
            {
                var heading = Heading(name);
                if (name == "Overdue") heading.Foreground = (Brush)FindResource("Danger");
                return InPage(heading, TasksWidth);
            }));
            lines.AddRange(rows.OrderBy(each => each.Due ?? DateOnly.MaxValue).ThenBy(each => each.Milestone).ThenBy(each => each.Order).Select(each => each.Line));
        }
        return lines;
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

    // A milestone's header: a click on its name opens or closes it, and closed it still says how far
    // along it is. Dragged by it, or moved with Alt+Up and Alt+Down from its buttons. A task dropped
    // on it goes to its end.
    private Border MilestoneHeader(Project project, TasksPageView view, TasksData data, string id, Milestone milestone, int done, int count, bool open)
    {
        var stage = data.Stage;
        var edit = TextButton("Edit", () => { });
        edit.Click += (_, _) => OpenMilestoneForm(edit, project, stage, (id, milestone));
        AutomationProperties.SetName(edit, $"Edit {milestone.Name}");
        var add = TextButton("Add task", () => { });
        add.Click += (_, _) => OpenTaskForm(add, project, stage, null, id);
        AutomationProperties.SetName(add, $"Add a task to {milestone.Name}");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { add, edit } };

        var finished = count > 0 && done == count;
        var chevron = AgentIcons.Line(open ? "chevron-down" : "chevron-right", 16, (Brush)FindResource("TextMuted"), 2);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Margin = new Thickness(0, 2, 6, 0);
        var toggle = new Button
        {
            Style = (Style)FindResource("HeaderButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 2, 8, 2),
            Margin = new Thickness(-6, 0, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    chevron,
                    new TextBlock { Text = milestone.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(finished ? "TextMuted" : "TextStrong") },
                    new TextBlock { Text = count == 0 ? "No tasks" : $"{done} of {count} done", FontSize = 13, Foreground = (Brush)FindResource(finished ? "Accent" : "TextMuted"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 2) },
                },
            },
        };
        AutomationProperties.SetName(toggle, $"{milestone.Name}, {(count == 0 ? "no tasks" : $"{done} of {count} done")}, {(open ? "open" : "closed")}");
        toggle.ToolTip = open ? "Click to close it. Drag to move it, or drop a task on it to move the task here." : "Click to open it. Drag to move it, or drop a task on it to move the task here.";
        toggle.Click += (_, _) =>
        {
            SetMilestoneOpen(project.Id, id, !open);
            if (view.Data is { } shown) view.Data = shown with { Open = new Dictionary<string, bool>(shown.Open) { [id] = !open } };
            ShowTasks(project, view);
            _ = Dispatcher.InvokeAsync(() => Buttons(view.List).FirstOrDefault(button => button.Tag as string == $"milestone:{id}")?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        toggle.Tag = $"milestone:{id}";

        var title = new DockPanel { Margin = new Thickness(0, 26, 0, 2), Background = Brushes.Transparent };
        DockPanel.SetDock(buttons, Dock.Right);
        title.Children.Add(buttons);
        title.Children.Add(toggle);
        title.PreviewKeyDown += (_, pressed) =>
        {
            if (MoveKey(pressed) is not { } by) return;
            pressed.Handled = true;
            PlaceMilestone(project, stage, id, data.Milestones.FindIndex(each => each.Id == id) + by);
        };
        DragFrom(title, MilestoneDrag, id);

        var titleFrame = new Border { Child = title, BorderBrush = (Brush)FindResource("Accent") };
        DropOn(titleFrame, TaskDrag, (moved, _) => PlaceTask(project, stage, moved, id, int.MaxValue));
        titleFrame.Drop += (_, drop) =>
        {
            if (drop.Data.GetData(MilestoneDrag) is not string moved || moved == id) return;
            drop.Handled = true;
            var others = data.Milestones.Where(each => each.Id != moved).ToList();
            PlaceMilestone(project, stage, moved, others.FindIndex(each => each.Id == id) + (drop.GetPosition(titleFrame).Y > titleFrame.ActualHeight / 2 ? 1 : 0));
        };
        titleFrame.DragOver += (_, drag) =>
        {
            if (!drag.Data.GetDataPresent(MilestoneDrag)) return;
            drag.Effects = DragDropEffects.Move;
            titleFrame.BorderThickness = drag.GetPosition(titleFrame).Y > titleFrame.ActualHeight / 2 ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
            drag.Handled = true;
        };
        return titleFrame;
    }

    // A line for a task someone made, which says everything it shows, so it's built again only when that changes.
    private ListLine TaskLine(Project project, TasksPageView view, TasksData data, string id, ProjectTask task, bool overdue, string? milestoneName = null)
    {
        var card = task.Card is { } cardId && data.Cards.TryGetValue(cardId, out var belongs) ? CardName(belongs.Card, belongs.Page) : null;
        // A list in a record says only its type, so its evidence is said by how much there is.
        return new ListLine($"task:{id}", $"{task}|{overdue}|{milestoneName}|{card}|{EvidenceCount(task.Evidence)}",() => InPage(MadeTaskRow(project, view, data, id, task, overdue, milestoneName), TasksWidth));
    }

    private ListLine AssetTaskLine(Project project, AssetTask task, bool overdue) =>
        new($"asset:{task.Asset.Id}", $"{task.Asset.Name}|{task.Due}|{task.Done}|{task.What}|{overdue}", () => InPage(AssetTaskRow(project, task, overdue), TasksWidth));

    // A task someone made: a tick to do it, and a click on the rest to change it.
    private FrameworkElement MadeTaskRow(Project project, TasksPageView view, TasksData data, string id, ProjectTask task, bool overdue, string? milestoneName = null)
    {
        var stage = data.Stage;
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
            var ticked = task with { Done = !task.Done };
            _database.SaveTask(project.Id, id, ticked);

            // Shown at once, before the page is read again.
            if (view.Data is { } shown)
            {
                view.Data = shown with { Made = [.. shown.Made.Select(each => each.Id == id ? (id, ticked) : each)] };
                FillTasksHeading(project, view);
                ShowTasks(project, view);
            }
            RedrawProject(project);
        };

        var due = DayOf(task.Due);
        var dueText = new TextBlock { Text = due is { } day ? DueText(day) : "", FontSize = 13, Foreground = (Brush)FindResource(overdue ? "Danger" : "TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = task.Title, FontSize = 14, Foreground = (Brush)FindResource(task.Done ? "TextMuted" : "TextStrong"), TextWrapping = TextWrapping.Wrap, TextDecorations = task.Done ? TextDecorations.Strikethrough : null });
        // Away from its milestone, it says which one it's in.
        var aside = string.Join(" · ", new[] { milestoneName, task.Notes?.Split('\n')[0], EvidenceCount(task.Evidence), task.Handoff is null ? null : "Handoff note" }.Where(each => each is { Length: > 0 }));
        if (aside.Length > 0)
        {
            words.Children.Add(new TextBlock { Text = aside, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        }
        var inside = new DockPanel();
        DockPanel.SetDock(dueText, Dock.Right);
        inside.Children.Add(dueText);
        inside.Children.Add(words);
        var open = new Button { Style = (Style)FindResource("HeaderButton"), Content = inside, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 4, 6, 4), Tag = id };
        AutomationProperties.SetName(open, task.Title);
        open.ToolTip = "Click to change it. Drag, or Alt+Up and Alt+Down, to move it.";
        open.Click += (_, _) => OpenTaskForm(open, project, stage, (id, task), task.Milestone);
        open.PreviewKeyDown += (_, pressed) =>
        {
            if (MoveKey(pressed) is not { } by) return;
            pressed.Handled = true;
            MoveTask(project, stage, id, by);
        };
        DragFrom(open, TaskDrag, id);

        var row = new DockPanel { Margin = new Thickness(-1, 0, -7, 1), Background = Brushes.Transparent };
        DockPanel.SetDock(tickButton, Dock.Left);
        row.Children.Add(tickButton);

        // The card it's about, which a click opens.
        if (task.Card is { } cardId && data.Cards.TryGetValue(cardId, out var belongs))
        {
            var cardName = CardName(belongs.Card, belongs.Page);
            var link = TextButton(cardName, () => OpenCardBoard(project, belongs.Page.ToLowerInvariant(), cardId));
            link.Foreground = (Brush)FindResource("Accent");
            link.FontSize = 12.5;
            link.VerticalAlignment = VerticalAlignment.Top;
            link.Margin = new Thickness(8, 2, 0, 0);
            link.ToolTip = $"Open the {OneOf(belongs.Page)} {cardName}";
            AutomationProperties.SetName(link, $"Open {cardName}, for {task.Title}");
            DockPanel.SetDock(link, Dock.Right);
            row.Children.Add(link);
        }
        row.Children.Add(open);

        // Dropped on, a task goes above or below it, in its milestone.
        var frame = new Border { Child = row, BorderBrush = (Brush)FindResource("Accent") };
        DropOn(frame, TaskDrag, (moved, below) =>
        {
            if (moved == id) return;
            var milestoneIds = MilestoneIds(project.Id, stage);
            var group = _database.ListTasks(project.Id, stage).Where(each => each.Id != moved && GroupOf(milestoneIds, each.Task) == GroupOf(milestoneIds, task)).ToList();
            var at = group.FindIndex(each => each.Id == id);
            PlaceTask(project, stage, moved, GroupOf(milestoneIds, task), at + (below ? 1 : 0));
        });
        return frame;
    }

    // Such as "2 pictures, 1 file", or nothing when it has none.
    private static string? EvidenceCount(TaskEvidence[]? evidence)
    {
        if (evidence is not { Length: > 0 }) return null;
        var pictures = evidence.Count(each => each.Picture is not null);
        var files = evidence.Length - pictures;
        return string.Join(", ", new[] { pictures > 0 ? $"{pictures} {(pictures == 1 ? "picture" : "pictures")}" : null, files > 0 ? $"{files} {(files == 1 ? "file" : "files")}" : null }.OfType<string>());
    }

    // Moving tasks and milestones: dragged, or Alt+Up and Alt+Down on what has the keyboard.

    private const string TaskDrag = "AgentOrange.Task";
    private const string MilestoneDrag = "AgentOrange.Milestone";

    private static int? MoveKey(KeyEventArgs pressed)
    {
        var key = pressed.Key == Key.System ? pressed.SystemKey : pressed.Key;
        if ((KeyState.Modifiers & ModifierKeys.Alt) == 0 || key is not (Key.Up or Key.Down)) return null;
        return key == Key.Up ? -1 : 1;
    }

    private HashSet<string> MilestoneIds(string projectId, string stage) => [.. _database.ListMilestones(projectId, stage).Select(each => each.Id)];

    // The milestone a task is shown under: its own, or none when that's gone.
    private static string? GroupOf(HashSet<string> milestoneIds, ProjectTask task) =>
        task.Milestone is { } id && milestoneIds.Contains(id) ? id : null;

    // Into a milestone (or none) at a place, the others there numbered again around it.
    private void PlaceTask(Project project, string stage, string id, string? milestone, int at)
    {
        var tasks = _database.ListTasks(project.Id, stage);
        if (tasks.FirstOrDefault(each => each.Id == id) is not { Task: not null } moved) return;
        var milestoneIds = MilestoneIds(project.Id, stage);
        var group = tasks.Where(each => each.Id != id && GroupOf(milestoneIds, each.Task) == milestone).ToList();
        group.Insert(Math.Clamp(at, 0, group.Count), (id, moved.Task with { Milestone = milestone }));
        for (var i = 0; i < group.Count; i++)
        {
            var (each, task) = group[i];
            if (task.Order != i + 1 || each == id) _database.SaveTask(project.Id, each, task with { Order = i + 1 });
        }

        // The milestone it went to opens, so it can still be seen.
        if (milestone is not null && GroupOf(milestoneIds, moved.Task) != milestone) SetMilestoneOpen(project.Id, milestone, true);
        FocusTaskAfterRedraw(id);
        RedrawProject(project);
    }

    // A place up or down, and past the end of a milestone into the next, as it's shown.
    private void MoveTask(Project project, string stage, string id, int by)
    {
        var milestoneIds = MilestoneIds(project.Id, stage);
        var groups = _database.ListMilestones(project.Id, stage).Select(each => (string?)each.Id).Append(null).ToList();
        var tasks = _database.ListTasks(project.Id, stage);
        if (tasks.FirstOrDefault(each => each.Id == id) is not { Task: not null } moving) return;
        var group = GroupOf(milestoneIds, moving.Task);
        var inGroup = tasks.Where(each => GroupOf(milestoneIds, each.Task) == group).ToList();
        var to = inGroup.FindIndex(each => each.Id == id) + by;
        if (to >= 0 && to < inGroup.Count)
        {
            PlaceTask(project, stage, id, group, to);
            return;
        }
        var next = groups.IndexOf(group) + by;
        if (next < 0 || next >= groups.Count) return;
        PlaceTask(project, stage, id, groups[next], by < 0 ? int.MaxValue : 0);
    }

    private void PlaceMilestone(Project project, string stage, string id, int at)
    {
        var milestones = _database.ListMilestones(project.Id, stage);
        if (milestones.FirstOrDefault(each => each.Id == id) is not { Milestone: not null } moved) return;
        var others = milestones.Where(each => each.Id != id).ToList();
        others.Insert(Math.Clamp(at, 0, others.Count), moved);
        for (var i = 0; i < others.Count; i++)
        {
            var (each, milestone) = others[i];
            if (milestone.Order != i + 1) _database.SaveMilestone(project.Id, each, milestone with { Order = i + 1 });
        }
        RedrawProject(project);
    }

    // A moved task keeps the keyboard, so Alt+Up can go on moving it. The page is read again
    // straight after, and when the change syncs, so the next couple of seconds' drawing gives it back too.
    private (string Id, DateTime Until)? _taskToFocus;

    private void FocusTaskAfterRedraw(string id) => _taskToFocus = (id, DateTime.UtcNow.AddSeconds(2));

    private void FocusTask(TasksPageView view, string id)
    {
        view.List.BringLineIntoView($"task:{id}");
        _ = Dispatcher.InvokeAsync(() => Buttons(view.List).FirstOrDefault(button => button.Tag as string == id)?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Pressed and moved a little, it's picked up to be dropped somewhere else.
    private static void DragFrom(FrameworkElement from, string kind, string id)
    {
        Point? pressedAt = null;
        from.PreviewMouseLeftButtonDown += (_, press) => pressedAt = press.GetPosition(from);
        from.PreviewMouseMove += (_, move) =>
        {
            if (move.LeftButton != MouseButtonState.Pressed || pressedAt is not { } start)
            {
                pressedAt = null;
                return;
            }
            if ((move.GetPosition(from) - start).Length < SystemParameters.MinimumVerticalDragDistance * 2) return;
            pressedAt = null;
            DragDrop.DoDragDrop(from, new DataObject(kind, id), DragDropEffects.Move);
        };
    }

    // A line above or below shows where it would go. Told what was dropped, and whether it was on the lower half.
    private static void DropOn(Border on, string kind, Action<string, bool> dropped)
    {
        on.AllowDrop = true;
        bool Lower(DragEventArgs drag) => drag.GetPosition(on).Y > on.ActualHeight / 2;
        on.DragOver += (_, drag) =>
        {
            // Something else being dragged is left to whatever else here takes it.
            if (!drag.Data.GetDataPresent(kind)) return;
            drag.Effects = DragDropEffects.Move;
            on.BorderThickness = Lower(drag) ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
            drag.Handled = true;
        };
        on.DragLeave += (_, _) => on.BorderThickness = new Thickness(0);
        on.Drop += (_, drop) =>
        {
            on.BorderThickness = new Thickness(0);
            if (drop.Data.GetData(kind) is not string id) return;
            drop.Handled = true;
            dropped(id, Lower(drop));
        };
    }

    private Button AssetTaskRow(Project project, AssetTask task, bool overdue)
    {
        var name = CardName(task.Asset, AssetsPage);
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
                new TextBlock { Text = $"Asset to deliver · {task.What}", FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) },
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

        var card = task?.Card;
        var (cardButton, cardList) = CardPicker(project.Id, stage, card, picked => card = picked);
        rows.Children.Add(FormLabel("Belongs to", above: 12));
        rows.Children.Add(cardButton);
        rows.Children.Add(cardList);

        // What shows its work, such as an agent's screenshots: a click opens one, and each can be taken off.
        var evidence = new List<TaskEvidence>(task?.Evidence ?? []);
        var attaching = new List<string>();
        var evidenceRows = new StackPanel();
        void ShowEvidence()
        {
            evidenceRows.Children.Clear();
            foreach (var each in evidence) evidenceRows.Children.Add(EvidenceRow(project, each, () => { evidence.Remove(each); ShowEvidence(); }));
            foreach (var file in attaching) evidenceRows.Children.Add(EvidenceRow(project, new TaskEvidence("", null, file), () => { attaching.Remove(file); ShowEvidence(); }));
        }
        ShowEvidence();
        var attach = TextButton("Attach a file", () =>
        {
            if (Dialogs.PickFile("Attach a file that shows the work", "Pictures and files|*.*", ProjectFolder(project.Id)) is not { } file) return;
            attaching.Add(file);
            ShowEvidence();
        });
        attach.HorizontalAlignment = HorizontalAlignment.Left;
        attach.ToolTip = "A picture is kept with the project, so every PC sees it. Another file is kept by its path.";
        rows.Children.Add(FormLabel("Evidence", above: 12));
        rows.Children.Add(evidenceRows);
        rows.Children.Add(attach);

        // Where the last conversation on it left off, for the next one, until it's cleared or written again.
        var handoff = task?.Handoff;
        if (handoff is not null)
        {
            var handoffRows = new StackPanel();
            var clear = TextButton("Clear", () => { });
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            clear.Click += (_, _) =>
            {
                handoff = null;
                handoffRows.Children.Clear();
                handoffRows.Children.Add(Note("Cleared once saved.", 0));
            };
            AutomationProperties.SetName(clear, "Clear the handoff note");
            handoffRows.Children.Add(Note(HandoffShown(handoff), 0));
            handoffRows.Children.Add(clear);
            rows.Children.Add(FormLabel("Handoff note", above: 12));
            rows.Children.Add(handoffRows);
        }

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
            if (attaching.Count > 0)
            {
                try
                {
                    evidence.AddRange(KeepEvidence(project, attaching));
                    attaching.Clear();
                }
                catch (McpException problemWithFile)
                {
                    ShowProblem(problem, problemWithFile.Message.Replace("evidence: ", ""));
                    return;
                }
            }
            // What the form doesn't change, such as whether it's done, stays as it is.
            var saved = (task ?? new ProjectTask(stage, typed)) with { Title = typed, Notes = notesText.Length > 0 ? notesText : null, Due = DayText(due), Milestone = chosen, Order = order, Card = card, Evidence = evidence.Count > 0 ? [.. evidence] : null, Handoff = handoff };
            foreach (var picture in (task?.Evidence ?? []).Except(evidence).Select(each => each.Picture).OfType<string>()) _database.DeleteDocumentPicture(picture);
            _database.SaveTask(project.Id, existing?.Id, saved);

            // A task added to a closed milestone opens it, so the task can be seen.
            if (chosen is not null && chosen != task?.Milestone) SetMilestoneOpen(project.Id, chosen, true);
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
            else if (cardList.IsKeyboardFocusWithin)
            {
                cardList.Visibility = Visibility.Collapsed;
                cardButton.Focus();
            }
            else
            {
                asked.IsOpen = false;
            }
        };
        asked.Opened += (_, _) => title.Focus();
        asked.IsOpen = true;
    }

    // One piece of a task's evidence in its form: a picture small, or a file by its name, which a click
    // opens, and a button to take it off.
    private DockPanel EvidenceRow(Project project, TaskEvidence evidence, Action remove)
    {
        var name = evidence.Path is { } path ? Path.GetFileName(path) : "A picture";
        FrameworkElement shown = evidence.Picture is { } picture && _database.GetDocumentPicture(picture) is { } stored && ProjectImages.ToImage(Convert.FromBase64String(stored), 240) is { } image
            ? new Image { Source = image, MaxHeight = 90, MaxWidth = 160, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }
            : new TextBlock { Text = name, FontSize = 13, Foreground = (Brush)FindResource("TextStrong"), TextTrimming = TextTrimming.CharacterEllipsis };
        var open = new Button { Style = (Style)FindResource("HeaderButton"), Content = shown, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(2) };
        open.ToolTip = evidence.Path ?? "A picture";
        AutomationProperties.SetName(open, $"Open {name}");
        open.Click += (_, _) => OpenEvidence(project, evidence);
        var off = TextButton("Remove", remove);
        off.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(off, $"Remove {name}");
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(off, Dock.Right);
        row.Children.Add(off);
        row.Children.Add(open);
        return row;
    }

    // A picture opens in whatever opens pictures on this PC, from a copy in the temporary folder, and a file as it is.
    private void OpenEvidence(Project project, TaskEvidence evidence)
    {
        string? file = null;
        if (evidence.Picture is { } picture && _database.GetDocumentPicture(picture) is { } stored)
        {
            var bytes = Convert.FromBase64String(stored);
            var folder = Path.Combine(Path.GetTempPath(), "AgentOrange-Evidence");
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, $"{Short(picture)}{(bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? ".png" : ".jpg")}");
            File.WriteAllBytes(file, bytes);
        }
        else if (evidence.Path is { } path)
        {
            file = Path.IsPathRooted(path) ? path : ProjectFolder(project.Id) is { } inFolder ? Path.Combine(inFolder, path) : null;
        }
        if (file is null || !File.Exists(file))
        {
            Dialogs.Notify(evidence.Path is { } missing ? $"{missing} isn't on this PC." : "That picture isn't on this PC yet.");
            return;
        }
#if DEBUG
        if (Headless.Active) return;
#endif
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialogs.Notify($"Nothing on this PC opens {Path.GetFileName(file)}.");
        }
    }

    // As the Tasks page and forms show a handoff note: when, who from, and what it says.
    private static string HandoffShown(HandoffNote note) =>
        $"{HandoffWhen(note)}{(note.From is { Length: > 0 } from ? $", from {from}" : "")}\n{note.Text.Trim()}";

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
        var notes = Box("Notes", milestone?.Notes, lines: true);
        var handoff = milestone?.Handoff;
        if (handoff is not null)
        {
            var handoffRows = new StackPanel();
            var clear = TextButton("Clear", () => { });
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            clear.Click += (_, _) =>
            {
                handoff = null;
                handoffRows.Children.Clear();
                handoffRows.Children.Add(Note("Cleared once saved.", 0));
            };
            AutomationProperties.SetName(clear, "Clear the handoff note");
            handoffRows.Children.Add(Note(HandoffShown(handoff), 0));
            handoffRows.Children.Add(clear);
            rows.Children.Add(FormLabel("Handoff note", above: 12));
            rows.Children.Add(handoffRows);
        }

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
            _database.SaveMilestone(project.Id, existing?.Id, (milestone ?? new Milestone(stage, typed)) with { Name = typed, Goal = Kept(goal), DoneWhen = Kept(doneWhen), Order = order, Notes = Kept(notes), Handoff = handoff });
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
