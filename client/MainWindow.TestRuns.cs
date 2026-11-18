using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// "Run tests on…" on a project's page (#93): sends the commit its folder has checked out, from the
/// folder's own origin, to one of the machines this PC is linked to, which runs the tests the repo
/// declares in .agent-orange/tests.json at that commit, and opens the run's window.
/// </summary>
public partial class MainWindow
{
    // Into the row of what can be done with the folder, beside the git actions. What the commit
    // checked out would send is only looked at when it's clicked.
    private void AddTestRunButton(Panel row, Project project, string folder)
    {
        var run = ActionButton("Run tests on…");
        run.VerticalAlignment = VerticalAlignment.Center;
        run.Margin = new Thickness(row.Children.Count > 0 ? 8 : 0, 0, 0, 0);
        run.IsEnabled = !IsBusyWith(project.Id);
        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            var sending = await TestMachines.WhatToSendAsync(folder);
            var recent = await _devices.RunsAsync(project.Id);
            run.IsEnabled = true;
            OpenTestMachineMenu(run, project, folder, sending.Setup, sending.Problem, recent.Value ?? []);
        };
        row.Children.Add(run);
    }
    // A list under the button, as the git actions' menu is: one line a linked machine, then earlier runs.
    private void OpenTestMachineMenu(Button button, Project project, string folder, TestSetup? setup, string? problem, List<TestRunInfo> recent)
    {
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
        if (problem is not null || setup is null) Add(problem ?? "This commit can't be sent.", null);
        else if (links.Count == 0) Add(_testMachines.Problem ?? "No linked machines. Link one in Settings › Test machines.", null);
        var needed = setup?.Tags ?? [];
        foreach (var link in setup is null ? [] : links)
        {
            var missing = needed.Where(tag => !TestMachines.HasTags(link.MachineTags, [tag])).ToList();
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

    // Looked at again as it's sent, as the folder may have moved on since the list opened.
    private async Task SendTestsAsync(MachineLinkInfo link, Project project, string folder)
    {
        var (runId, failed) = await _testMachines.RunTestsAsync(link, project, folder);
        if (runId is null)
        {
            ShowPageProblem(project.Id, failed ?? "The tests couldn't be sent.");
            return;
        }
        new TestRunWindow(_devices, runId, project.Name) { Owner = null }.Show();
    }
}
