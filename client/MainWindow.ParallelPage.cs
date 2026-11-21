using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// A milestone split across agents on the Tasks page (#94): "Split across agents" on its header opens
/// how its open tasks would be split, with the login the parts start with, and starts it; once split,
/// a line under the header says how each part stands, opens its conversation, and offers Merge parts
/// once they've all stopped, or Stop while they work.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the split's lines show, so they're built again when any of it changes.</summary>
    private string SplitKey(string projectId, string milestoneId) =>
        RunOf(projectId, milestoneId) is { } run ? $"{run.Id}|{run.MergeNote}|{string.Join("|", run.Parts.Select(part => PartState(run, part).Words))}" : "";

    /// <summary>The header's button that splits the milestone, when it has tasks enough and isn't split already.</summary>
    private Button? SplitButton(Project project, string milestoneId, Milestone milestone, int openTasks)
    {
        if (openTasks < 2 || RunOf(project.Id, milestoneId) is not null) return null;
        var split = TextButton("Split across agents", () => { });
        split.Click += (_, _) => OpenSplitForm(split, project, milestoneId);
        split.ToolTip = "Its open tasks shared out among agent conversations that work at the same time, each in a Git worktree of its own. Tasks that use something in common stay in one part, one after another.";
        AutomationProperties.SetName(split, $"Split {milestone.Name} across agents");
        return split;
    }

    /// <summary>How the milestone would be split, the login its parts start with, and Start.</summary>
    private void OpenSplitForm(UIElement by, Project project, string milestoneId)
    {
        var rows = new StackPanel { Width = 420, Margin = new Thickness(8, 6, 8, 6) };
        var asked = NewMenu(by, atMouse: false, Scrolling(rows));
        var problem = ProblemText();
        problem.Margin = new Thickness(0, 6, 0, 0);

        (string Id, Milestone Milestone, List<ParallelPlan.Part> Parts) plan;
        try
        {
            plan = PlanSplit(project, milestoneId, null);
        }
        catch (McpException refused)
        {
            Dialogs.Notify(refused.Message.Replace("milestone: ", "").Replace("parts: ", ""));
            return;
        }

        rows.Children.Add(new TextBlock { Text = $"Split {plan.Milestone.Name} across agents", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 6) });
        rows.Children.Add(Note($"Each part is a conversation of its own, working at the same time as the others, at most {MostAtOnce} at once (Settings › Providers › Parallel work). Each commits on a branch of its own in a Git worktree, and the parts are merged in order once they've all stopped.", 0));
        foreach (var (part, at) in plan.Parts.Select((part, at) => (part, at)))
        {
            var where = part.InProjectFolder ? $"In the project's own folder, as {project.Engine} opens the project there" : "In a worktree of its own";
            var uses = part.Uses.Count > 0 ? $" · uses {string.Join(", ", part.Uses)}" : "";
            rows.Children.Add(FormLabel($"Part {at + 1} · {where}{uses}", above: 10));
            foreach (var task in part.Tasks) rows.Children.Add(new TextBlock { Text = task.Title, FontSize = 13.5, Foreground = (Brush)FindResource("TextStrong"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 3) });
        }
        if (plan.Parts.Count < 2)
        {
            rows.Children.Add(Note("Its tasks all use something in common, so they'd go one after another in one part anyway. Work on it in one conversation, or take off the uses of the tasks that are independent.", 10));
            asked.IsOpen = true;
            return;
        }

        var logins = AllAgentLogins();
        var login = new ComboBox { Style = (Style)FindResource("Choice"), Width = double.NaN };
        AutomationProperties.SetName(login, "Login");
        foreach (var each in logins) login.Items.Add(new ComboBoxItem { Content = each.Label, Tag = each.Id });
        login.SelectedItem = login.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == DefaultAgentProvider()?.Id) ?? login.Items.OfType<ComboBoxItem>().FirstOrDefault();
        rows.Children.Add(FormLabel("Each part starts with", above: 12));
        if (logins.Count > 0) rows.Children.Add(login);
        else rows.Children.Add(Note("No login yet: add a Claude Code or Codex login in Settings, under Agents.", 0));

        var start = ControlButton("Start", () => { });
        start.IsEnabled = logins.Count > 0;
        start.Click += async (_, _) =>
        {
            start.IsEnabled = false;
            var chosen = logins.FirstOrDefault(each => each.Id == (string?)(login.SelectedItem as ComboBoxItem)?.Tag);
            var (run, why) = await StartSplitAsync(project, milestoneId, null, null, chosen);
            if (run is null)
            {
                ShowProblem(problem, why ?? "It couldn't be split.");
                start.IsEnabled = true;
                return;
            }
            asked.IsOpen = false;
            SetMilestoneOpen(project.Id, milestoneId, true);
            if (_openProject?.Id == project.Id) RenderProject(project);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { start } };
        var cancel = TextButton("Cancel", () => asked.IsOpen = false);
        cancel.Margin = new Thickness(10, 0, 0, 0);
        buttons.Children.Add(cancel);
        rows.Children.Add(buttons);
        rows.Children.Add(problem);
        asked.IsOpen = true;
    }

    /// <summary>The lines under a split milestone's header: how each part stands, and what can be done with them.</summary>
    private IEnumerable<ListLine> SplitLines(Project project, string milestoneId)
    {
        if (RunOf(project.Id, milestoneId) is not { } run) yield break;
        yield return new ListLine($"split:{milestoneId}", SplitKey(project.Id, milestoneId), () => InPage(SplitPanel(project, run), TasksWidth));
    }

    private FrameworkElement SplitPanel(Project project, ParallelRun run)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (ReadyToMerge(run))
        {
            var merge = ControlButton("Merge parts", () => { });
            merge.Click += async (_, _) =>
            {
                merge.IsEnabled = false;
                await MergeSplitAsync(run);
                if (_openProject?.Id == project.Id) RenderProject(project);
            };
            merge.ToolTip = $"Merges each part's branch into {run.BaseBranch}, in order, and stops at a conflict.";
            AutomationProperties.SetName(merge, $"Merge the parts of {run.MilestoneName}");
            actions.Children.Add(merge);
        }
        else if (run.Parts.Any(part => !PartState(run, part).Over))
        {
            var stop = TextButton("Stop", () => StopSplit(run));
            stop.ToolTip = "Parts not started never are, and parts at work are asked to stop. What they committed stays on their branches.";
            AutomationProperties.SetName(stop, $"Stop the parts of {run.MilestoneName}");
            actions.Children.Add(stop);
        }
        DockPanel.SetDock(actions, Dock.Right);
        heading.Children.Add(actions);
        heading.Children.Add(new TextBlock { Text = $"Split across {run.Parts.Count} agents, from {run.BaseBranch}", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(heading);

        foreach (var part in run.Parts)
        {
            var (words, _, _) = PartState(run, part);
            var where = part.InProjectFolder ? "in the project's folder" : part.Branch ?? "";
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            if (PartConversation(part) is { } conversation)
            {
                var open = TextButton("Open", () =>
                {
                    OpenAgents_Click(this, new RoutedEventArgs());
                    _agentsWindow?.OpenConversation(conversation);
                });
                open.FontSize = 12.5;
                AutomationProperties.SetName(open, $"Open part {part.Number} of {run.MilestoneName}");
                DockPanel.SetDock(open, Dock.Right);
                row.Children.Add(open);
            }
            row.Children.Add(new TextBlock
            {
                Text = $"Part {part.Number} · {words} · {where}",
                FontSize = 13,
                Foreground = (Brush)FindResource(words.StartsWith("Needs you", StringComparison.Ordinal) ? "Accent" : "TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = part.Worktree ?? ProjectFolder(project.Id),
            });
            panel.Children.Add(row);
        }
        if (run.MergeNote is { Length: > 0 } note) panel.Children.Add(Note(note, 4));
        return panel;
    }
}
