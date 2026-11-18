using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// "Run tests on…" on a project's page (#93): sends the project's tests, at the commit its folder
/// has checked out, to one of the machines this PC is linked to, and opens the run's window.
/// </summary>
public partial class MainWindow
{
    private TestCommandsWindow? _approvalWindow;

    // On the PC running them: a window asking a person to allow a run's commands, closed if the
    // run stops waiting some other way, such as from the settings page or a cancel.
    private void WatchTestApprovals()
    {
        _testMachines.ApprovalNeeded += request =>
        {
            _approvalWindow?.Close();
            var window = TestCommandsWindow.ToRunHere(request);
            _approvalWindow = window;
            _ = AnswerWhenToldAsync(window, request.RunId);
            window.Show();
#if DEBUG
            if (!Headless.Active)
#endif
            window.Activate();
        };
        _testMachines.ApprovalEnded += () =>
        {
            var window = _approvalWindow;
            _approvalWindow = null;
            window?.Close();
        };
    }

    private async Task AnswerWhenToldAsync(TestCommandsWindow window, string runId) =>
        _testMachines.AnswerApproval(runId, await window.Result);

    // Into the row of what can be done with the folder, beside the git actions.
    private void AddTestRunButton(Panel row, Project project, string folder)
    {
        if (!_database.GetTestSetup(project.Id).CanRun) return;
        var run = ActionButton("Run tests on…");
        run.VerticalAlignment = VerticalAlignment.Center;
        run.Margin = new Thickness(row.Children.Count > 0 ? 8 : 0, 0, 0, 0);
        run.IsEnabled = !IsBusyWith(project.Id);
        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            var recent = await _devices.RunsAsync(project.Id);
            run.IsEnabled = true;
            OpenTestMachineMenu(run, project, folder, recent.Value ?? []);
        };
        row.Children.Add(run);
    }

    // A list under the button, as the git actions' menu is: one line a linked machine, then earlier runs.
    private void OpenTestMachineMenu(Button button, Project project, string folder, List<TestRunInfo> recent)
    {
        var setup = _database.GetTestSetup(project.Id);
        var items = new StackPanel { MinWidth = 260 };
        var menu = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)FindResource("SidebarBackground"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 4, 0, 0),
                Child = items,
            },
        };
        void Add(string text, Action? pick)
        {
            if (pick is null)
            {
                items.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(10, 6, 10, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
                return;
            }
            var item = Clickable("HeaderButton", text, () =>
            {
                menu.IsOpen = false;
                pick();
            });
            item.HorizontalAlignment = HorizontalAlignment.Stretch;
            item.HorizontalContentAlignment = HorizontalAlignment.Left;
            items.Children.Add(item);
        }

        var latest = _testMachines.Latest;
        var links = latest?.Links.Where(link => link.Linker == latest.Me).OrderBy(link => link.MachineLabel, StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];
        if (links.Count == 0) Add(_testMachines.Problem ?? "No linked machines. Link one in Settings › Test machines.", null);
        foreach (var link in links)
        {
            var missing = (setup.Tags ?? []).Where(tag => !TestMachines.HasTags(link.MachineTags, [tag])).ToList();
            var why = missing.Count > 0 ? $"doesn't offer {string.Join(", ", missing)}" : link.Status switch
            {
                "expired" => "expired",
                "unreachable" => "not answering right now",
                "busy" => "running tests, so this waits its turn",
                _ => null,
            };
            var usable = link.Status is "reachable" or "busy" && missing.Count == 0;
            Add(why is null ? link.MachineLabel : $"{link.MachineLabel} · {why}", usable ? async () => await SendTestsAsync(link, project, folder) : null);
        }

        // Earlier runs this PC sent, to open again.
        var sent = recent.Where(each => each.Linker == latest?.Me).Take(5).ToList();
        if (sent.Count > 0)
        {
            items.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(4) });
            foreach (var each in sent)
            {
                var when = DateTimeOffset.FromUnixTimeSeconds((long)each.CreatedAt).ToLocalTime();
                Add($"{TestRunner.VerdictLabel(each.Status)} on {each.MachineLabel}, {when:d MMM HH:mm}", () => new TestRunWindow(_devices, each.Id, project.Name).Show());
            }
        }
        menu.IsOpen = true;
    }
    private async Task SendTestsAsync(MachineLinkInfo link, Project project, string folder)
    {
        // The machine fetches the commit from origin, so it has to be there.
        var (commit, _, pushed, problem) = await GitTools.PushedHeadAsync(folder);
        if (problem is not null)
        {
            ShowPageProblem(project.Id, problem);
            return;
        }
        if (!pushed)
        {
            ShowPageProblem(project.Id, $"Push the commit checked out here first. {link.MachineLabel} fetches it from origin, and origin doesn't have {commit[..Math.Min(12, commit.Length)]} yet.");
            return;
        }

        var (runId, failed) = await _testMachines.RunTestsAsync(link, project, _database.GetTestSetup(project.Id), commit, async request =>
        {
            var window = TestCommandsWindow.ToSend(request);
            window.Owner = this;
            window.Show();
            return await window.Result;
        });
        if (runId is null)
        {
            if (failed is not null) ShowPageProblem(project.Id, failed); // Otherwise the person chose not to send them.
            return;
        }
        new TestRunWindow(_devices, runId, project.Name) { Owner = null }.Show();
    }
}
