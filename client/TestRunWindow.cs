using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// A test run seen from the PC that sent it: where it runs, its status, exactly what it runs, and
/// its log as it streams. Its own window, so several can be watched at once. Plain, as T3 has no
/// equivalent, in the look of the rest of the app.
/// </summary>
public sealed class TestRunWindow : Window
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1.5);

    private readonly DeviceSession _devices;
    private readonly string _runId;
    private readonly TextBlock _status = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _details = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
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

    public TestRunWindow(DeviceSession devices, string runId, string projectName)
    {
        _devices = devices;
        _runId = runId;
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
            var cancelled = await _devices.CancelRunAsync(_runId);
            if (!cancelled.Worked) _cancel.IsEnabled = true;
            await ReadAsync();
        };

        var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new StackPanel { Children = { _status, _details } });
        Grid.SetColumn(_cancel, 1);
        top.Children.Add(_cancel);

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
                var answer = await _devices.ReadRunAsync(_runId, _after);
                if (answer.Value is not { } read)
                {
                    _details.Text = TestMachines.DescribeProblem(answer.Status) ?? "Couldn't read the run.";
                    return;
                }
                Show(read.Run);
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
        var repo = "";
        try
        {
            if (JsonSerializer.Deserialize<TestJob>(run.Job, TestJob.Json) is { } job) repo = $"\n{job.RepoUrl}";
        }
        catch (JsonException)
        {
        }
        _details.Text = $"On {run.MachineLabel}, at {(run.Commit.Length > 12 ? run.Commit[..12] : run.Commit)}, running what {TestsFile.RelativePath} says there.{repo}";
        _cancel.Visibility = run.Finished ? Visibility.Collapsed : Visibility.Visible;
    }
}
