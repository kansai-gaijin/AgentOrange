using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// "Run tests on…" on a project's page (#93): sends the commit its folder has checked out, from the
/// folder's own origin, to one of the machines this PC is linked to, which runs the tests the repo
/// declares in .agent-orange/tests.json at that commit, and opens the run's window. A machine already
/// testing that commit, or with it waiting, opens that run instead of sending another, the list
/// keeps up with runs as they change, and each run has one window (#118).
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan TestMenuRefreshEvery = TimeSpan.FromSeconds(3);

    // A window per run, brought to the front when it's opened again.
    private readonly Dictionary<string, TestRunWindow> _testRunWindows = [];

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
            var recent = await TestRunsAsync(project.Id);
            var history = await CheckHistoryAsync(project.Id);
            run.IsEnabled = true;
            OpenTestMachineMenu(run, project, folder, sending.Setup, sending.Commit, sending.Problem, recent ?? [], history ?? []);
        };
        row.Children.Add(run);
    }

    // A list under the button, as the git actions' menu is: one line a linked machine, then earlier
    // runs, then the checks that flip, if any do. Asked again every few seconds while it's open, so
    // what it says stays current.
    private void OpenTestMachineMenu(Button button, Project project, string folder, TestSetup? setup, string? commit, string? problem, List<TestRunInfo> recent, List<CheckHistoryRow> history)
    {
        var flipping = TestChecks.Flags(history).Count;
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

        void Fill()
        {
            items.Children.Clear();
            var latest = _testMachines.Latest;
            var links = latest?.Links.Where(link => link.Linker == latest.Me).OrderBy(link => link.MachineLabel, StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];
            if (problem is not null || setup is null) Add(problem ?? "This commit can't be sent.", null);
            else if (links.Count == 0) Add(_testMachines.Problem ?? "No linked machines. Link one in Settings › Test machines.", null);
            var needed = setup?.Tags ?? [];
            foreach (var link in setup is null ? [] : links)
            {
                // Sending the same commit again would only repeat it, so that run opens instead.
                if (_testMachines.SameRun(recent, link.Id, project.Id, commit) is { } same)
                {
                    Add($"{link.MachineLabel} · {(same.Status == "running" ? "already testing this commit" : "has this commit waiting")}, open it", () => OpenTestRun(same.Id, project.Name));
                    continue;
                }
                var missing = needed.Where(tag => !TestMachines.HasTags(link.MachineTags, [tag])).ToList();
                var why = missing.Count > 0 ? $"doesn't offer {string.Join(", ", missing)}" : link.Status switch
                {
                    "expired" => "expired",
                    "unreachable" => "not answering right now",
                    "other_release" => "on another release, update it",
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
                    Add($"{TestRunner.VerdictLabel(each.Status)} on {each.MachineLabel}, {when:d MMM HH:mm}{(each.Filter is { } only ? $", only {only}" : "")}{(each.IsAgent ? ", an agent task" : "")}{(each.ByAgent ? ", started by an agent" : "")}", () => OpenTestRun(each.Id, project.Name));
                }
            }

            // Checks that passed and failed at the same commit (#105), whose history says why.
            if (flipping > 0)
            {
                items.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(4) });
                Add($"{(flipping == 1 ? "1 check flips" : $"{flipping} checks flip")} between pass and fail, see the history", async () => await OpenCheckHistoryAsync(project.Id, project.Name));
            }
        }
        Fill();

        var refresh = new DispatcherTimer { Interval = TestMenuRefreshEvery };
        var asking = false;
        refresh.Tick += async (_, _) =>
        {
            if (asking) return;
            asking = true;
            var runs = await TestRunsAsync(project.Id);
            asking = false;
            if (runs is { } now) recent = now;
            if (menu.IsOpen) Fill();
        };
        void MachinesChanged() => Dispatcher.InvokeAsync(() => { if (menu.IsOpen) Fill(); });
        _testMachines.Changed += MachinesChanged;
        menu.Closed += (_, _) =>
        {
            refresh.Stop();
            _testMachines.Changed -= MachinesChanged;
        };
        refresh.Start();
        menu.IsOpen = true;
    }

    // Looked at again as it's sent, as the folder may have moved on since the list opened.
    private async Task SendTestsAsync(MachineLinkInfo link, Project project, string folder)
    {
        var (runId, failed, existing) = await _testMachines.RunTestsAsync(link, project, folder);
        if (existing is not null)
        {
            ShowPageProblem(project.Id, $"{link.MachineLabel} is already testing this commit, or has it waiting, so that run was opened instead of sending another.");
            OpenTestRun(existing.Id, project.Name);
            return;
        }
        if (runId is null)
        {
            ShowPageProblem(project.Id, failed ?? "The tests couldn't be sent.");
            return;
        }
        OpenTestRun(runId, project.Name);
    }

    /// <summary>A run's window: the one it has, brought to the front, or a new one.</summary>
    internal TestRunWindow OpenTestRun(string runId, string projectName)
    {
        if (_testRunWindows.TryGetValue(runId, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Show();
            open.Activate();
            return open;
        }
        var window = new TestRunWindow(this, runId, projectName) { Owner = null };
        _testRunWindows[runId] = window;
        window.Closed += (_, _) => _testRunWindows.Remove(runId);
        window.Show();
        return window;
    }

    // A window per project's check history, brought to the front and read again when opened again.
    private readonly Dictionary<string, CheckHistoryWindow> _checkHistoryWindows = [];

    /// <summary>A project's checks that failed, with their history and the ones that flip (#105).</summary>
    internal async Task OpenCheckHistoryAsync(string projectId, string projectName)
    {
        if (!_checkHistoryWindows.TryGetValue(projectId, out var window))
        {
            window = new CheckHistoryWindow(projectName) { Owner = null };
            _checkHistoryWindows[projectId] = window;
            window.Closed += (_, _) => _checkHistoryWindows.Remove(projectId);
            window.Show();
        }
        else
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }
        window.Fill(await CheckHistoryAsync(projectId));
    }

    /// <summary>A run and its log after the part read, for its window: from the server, or made up for headless checks.</summary>
    internal async Task<ServerAnswer<TestRunWithLog>> ReadRunForWindowAsync(string runId, int after)
    {
#if DEBUG
        if (_checksForChecks is not null && _testRunsForChecks?.FirstOrDefault(run => run.Id == runId) is { } madeUp)
        {
            var log = after == -1 ? new List<RunLogLine> { new(0, _testLogForChecks) } : [];
            return new(new TestRunWithLog(madeUp.Id, madeUp.LinkId, madeUp.Machine, madeUp.MachineLabel, madeUp.Linker, madeUp.LinkerLabel, madeUp.ProjectId, madeUp.Commit, madeUp.Job, madeUp.Mac,
                madeUp.Status, madeUp.ExitCode, madeUp.CancelRequested, madeUp.CreatedAt, madeUp.StartedAt, madeUp.EndedAt, madeUp.LinkAccess, madeUp.LinkExpiresAt, log, madeUp.ChecksPassed, madeUp.ChecksFailed, madeUp.ChecksSkipped, madeUp.HeldBack), System.Net.HttpStatusCode.OK);
        }
#endif
        return await _devices.ReadRunAsync(runId, after);
    }

    // A project's recent runs as the server has them, or null when it couldn't say.
    private async Task<List<TestRunInfo>?> TestRunsAsync(string projectId, int? limit = null)
    {
#if DEBUG
        if (_testRunsForChecks is { } madeUp) return [.. madeUp];
#endif
        return (await _devices.RunsAsync(projectId, limit)).Value;
    }

    // The default branch's tests (#93): its latest verdict, shown on the project page while it's a
    // failure, until a later run passes. Read as the page opens, every minute while it's open, and
    // as a run's window sees its run end.
    private const int RunsLookedAtForBranch = 200;
    private static readonly TimeSpan BranchVerdictEvery = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, BranchFailure> _branchFailures = [];
    private readonly HashSet<string> _readingBranchVerdict = [];
    private DispatcherTimer? _branchVerdictTimer;

    private sealed record BranchFailure(TestRunInfo Run, string Branch);

    /// <summary>
    /// The run that says how the default branch stands, when it failed or timed out: the latest
    /// run of all the tests at the newest commit on that branch that has such a verdict. A run
    /// passing later, at that commit or a newer one, takes it away. Runs of only some checks, and
    /// runs that couldn't run or were stopped, say nothing about the branch.
    /// </summary>
    internal static TestRunInfo? BranchFailureIn(IEnumerable<TestRunInfo> runs, IReadOnlyList<string> history)
    {
        var place = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var at = 0; at < history.Count; at++) place.TryAdd(history[at], at);
        var latest = runs
            .Where(run => run.Filter is null && !run.IsAgent && run.Status is "passed" or "failed" or "timed_out" && place.ContainsKey(run.Commit))
            .OrderBy(run => place[run.Commit])
            .ThenByDescending(run => run.CreatedAt)
            .FirstOrDefault();
        return latest is { Status: not "passed" } ? latest : null;
    }

    private async Task ReadBranchVerdictAsync(string projectId)
    {
        if (!_readingBranchVerdict.Add(projectId)) return;
        try
        {
            BranchFailure? found = null;
            if (ProjectFolder(projectId) is { } folder && await GitTools.DefaultBranchAsync(folder) is { } branch)
            {
                // With no answer from the server or Git, what's shown stays as it is.
                if (await GitTools.BranchHistoryAsync(folder, branch) is not { } history || await TestRunsAsync(projectId, RunsLookedAtForBranch) is not { } runs) return;
                if (BranchFailureIn(runs, history) is { } failed) found = new BranchFailure(failed, branch.StartsWith("origin/", StringComparison.Ordinal) ? branch["origin/".Length..] : branch);
            }
            var shown = _branchFailures.GetValueOrDefault(projectId);
            if (found == shown) return;
            if (found is null) _branchFailures.Remove(projectId);
            else _branchFailures[projectId] = found;
            if (_openProject?.Id == projectId && _pageCheck is not null && FindProject(projectId) is { } project) RenderProjectKeepingFocus(project);
        }
        finally
        {
            _readingBranchVerdict.Remove(projectId);
        }
    }

    // Read again every minute while a project's page is open.
    private void WatchBranchVerdict(string projectId)
    {
        if (_branchVerdictTimer is null)
        {
            _branchVerdictTimer = new DispatcherTimer { Interval = BranchVerdictEvery };
            _branchVerdictTimer.Tick += (_, _) =>
            {
                if (_openProject is { } open && ProjectView.Visibility == Visibility.Visible) _ = ReadBranchVerdictAsync(open.Id);
            };
            _branchVerdictTimer.Start();
        }
        _ = ReadBranchVerdictAsync(projectId);
    }

    /// <summary>A run's window saw it end, which can change how the default branch stands.</summary>
    internal void TestRunEnded(string projectId) => _ = ReadBranchVerdictAsync(projectId);

    // Under the folder's actions: the failure, where and when, and its run.
    private FrameworkElement BranchFailureRow(Project project, BranchFailure failure)
    {
        var run = failure.Run;
        var when = DateTimeOffset.FromUnixTimeSeconds((long)run.CreatedAt).ToLocalTime();
        var how = run.Status == "timed_out" ? "timed out" : "failed";
        var exit = run.ExitCode is { } code ? $", exit code {code}" : "";
        var said = new TextBlock
        {
            Text = $"The tests {how} on {failure.Branch}, the default branch, at {(run.Commit.Length > 12 ? run.Commit[..12] : run.Commit)}, on {run.MachineLabel}, {when:d MMM HH:mm}{exit}. This stays until a later run passes there.",
            Foreground = (Brush)FindResource("Accent"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var open = ActionButton("Open the run");
        open.Margin = new Thickness(12, 0, 0, 0);
        open.VerticalAlignment = VerticalAlignment.Center;
        open.Click += (_, _) => OpenTestRun(run.Id, project.Name);
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(open, Dock.Right);
        row.Children.Add(open);
        row.Children.Add(said);
        return row;
    }

#if DEBUG
    // Headless checks only: runs as if the server had them, and a linked machine to send them to.
    private List<TestRunInfo>? _testRunsForChecks;

    /// <summary>
    /// Opens the Run tests on… list for a project as if this PC were linked to Office PC, which has a
    /// run of this commit in that state, so a check can see what the list offers and how it keeps up.
    /// </summary>
    internal void OpenTestMenuForChecks(string projectName, string status)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var link = new MachineLinkInfo("check-link", "office", "Office PC", [], "me", "This PC", "tests", 0, DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds(), "busy", 0);
        _testMachines.ShowForChecks(new MachinesInfo("me", null, [], [link]));
        // After testchecks, its runs, the latest by an agent, in that state.
        _testRunsForChecks = _checksForChecks is not null && _testRunsForChecks is [var latest, .. var rest]
            ? [latest with { Status = status }, .. rest]
            : [new TestRunInfo("check-run", link.Id, link.Machine, link.MachineLabel, "me", "This PC", project.Id, commit, "{}", "", status, null, false, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null, null, null)];
        OpenTestMachineMenu(RunTestsButton(this) ?? throw new InvalidOperationException("The project's page has no Run tests on… button."), project, "", new TestSetup("", "check", null, [], 30), commit, null, [.. _testRunsForChecks], [.. _checkHistoryForChecks ?? []]);

        static Button? RunTestsButton(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button { Content: "Run tests on…", IsVisible: true } found) return found;
                if (RunTestsButton(child) is { } deeper) return deeper;
            }
            return null;
        }
    }

    /// <summary>
    /// Headless checks only: the made-up run changes state, as the server would say it had, not
    /// being cancelled; held back, its report was turned away for coming from another release (#124).
    /// </summary>
    internal void SetTestRunForChecks(string status, bool heldBack = false)
    {
        if (_testRunsForChecks is { Count: > 0 } runs) runs[0] = runs[0] with { Status = status, CancelRequested = false, HeldBack = heldBack };
    }

    // The made-up server a held-back run told about it, which later takes what was kept.
    private MadeUpRunServer? _runServerForChecks;

    /// <summary>
    /// Headless checks only: a whole run here of a folder committed as a repo, telling a made-up
    /// server that updates after that many parts of its log, which then turns the rest away (#124).
    /// Its verdict and note, what the server took, and how many reports this PC keeps.
    /// </summary>
    internal async Task<string> HeldBackRunForChecksAsync(string repo, int releasedAfterParts)
    {
        var commit = await GitTools.CommitAllForChecksAsync(repo) ?? throw new InvalidOperationException($"{repo} couldn't be committed.");
        _runServerForChecks = new MadeUpRunServer(releasedAfterParts);
        var settings = new TestMachineSettings(true, "Check PC", [], System.IO.Path.Combine(Headless.DataFolder, "test-runs"));
        var done = await TestRunner.RunForChecksAsync(_database, _runServerForChecks, settings, repo, commit, "Check project");
        return $"Verdict: {TestRunner.VerdictLabel(done.Verdict)}. {done.Note}\nThe server took: {string.Join(", ", _runServerForChecks.Took)}\nKept: {TestRunner.HeldReports(_database).Count}";
    }

    /// <summary>
    /// Headless checks only: this PC has updated, so the made-up server (a new one, after the app
    /// started again) takes its release, and the reports kept are sent. What it took, the log
    /// it was sent, and how many reports this PC still keeps.
    /// </summary>
    internal async Task<string> SendHeldReportsForChecksAsync()
    {
        var server = _runServerForChecks ?? new MadeUpRunServer(0);
        server.Updated = true;
        var (took, logged) = (server.Took.Count, server.Log.Length);
        await TestRunner.SendHeldReportsAsync(_database, server);
        return $"The server took: {string.Join(", ", server.Took.Skip(took))}\n{server.Log[logged..]}Kept: {TestRunner.HeldReports(_database).Count}";
    }

    /// <summary>
    /// Headless checks only: as if this PC were linked to these machines, each "name|status|tags"
    /// with its tags split by commas, and the server had no runs.
    /// </summary>
    internal void SetUpTestMachinesForChecks(IEnumerable<string> machines)
    {
        var links = machines.Select((each, at) => each.Split('|') is [var name, var status, var tags]
            ? new MachineLinkInfo($"check-link-{at}", $"machine-{at}", name, [.. tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], "me", "This PC", "tests", 0, DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds(), status, 0)
            : throw new InvalidOperationException($"{each} isn't name|status|tags.")).ToList();
        _testMachines.ShowForChecks(new MachinesInfo("me", null, [], links));
        _testRunsForChecks = [];
    }

    /// <summary>
    /// Headless checks only: a made-up run of all the tests, or only the filter's, newer than any
    /// other, at the commit the project's folder names (HEAD if not given), on Office PC, in that
    /// state; then the default branch's verdict is read again, as a run's window does when one ends.
    /// </summary>
    internal async Task AddTestRunForChecks(string projectName, string status, string? filter, string? commit)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var folder = ProjectFolder(project.Id) ?? throw new InvalidOperationException($"{projectName} has no folder.");
        var (sha, _, _, problem) = await GitTools.PushedHeadAsync(folder, commit);
        if (problem is not null) throw new InvalidOperationException(problem);
        _testRunsForChecks ??= [];
        var at = _testRunsForChecks.Count > 0 ? _testRunsForChecks.Max(run => run.CreatedAt) + 60 : DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
        var id = $"check-run-{_testRunsForChecks.Count + 1}";
        var job = System.Text.Json.JsonSerializer.Serialize(new TestJob(TestJob.TestsKind, id, (long)at, project.Id, project.Name, "", sha, "tests", [], Filter: filter), TestJob.Json);
        _testRunsForChecks.Insert(0, new TestRunInfo(id, "check-link", "office", "Office PC", "me", "This PC", project.Id, sha, job, "", status, status == "failed" ? 1 : status == "passed" ? 0 : null, false, at, at, status is "queued" or "running" ? null : at, "tests", null));
        await ReadBranchVerdictAsync(project.Id);
    }

    /// <summary>Headless checks only: this PC takes runs, into a work folder in the data folder.</summary>
    internal void SetTestWorkFolderForChecks(string folder) => _testMachines.Save(_testMachines.Settings with { Accepting = true, WorkFolder = folder });

    /// <summary>Headless checks only: how many run windows are open.</summary>
    internal int TestRunWindowsForChecks => _testRunWindows.Count;
#endif
}
