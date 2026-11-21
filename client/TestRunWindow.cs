using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// A test run seen from the PC that sent it: where it runs, its status, who started it, exactly what
/// it runs, and its log as it streams. Once it ends, how its checks went when the repo names a
/// results file, with the ones that flip (#105) and a way to their history. Its own window, so
/// several can be watched at once. Plain, as T3 has no equivalent, in the look of the rest of the app.
/// </summary>
public sealed class TestRunWindow : Window
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1.5);

    private readonly MainWindow _main;
    private readonly string _runId;
    private readonly string _projectName;
    private readonly TextBlock _status = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _details = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _checks = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _history = ActionButton("Check history");
    private bool _checksShown;
    private string _projectId = "";
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12.5,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8),
    };
    private readonly Button _cancel = ActionButton("Cancel run");
    private readonly DispatcherTimer _poll = new() { Interval = PollEvery };
    private int _after = -1;
    private bool _reading;

    public TestRunWindow(MainWindow main, string runId, string projectName)
    {
        _main = main;
        _runId = runId;
        _projectName = projectName;
        Title = $"Test run · {projectName}";
        Width = 860;
        Height = 620;
        Background = (Brush)FindResource("ContentBackground");
        _status.Foreground = (Brush)FindResource("TextStrong");
        _details.Foreground = (Brush)FindResource("TextMuted");
        _log.Background = (Brush)FindResource("SidebarBackground");
        _log.Foreground = (Brush)FindResource("TextNormal");
        _log.BorderBrush = (Brush)FindResource("Line");
        AutomationProperties.SetName(_log, "Log");
        _status.Text = "Waiting";

        _cancel.HorizontalAlignment = HorizontalAlignment.Right;
        _cancel.VerticalAlignment = VerticalAlignment.Top;
        _cancel.Click += async (_, _) =>
        {
            _cancel.IsEnabled = false;
            var cancelled = await _main.CancelTestRunAsync(_runId);
            if (!cancelled.Worked) _cancel.IsEnabled = true;
            await ReadAsync();
        };

        _checks.Foreground = (Brush)FindResource("TextNormal");
        _history.HorizontalAlignment = HorizontalAlignment.Right;
        _history.VerticalAlignment = VerticalAlignment.Top;
        _history.Visibility = Visibility.Collapsed;
        _history.Click += async (_, _) => await _main.OpenCheckHistoryAsync(_projectId, _projectName);

        var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new StackPanel { Children = { _status, _details, _checks } });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Children = { _history, _cancel } };
        _cancel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(buttons, 1);
        top.Children.Add(buttons);

        var layout = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(_log);
        Content = layout;

        _poll.Tick += async (_, _) => await ReadAsync();
        Loaded += async (_, _) =>
        {
            _poll.Start();
            await ReadAsync();
        };
        Closed += (_, _) => _poll.Stop();
#if DEBUG
        if (Headless.Active) Headless.KeepOffScreen(this);
#endif
    }

    private async Task ReadAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            // Pages of log until it's caught up.
            while (true)
            {
                var answer = await _main.ReadRunForWindowAsync(_runId, _after);
                if (answer.Value is not { } read)
                {
                    _details.Text = TestMachines.DescribeProblem(answer.Status) ?? "Couldn't read the run.";
                    return;
                }
                Show(read.Run);
                if (read.Run.Finished && !_checksShown)
                {
                    _checksShown = true;
                    _main.TestRunEnded(read.Run.ProjectId);
                    await ShowChecksAsync(read.Run);
                }
                if (read.Log.Count > 0)
                {
                    var atEnd = _log.VerticalOffset + _log.ViewportHeight >= _log.ExtentHeight - 4;
                    foreach (var line in read.Log) _log.AppendText(line.Text);
                    _after = read.Log[^1].Seq;
                    if (atEnd) _log.ScrollToEnd();
                }
                if (read.Log.Count == 0)
                {
                    if (read.Run.Finished) _poll.Stop(); // All of it is here now.
                    break;
                }
            }
        }
        finally
        {
            _reading = false;
        }
    }

    private void Show(TestRunInfo run)
    {
        // Each run's window says which run it is, as several can be open.
        var sentAt = DateTimeOffset.FromUnixTimeSeconds((long)run.CreatedAt).ToLocalTime();
        Title = $"Test run · {_projectName} · {run.MachineLabel} · {sentAt:d MMM HH:mm}";

        var verdict = TestRunner.VerdictLabel(run.Status);
        _status.Text = run.Status == "running" && run.CancelRequested ? "Cancelling…"
            : run.ExitCode is { } code && run.Finished ? $"{verdict} (exit code {code})"
            : verdict;
        _status.Foreground = run.Status switch
        {
            "passed" => new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A)),
            "failed" or "lost" or "timed_out" or "couldnt_run" or "refused" or "expired" => new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B)),
            _ => (Brush)FindResource("TextStrong"),
        };

        // The commands are the repo's, which the log shows as the machine read them.
        var job = TestJob.Read(run.Job);
        var repo = job is null ? "" : $"\n{job.RepoUrl}";
        var what = job?.Filter is { } filter ? $"testing only {filter}, with testWithFilter from {TestsFile.RelativePath} there" : $"running what {TestsFile.RelativePath} says there";
        _projectId = run.ProjectId;
        _details.Text = $"On {run.MachineLabel}, at {(run.Commit.Length > 12 ? run.Commit[..12] : run.Commit)}, {what}. Started by {(run.ByAgent ? "an agent" : "a person")}.{repo}";
        _cancel.Visibility = run.Finished ? Visibility.Collapsed : Visibility.Visible;
    }

    // How its checks went, once it's ended, with the failed ones and why any that flip are flagged.
    private async Task ShowChecksAsync(TestRunInfo run)
    {
        if (run.Checks == 0) return;
        var checks = await _main.RunChecksAsync(run.Id) ?? [];
        var flags = TestChecks.Flags(await _main.CheckHistoryAsync(run.ProjectId) ?? []);
        var lines = new List<string> { $"Checks: {TestChecks.Summary(run.ChecksPassed, run.ChecksFailed, run.ChecksSkipped)}." };
        var failed = checks.Where(check => check.Outcome == TestChecks.Failed).ToList();
        foreach (var check in failed.Take(10)) lines.Add($"Failed: {check.Name}{(flags.TryGetValue(check.Name, out var flag) ? $". It {flag.Why}" : "")}");
        if (failed.Count > 10) lines.Add($"And {failed.Count - 10} more failed.");
        foreach (var check in checks.Where(check => check.Outcome != TestChecks.Failed && flags.ContainsKey(check.Name)).Take(5)) lines.Add($"{TestChecks.Label(check.Outcome)} this time: {check.Name}. It {flags[check.Name].Why}");
        _checks.Text = string.Join("\n", lines);
        _checks.Visibility = Visibility.Visible;
        _history.Visibility = failed.Count > 0 || flags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
