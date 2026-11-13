using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentOrange;

/// <summary>
/// What a conversation's login has left, and what the agent is up to, as T3 Code shows them: each
/// limit's use down the side, how full the context is by the send button, a banner when a limit stops
/// it (it goes on by itself once that resets), and the plan it keeps for itself above the box.
/// </summary>
public sealed partial class AgentsWindow
{
    private static readonly CultureInfo Clock = CultureInfo.InvariantCulture;

    private readonly Border _usagePanel = new() { Padding = new Thickness(18, 12, 18, 14) };
    private readonly Border _limitBanner = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 12), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
    private readonly Border _stepsPanel = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(12), Padding = new Thickness(6, 4, 6, 6), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
    private readonly Border _contextMeter = new() { Visibility = Visibility.Collapsed, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private Button? _loginPill;
    private bool _stepsOpen = true;

    private void BuildUsageParts()
    {
        _usagePanel.BorderBrush = Brush("A.Border");
        _usagePanel.BorderThickness = new Thickness(0, 1, 0, 0);
        _limitBanner.Background = Brush("A.AccentTint");
        _limitBanner.BorderBrush = Brush("A.Accent");
        _stepsPanel.Background = Brush("A.Raised");
        _stepsPanel.BorderBrush = Brush("A.InputBorder");
        _main.Usage.Changed += ShowUsage;
        Closed += (_, _) => _main.Usage.Changed -= ShowUsage;
        _ = _main.ProbeUsageAsync();
    }

    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        return local.Date == today ? local.ToString("h:mm tt", Clock)
            : local.Date == today.AddDays(1) ? $"tomorrow {local.ToString("h:mm tt", Clock)}"
            : local.ToString("ddd h:mm tt", Clock);
    }

    /// <summary>Everything here, for the open conversation and its login.</summary>
    private void ShowUsage()
    {
        ShowUsagePanel();
        ShowContext();
        ShowLimitBanner();
        ShowSteps();
        ShowLoginPill();
    }

    // Down the side: each of the login's limits, with how much is used and when it starts again.
    private void ShowUsagePanel()
    {
        var logins = _main.AgentLogins();
        var login = (_open is null ? null : _main.AgentProviderFor(_open)) ?? logins.FirstOrDefault();
        var rows = new StackPanel();
        _usagePanel.Child = rows;
        _usagePanel.Visibility = login is null ? Visibility.Collapsed : Visibility.Visible;
        if (login is null) return;

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var refresh = Make("A.Ghost", Glyph("", "A.Faint", 11), () => _ = _main.ProbeUsageAsync());
        refresh.Padding = new Thickness(6, 3, 6, 3);
        refresh.ToolTip = _main.Usage.CheckedAt(login.Id) is { } checkedAt ? $"Checked {Ago(checkedAt)} ago. Check again" : "Check now";
        AutomationProperties.SetName(refresh, "Check usage again");
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);
        top.Children.Add(Text(logins.Count > 1 ? $"USAGE · {login.Label.ToUpperInvariant()}" : "USAGE", "A.Faint", 10.5, FontWeights.SemiBold));
        rows.Children.Add(top);

        var limits = _main.Usage.For(login.Id).Where(limit => limit.Used is not null && limit.Kind is "five_hour" or "seven_day" or "seven_day_opus" or "seven_day_sonnet").ToList();
        if (limits.Count == 0)
        {
            var none = Text("Shows once its login has been asked.", "A.Faint", 11.5);
            none.TextWrapping = TextWrapping.Wrap;
            rows.Children.Add(none);
            return;
        }
        foreach (var limit in limits) rows.Children.Add(LimitRow(limit));
    }

    private FrameworkElement LimitRow(AgentLimit limit)
    {
        var used = Math.Clamp(limit.Used ?? 0, 0, 100);
        var tone = used >= 90 || limit.Blocked ? "A.Danger" : used >= 80 ? "A.Warning" : "A.Accent";
        var heading = new DockPanel();
        var percent = Text(limit.Blocked ? "Ran out" : $"{used:0}% used", used >= 80 ? tone : "A.Muted", 11.5);
        DockPanel.SetDock(percent, Dock.Right);
        heading.Children.Add(percent);
        heading.Children.Add(Text(limit.Name, "A.Normal", 12));

        var bar = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 4), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) }, new ColumnDefinition { Width = new GridLength(100 - used, GridUnitType.Star) } } };
        bar.Children.Add(new Border { Background = Brush("A.Surface"), CornerRadius = new CornerRadius(2) });
        Grid.SetColumnSpan(bar.Children[0], 2);
        bar.Children.Add(new Border { Background = Brush(tone), CornerRadius = new CornerRadius(2) });

        var under = new List<string>();
        if (limit.ResetsAt is { } resets) under.Add($"resets {When(resets)}");
        if (limit.Pace is { } pace && !limit.Blocked) under.Add(pace.ToLowerInvariant());
        var row = new StackPanel { Margin = new Thickness(0, 6, 0, 2), Children = { heading, bar } };
        if (under.Count > 0) row.Children.Add(Text(string.Join(" · ", under), "A.Faint", 11));
        row.ToolTip = limit.Pace == "Ahead of pace" ? "Used faster than the time gone would allow, so it may run out before it resets." : null;
        return row;
    }

    // By the send button: how full its context is, as a ring.
    private void ShowContext()
    {
        if (_open is not { ContextWindow: > 0 } open)
        {
            _contextMeter.Visibility = Visibility.Collapsed;
            return;
        }
        var fraction = Math.Clamp((double)open.ContextUsed / open.ContextWindow, 0, 1);
        var tone = fraction >= 0.9 ? "A.Danger" : fraction >= 0.8 ? "A.Warning" : "A.Muted";
        var words = Text($"{fraction * 100:0}%", "A.Faint", 11.5);
        words.Margin = new Thickness(6, 0, 0, 0);
        _contextMeter.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Ring(fraction, tone), words } };
        _contextMeter.Visibility = Visibility.Visible;
        _contextMeter.ToolTip = $"Context: {AgentConversation.Tokens(open.ContextUsed)} of {AgentConversation.Tokens(open.ContextWindow)} tokens. Near the end it sums up what came before to make room.";
        AutomationProperties.SetName(_contextMeter, $"Context {fraction * 100:0}% full");
    }

    private FrameworkElement Ring(double fraction, string brush)
    {
        const double Size = 15, Thick = 2;
        var radius = (Size - Thick) / 2;
        var grid = new Grid { Width = Size, Height = Size };
        grid.Children.Add(new Ellipse { Stroke = Brush("A.InputBorder"), StrokeThickness = Thick });
        if (fraction >= 0.999)
        {
            grid.Children.Add(new Ellipse { Stroke = Brush(brush), StrokeThickness = Thick });
        }
        else if (fraction > 0.005)
        {
            var angle = fraction * 2 * Math.PI;
            var centre = Size / 2;
            var figure = new PathFigure { StartPoint = new Point(centre, centre - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle)), new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            grid.Children.Add(new Path { Stroke = Brush(brush), StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = new PathGeometry([figure]) });
        }
        return grid;
    }

    // Above the box, when its login ran out: when it goes on by itself, or a way to go on sooner.
    private void ShowLimitBanner()
    {
        if (_open is not { IsLimited: true } open)
        {
            _limitBanner.Visibility = Visibility.Collapsed;
            return;
        }
        _limitBanner.Visibility = Visibility.Visible;
        var resets = _main.ResumesAt(open);
        var title = Text("Usage limit reached", "A.Strong", 13.5, FontWeights.SemiBold);
        var said = Text(
            open.ResumeAtReset
                ? resets is { } at ? $"It goes on by itself at {When(at)}, when the limit resets." : "It goes on by itself once the limit resets."
                : resets is { } then ? $"The limit resets at {When(then)}. It won't go on until you say." : "It won't go on until you say.",
            "A.Muted", 12.5);
        said.TextWrapping = TextWrapping.Wrap;
        said.TextTrimming = TextTrimming.None;
        said.Margin = new Thickness(0, 2, 0, 0);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var toggle = Make("A.Pill", Text(open.ResumeAtReset ? "Don't go on by itself" : "Go on at reset", "A.Normal", 12.5), () =>
        {
            open.ResumeAtReset = !open.ResumeAtReset;
            open.Save(_main.ConversationsFolder);
            ShowLimitBanner();
            ShowConversations();
        });
        var now = Make("A.Pill", Text("Try now", "A.Normal", 12.5), async () =>
        {
            if (await _main.ResumeAsync(open) is { } problem) ShowNote(problem);
        });
        now.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(toggle);
        buttons.Children.Add(now);

        // Another login of the same kind can take it on straight away, with everything said so far.
        var current = _main.AgentProviderFor(open);
        foreach (var login in _main.AgentLogins().Where(login => login.Id != current?.Id))
        {
            var blocked = _main.Usage.BlockedUntil(login.Id) is { } until && until > DateTimeOffset.UtcNow;
            var other = Make("A.Primary", $"Go on with {login.Label}", async () =>
            {
                _main.SwitchLogin(open, login);
                if (await _main.ResumeAsync(open) is { } problem) ShowNote(problem);
                ShowUsage();
            });
            other.IsEnabled = !blocked;
            other.Margin = new Thickness(6, 0, 0, 0);
            other.ToolTip = blocked ? "That login has run out too." : "Switches this conversation to that login, with everything said so far, and goes on.";
            buttons.Children.Add(other);
        }

        var icon = Glyph("", "A.Accent", 16);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 12, 0);
        var body = new StackPanel { Children = { title, said, buttons } };
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(body);
        _limitBanner.Child = row;
    }

    // Above the box: the plan it keeps for itself as it works, ticked off as it goes.
    private void ShowSteps()
    {
        if (_open?.Steps is not { Count: > 0 } steps)
        {
            _stepsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        _stepsPanel.Visibility = Visibility.Visible;
        var done = steps.Count(step => step.Status == "completed");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = _stepsOpen ? "" : "", FontFamily = new FontFamily(Icons), FontSize = 9, Foreground = Brush("A.Faint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0) });
        header.Children.Add(Text("Plan", "A.Normal", 12.5, FontWeights.SemiBold));
        header.Children.Add(Text($"  ·  {done} of {steps.Count} done", "A.Faint", 12.5));
        var toggle = Make("A.Ghost", header, () =>
        {
            _stepsOpen = !_stepsOpen;
            ShowSteps();
        });
        toggle.Padding = new Thickness(10, 5, 10, 5);
        toggle.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(toggle, $"Plan, {done} of {steps.Count} done, {(_stepsOpen ? "showing" : "click to show")} each step");
        var rows = new StackPanel { Children = { toggle } };
        if (_stepsOpen)
        {
            var list = new StackPanel { Margin = new Thickness(10, 2, 10, 4) };
            foreach (var step in steps) list.Children.Add(StepRow(step));
            rows.Children.Add(new ScrollViewer { MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        }
        _stepsPanel.Child = rows;
    }

    private FrameworkElement StepRow(AgentStep step)
    {
        FrameworkElement mark = step.Status switch
        {
            "completed" => Glyph("", "A.Success", 11),
            "in_progress" => new Border { Height = 17, Child = Pulse() },
            _ => Glyph("", "A.Faint", 11),
        };
        mark.Width = 18;
        if (mark is TextBlock glyph) glyph.Margin = new Thickness(0, 2, 0, 0);
        mark.VerticalAlignment = VerticalAlignment.Top;
        mark.HorizontalAlignment = HorizontalAlignment.Left;
        var words = Text(step.Status == "in_progress" && step.ActiveForm is { Length: > 0 } doing ? doing : step.Content, step.Status switch { "completed" => "A.Faint", "in_progress" => "A.Strong", _ => "A.Muted" }, 12.5);
        words.TextWrapping = TextWrapping.Wrap;
        words.TextTrimming = TextTrimming.None;
        if (step.Status == "completed") words.TextDecorations = TextDecorations.Strikethrough;
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(words);
        return row;
    }

    // In the box, with more than one login: which one it works with, switched with everything said so far.
    private void ShowLoginPill()
    {
        if (_loginPill is null) return;
        var logins = _main.AgentLogins();
        var current = _open is null ? null : _main.AgentProviderFor(_open);
        _loginPill.Visibility = logins.Count > 1 && current is not null ? Visibility.Visible : Visibility.Collapsed;
        if (current is null) return;
        ((TextBlock)((StackPanel)_loginPill.Content).Children[1]).Text = current.Label;
        _loginPill.IsEnabled = _open is { IsWorking: false };
        _loginPill.ToolTip = _open is { IsWorking: true } ? "Switch logins once it's done" : "Which login it works with";
    }

    private void PickLogin()
    {
        if (_open is not { } open || _loginPill is null) return;
        var current = _main.AgentProviderFor(open);
        var rows = new StackPanel();
        Popup? menu = null;
        foreach (var login in _main.AgentLogins())
        {
            var row = new DockPanel();
            var tick = Glyph(login.Id == current?.Id ? "" : "", "A.Accent", 12);
            tick.Width = 16;
            DockPanel.SetDock(tick, Dock.Right);
            row.Children.Add(tick);
            var name = new StackPanel { Children = { Text(login.Label, "A.Normal", 13) } };
            if (_main.Usage.For(login.Id).FirstOrDefault(limit => limit.Kind == "five_hour") is { Used: { } used })
            {
                name.Children.Add(Text(_main.Usage.BlockedUntil(login.Id) is { } until && until > DateTimeOffset.UtcNow ? $"Ran out, resets {When(until)}" : $"Session {used:0}% used", "A.Faint", 11.5));
            }
            row.Children.Add(name);
            rows.Children.Add(Make("A.Ghost", row, () =>
            {
                menu!.IsOpen = false;
                _main.SwitchLogin(open, login);
                ShowUsage();
            }));
        }
        rows.Children.Add(new TextBlock { Text = "It goes on with everything said so far.", Foreground = Brush("A.Faint"), FontSize = 11, Margin = new Thickness(10, 4, 10, 4) });
        menu = Menu(_loginPill, PlacementMode.Top, rows);
        menu.IsOpen = true;
    }

    // Its plan, from plan mode: go ahead with it, or keep planning. Kept on screen once answered.
    private FrameworkElement PlanCard(AgentItem item)
    {
        var rows = new StackPanel();
        var waiting = item.RequestState == AgentRequestState.Waiting;
        var title = IconAndText("", waiting ? "Proposed plan" : item.RequestState == AgentRequestState.Allowed ? "Plan · went ahead" : item.RequestState == AgentRequestState.Denied ? "Plan · kept planning" : "Plan", waiting ? "A.Strong" : "A.Muted", 13.5);
        ((TextBlock)title.Children[1]).FontWeight = FontWeights.SemiBold;
        ((TextBlock)title.Children[0]).Foreground = Brush(waiting ? "A.Accent" : "A.Faint");
        rows.Children.Add(title);

        var plan = new AgentMarkdown(new AgentMarkdown.Look(
            Brush("A.Normal"), Brush("A.Strong"), Brush("A.Muted"), Brush("A.Normal"), Brush("A.CodeBack"), Brush("A.Surface"), Brush("A.Border"), Brush("A.Accent"),
            FontFamily, new FontFamily("Cascadia Mono, Consolas"), 13.5, 22));
        plan.View.SelectionBrush = Brush("A.Accent");
        plan.Show(item.Text);
        rows.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Child = plan.View });

        if (waiting)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var go = Make("A.Primary", "Go ahead", () => _open?.AnswerPlan(item, goAhead: true));
            go.ToolTip = "It starts on the plan, editing the project's files freely.";
            var keep = Make("A.Pill", Text("Keep planning", "A.Normal", 13), () =>
            {
                _open?.AnswerPlan(item, goAhead: false);
                _input.Focus();
            });
            keep.Margin = new Thickness(8, 0, 0, 0);
            keep.Padding = new Thickness(14, 6, 14, 6);
            keep.ToolTip = "It waits for what you say next about the plan.";
            buttons.Children.Add(go);
            buttons.Children.Add(keep);
            rows.Children.Add(buttons);
        }
        return Card(rows, waiting);
    }
}
