using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// What the agent is doing and waiting on, as T3 Code shows its background work: a line above the
/// box while any of it runs ("Waiting · Run the test suite · 35m 50s · Details · Stop"), and a
/// Background work panel from Details listing every piece, with what it is, how long it has run and
/// how it stands. A person, or an agent driving the window, can tell a pause from a finish by it.
/// </summary>
public sealed partial class AgentsWindow
{
    private readonly Border _workLine = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 8, 8) };
    private bool _workPanelOpen;

    private static string WorkIcon(BackgroundKind kind) => kind switch
    {
        BackgroundKind.Agent => "bot",
        BackgroundKind.Monitor => "eye",
        BackgroundKind.Wait => "clock",
        BackgroundKind.Workflow => "list-checks",
        BackgroundKind.Tool => "hammer",
        _ => "terminal",
    };

    // How one stands, in a word: what the turn is doing with it while it runs, or how it ended.
    private static (string Words, Brush Brush) WorkState(AgentConversation conversation, BackgroundWork work) => work.State switch
    {
        "running" => (conversation.IsWorking && work.Kind != BackgroundKind.Wait ? "Running" : "Waiting", Ocean.Info),
        "failed" => ("Failed", Ocean.Error),
        "stopped" => ("Stopped", Ocean.Secondary),
        _ => ("Done", Ocean.Success),
    };

    /// <summary>The line above the box, each second while any of its work runs.</summary>
    private void ShowWorkLine()
    {
        var running = _open?.Work.Where(work => work.IsRunning).ToList() ?? [];
        if (_open is not { } conversation || running.Count == 0)
        {
            _workLine.Visibility = Visibility.Collapsed;
            _workLine.Child = null;
            if (_workPanelOpen) ShowWorkPanel();
            return;
        }
        var first = running[0];
        var waiting = !conversation.IsWorking || running.All(work => work.Kind == BackgroundKind.Wait);
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var icon = AgentIcons.Line(WorkIcon(first.Kind), 14, Ocean.MutedText);
        icon.Margin = new Thickness(0, 0, 6, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        void Dot() => row.Children.Add(Ocean.Text("  ·  ", Ocean.Secondary, 12));
        var state = Ocean.Text(waiting ? "Waiting" : "Running", Ocean.Foreground, 12, FontWeights.Medium);
        row.Children.Add(state);
        Dot();
        var what = AgentConversation.Describe(first) + (running.Count > 1 ? $" and {running.Count - 1} more" : "");
        var title = Ocean.Text(what, Ocean.MutedText, 12);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxWidth = 420;
        title.ToolTip = string.Join("\n", running.Select(AgentConversation.Describe));
        row.Children.Add(title);
        Dot();
        row.Children.Add(Ocean.Text(Took(DateTimeOffset.UtcNow - running.Min(work => work.Started)), Ocean.MutedText, 12));
        Dot();
        row.Children.Add(InlineButton("Details", () =>
        {
            _workPanelOpen = true;
            ShowWorkPanel();
        }, strong: false, tip: "Every piece of background work in this conversation"));
        var stoppable = running.Where(work => work.Kind != BackgroundKind.Wait).ToList();
        if (stoppable.Count > 0)
        {
            Dot();
            row.Children.Add(InlineButton(stoppable.Count > 1 ? "Stop all" : "Stop", () =>
            {
                foreach (var work in stoppable) conversation.StopWork(work);
            }, strong: false, tip: stoppable.Count > 1 ? "Stops all of it" : $"Stops {AgentConversation.Describe(stoppable[0])}"));
        }
        AutomationProperties.SetName(row, $"{(waiting ? "Waiting" : "Running")}: {what}");
        _workLine.Child = row;
        _workLine.Visibility = Visibility.Visible;
        if (_workPanelOpen) ShowWorkPanel();
    }

    // The Background work panel, over the window as the Usage page is: each piece of work, running
    // first, then the rest with the latest first.
    private void ShowWorkPanel()
    {
        if (_open is not { } conversation)
        {
            CloseWorkPanel();
            return;
        }
        var close = Ocean.IconButton("x", CloseWorkPanel, "Close", 28, 16, Ocean.MutedText);
        var bar = new DockPanel { Height = 48, Margin = new Thickness(20, 0, 8, 0) };
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        var running = conversation.Work.Count(work => work.IsRunning);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { Ocean.Text("Background work", Ocean.Foreground, 14, FontWeights.Medium) } };
        if (running > 0) heading.Children.Add(Ocean.Text($"  {running} running", Ocean.MutedText, 14));
        bar.Children.Add(heading);
        var header = new Border { BorderBrush = Ocean.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };

        var list = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
        var shown = conversation.Work.Where(work => work.IsRunning).Concat(conversation.Work.Where(work => !work.IsRunning).OrderByDescending(work => work.Ended)).ToList();
        if (shown.Count == 0) list.Children.Add(Ocean.Wrapped("Nothing has run in the background since its agent program started.", Ocean.MutedText, 13, 20));
        foreach (var work in shown) list.Children.Add(WorkEntry(conversation, work));

        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });

        _overlay.Children.Clear();
        var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
        shade.MouseLeftButtonUp += (_, _) => CloseWorkPanel();
        _overlay.Children.Add(shade);
        _overlay.Children.Add(new Border
        {
            Background = Ocean.Canvas,
            BorderBrush = Ocean.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(40),
            MaxWidth = 640,
            MaxHeight = 720,
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 50, ShadowDepth = 20, Direction = 270, Opacity = 0.4 },
        });
        _overlay.Visibility = Visibility.Visible;
    }

    private void CloseWorkPanel()
    {
        _workPanelOpen = false;
        _overlay.Visibility = Visibility.Collapsed;
    }

    // One piece of work: its title with how it stands on the right, then what it is (the command, or the
    // helper agent's kind and what it's doing), how long it has run, and what it said when it ended.
    private FrameworkElement WorkEntry(AgentConversation conversation, BackgroundWork work)
    {
        var (stateWords, stateBrush) = WorkState(conversation, work);
        var top = new DockPanel();
        var icon = AgentIcons.Line(WorkIcon(work.Kind), 16, Ocean.Secondary, 2);
        icon.Margin = new Thickness(0, 2, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        var state = Ocean.Text(stateWords, stateBrush, 12, FontWeights.Medium);
        state.Margin = new Thickness(12, 2, 0, 0);
        state.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(state, Dock.Right);
        top.Children.Add(state);
        var title = Ocean.Wrapped(AgentConversation.Describe(work), Ocean.Foreground, 14, 20);
        title.FontWeight = FontWeights.Medium;
        top.Children.Add(title);

        var rows = new StackPanel { Children = { top } };
        var kind = work.Kind switch
        {
            BackgroundKind.Agent => work.AgentType is { Length: > 0 } type ? $"Helper agent ({type})" : "Helper agent",
            BackgroundKind.Monitor => "Monitor",
            BackgroundKind.Wait => "Wait",
            BackgroundKind.Workflow => "Workflow",
            BackgroundKind.Tool => "Tool call",
            _ => "Background command",
        };
        var took = work.IsRunning ? $"running for {Took(DateTimeOffset.UtcNow - work.Started)}" : $"ran for {Took((work.Ended ?? work.Started) - work.Started)}";
        var about = Ocean.Text($"{kind} · {took}", Ocean.MutedText, 12);
        about.Margin = new Thickness(24, 2, 0, 0);
        rows.Children.Add(about);

        if (work.Command is { Length: > 0 } command)
        {
            var code = Ocean.Wrapped(command, Ocean.Body, 12, 18);
            code.FontFamily = Ocean.Mono;
            rows.Children.Add(new Border { Margin = new Thickness(24, 8, 0, 0), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8), Background = Ocean.CodeBack, BorderBrush = Ocean.Border60, BorderThickness = new Thickness(1), Child = code });
        }
        var said = work.IsRunning ? work.Doing : work.Summary;
        if (said is { Length: > 0 })
        {
            var words = Ocean.Wrapped(said, Ocean.Secondary, 12, 18);
            words.Margin = new Thickness(24, 6, 0, 0);
            rows.Children.Add(words);
        }
        if (work.IsRunning && work.Kind != BackgroundKind.Wait)
        {
            var stop = InlineButton("Stop", () => conversation.StopWork(work), strong: false, tip: $"Stops {AgentConversation.Describe(work)}");
            stop.HorizontalAlignment = HorizontalAlignment.Left;
            stop.Margin = new Thickness(24, 6, 0, 0);
            rows.Children.Add(stop);
        }
        AutomationProperties.SetName(rows, $"{AgentConversation.Describe(work)}, {stateWords}");
        return new Border { BorderBrush = Ocean.Border60, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12, 0, 12), Child = rows };
    }
}
