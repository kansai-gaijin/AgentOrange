using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// "Start an agent on…" from a project's page (#93): one of the machines this PC links to with
/// Agents access, what the agent there should do, and Start, which sends it from the commit checked
/// out here, once pushed, and opens its run's window, where its conversation streams. Machines
/// without Agents access are listed with why, and what to do about it. Its own window, plain, in
/// the look of the run windows.
/// </summary>
public sealed class RemoteAgentWindow : Window
{
    private readonly MainWindow _main;
    private readonly TestMachines _machines;
    private readonly Project _project;
    private readonly string _folder;
    private readonly ComboBox _picker = new() { MinWidth = 300 };
    private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
    private readonly TextBox _task = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 160,
        Padding = new Thickness(8),
        MaxLength = TestJob.MostTaskLength,
    };
    private readonly TextBlock _problem = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _start;

    public RemoteAgentWindow(MainWindow main, TestMachines machines, Project project, string folder)
    {
        _main = main;
        _machines = machines;
        _project = project;
        _folder = folder;
        Title = $"Start an agent on… · {project.Name}";
        Width = 640;
        Height = 520;
        Background = (Brush)FindResource("ContentBackground");
        _picker.Style = (Style)FindResource("Choice");
        AutomationProperties.SetName(_picker, "Machine");
        AutomationProperties.SetName(_task, "Task");
        AutomationProperties.SetName(_note, "About this machine");
        _note.Foreground = (Brush)FindResource("TextMuted");
        _problem.Foreground = (Brush)FindResource("Accent");
        _task.Background = (Brush)FindResource("SidebarBackground");
        _task.Foreground = (Brush)FindResource("TextNormal");
        _task.BorderBrush = (Brush)FindResource("Line");

        var latest = machines.Latest;
        var links = latest?.Links.Where(link => link.Linker == latest.Me).OrderBy(link => link.MachineLabel, StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];
        foreach (var link in links) _picker.Items.Add(new ComboBoxItem { Content = machines.AgentsProblem(link) is null ? link.MachineLabel : $"{link.MachineLabel}   {MachineLinks.AccessLabel(link.Access)}", Tag = link });
        var usable = links.FindIndex(link => machines.AgentsProblem(link) is null);
        if (usable >= 0) _picker.SelectedIndex = usable;
        _picker.SelectionChanged += (_, _) => ShowNote();

        _start = PrimaryButton("Start", async () => await StartAsync());
        _start.HorizontalAlignment = HorizontalAlignment.Right;

        var layout = new StackPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock { Text = "Start an agent on another PC", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong") });
        layout.Children.Add(new TextBlock
        {
            Text = $"It works on {project.Name} there, in that PC's own clone of the repo, from the commit checked out here (push it first), with that PC's login and settings. Its conversation streams into a run window here, and once it's done, its commits are pushed to a new branch on origin for this PC to fetch.",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextMuted"),
            Margin = new Thickness(0, 6, 0, 14),
        });
        layout.Children.Add(links.Count == 0 ? new TextBlock { Text = machines.Problem ?? "No linked machines. Link one with Agents access in Settings › Test machines.", Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap } : _picker);
        layout.Children.Add(_note);
        layout.Children.Add(new TextBlock { Text = "What should it do?", FontSize = 13, Foreground = (Brush)FindResource("TextNormal"), Margin = new Thickness(0, 0, 0, 6) });
        layout.Children.Add(_task);
        layout.Children.Add(_problem);
        layout.Children.Add(new Border { Height = 12 });
        layout.Children.Add(_start);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = layout };
        ShowNote();
#if DEBUG
        if (Headless.Active) Headless.KeepOffScreen(this);
#endif
    }

    private MachineLinkInfo? Picked => (_picker.SelectedItem as ComboBoxItem)?.Tag as MachineLinkInfo;

    // Whether the chosen machine can take it now, and why not.
    private void ShowNote()
    {
        if (Picked is not { } link)
        {
            _note.Text = "Choose a machine linked with Agents access.";
            _start.IsEnabled = false;
            return;
        }
        var cant = _machines.AgentsProblem(link);
        _note.Text = cant ?? link.Status switch
        {
            "busy" => $"{link.MachineLabel} is busy with a run, so this waits its turn there.",
            "unreachable" => $"{link.MachineLabel} isn't answering right now. It waits there, a day at most, until Agent Orange runs there.",
            "other_release" => $"{link.MachineLabel} is on another release of Agent Orange. It starts once that PC updates.",
            _ => $"{link.MachineLabel} starts it within a few seconds.",
        };
        _start.IsEnabled = cant is null;
    }

    private async Task StartAsync()
    {
        if (Picked is not { } link || !_start.IsEnabled) return;
        _start.IsEnabled = false;
        _start.Content = "Starting…";
        _problem.Visibility = Visibility.Collapsed;
        var (runId, problem) = await _machines.StartAgentAsync(link, _project, _folder, _task.Text);
        _start.Content = "Start";
        if (runId is null)
        {
            _problem.Text = problem ?? "It couldn't be sent.";
            _problem.Visibility = Visibility.Visible;
            ShowNote();
            return;
        }
        _main.OpenTestRun(runId, _project.Name);
        Close();
    }
}
