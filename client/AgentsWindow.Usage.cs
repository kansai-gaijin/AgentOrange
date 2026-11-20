using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentOrange;

/// <summary>
/// What a conversation's login has left, and what the agent is up to, as T3 Code shows them: the
/// Usage page with each limit's bar (what's left, what a reset gives back, and where an even pace
/// would be by now), a banner on top of the box when a limit stops it (it goes on by itself once
/// that resets), the plan it keeps for itself, and plan mode's proposed plan.
/// </summary>
public sealed partial class AgentsWindow
{
    private static readonly CultureInfo Clock = CultureInfo.InvariantCulture;

    private readonly Border _limitBanner = new() { Visibility = Visibility.Collapsed };
    private readonly Border _stepsBanner = new() { Visibility = Visibility.Collapsed };
    private bool _stepsOpen = true;

    private void BuildUsageParts()
    {
        _banners.Children.Add(_limitBanner);
        _banners.Children.Add(_questionBanner);
        _banners.Children.Add(_stepsBanner);
        _main.Usage.Changed += UsageChanged;
        Closed += (_, _) => _main.Usage.Changed -= UsageChanged;
        _ = _main.ProbeUsageAsync();
    }

    private void UsageChanged()
    {
        ShowUsage();
        ShowControls(); // A Codex login's models may have come with it.
        if (_overlay.Visibility == Visibility.Visible) ShowUsagePage();
    }

    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        return local.Date == today ? local.ToString("h:mm tt", Clock)
            : local.Date == today.AddDays(1) ? $"tomorrow {local.ToString("h:mm tt", Clock)}"
            : local.ToString("ddd h:mm tt", Clock);
    }

    // As T3 Code's reset badge says it: 4h 21m, 3d 18h.
    private static string Until(DateTimeOffset at)
    {
        var left = at - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "now";
        return left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{Math.Max(1, left.Minutes)}m";
    }

    /// <summary>The banners on top of the box, for the open conversation and its login.</summary>
    private void ShowUsage()
    {
        ShowLimitBanner();
        ShowQuestions();
        ShowSteps();
        ShowControls();
    }

    // A panel docked on top of the box, as T3 Code's composer banners: 44 px narrower than it,
    // rounded at the top, tucked 17 px under it.
    private Border BannerFace(UIElement content, Brush? outline = null, Brush? tint = null) => new()
    {
        Margin = new Thickness(22, 0, 22, -17),
        Background = tint ?? Ocean.Composer,
        BorderBrush = outline ?? Ocean.ComposerOutline,
        BorderThickness = new Thickness(1, 1, 1, 0),
        CornerRadius = new CornerRadius(16, 16, 0, 0),
        Padding = new Thickness(16, 10, 16, 27),
        Child = content,
    };

    // When its login ran out: when it goes on by itself, or ways to go on sooner.
    private void ShowLimitBanner()
    {
        if (_open is not { IsLimited: true } open)
        {
            _limitBanner.Visibility = Visibility.Collapsed;
            return;
        }
        _limitBanner.Visibility = Visibility.Visible;
        var resets = _main.ResumesAt(open);
        var title = Ocean.IconText("gauge", "Usage limit reached", Ocean.Foreground, 12, 14, FontWeights.Medium);
        var said = Ocean.Wrapped(
            open.ResumeAtReset
                ? resets is { } at ? $"It goes on by itself at {When(at)}, when the limit resets." : "It goes on by itself once the limit resets."
                : resets is { } then ? $"The limit resets at {When(then)}. It won't go on until you say." : "It won't go on until you say.",
            Ocean.MutedText, 12, 16);
        said.Margin = new Thickness(20, 2, 0, 0);

        Button Action(string words, Action click, bool strong = false)
        {
            var button = Ocean.Ghost(Ocean.Text(words, strong ? Ocean.Foreground : Ocean.MutedText, 12, FontWeights.Medium), click, 8, new Thickness(8, 0, 8, 0));
            button.Height = 24;
            return button;
        }
        var buttons = new WrapPanel { Margin = new Thickness(14, 6, 0, 0) };
        buttons.Children.Add(Action(open.ResumeAtReset ? "Don't go on by itself" : "Go on at reset", () =>
        {
            open.ResumeAtReset = !open.ResumeAtReset;
            open.Save(_main.ConversationsFolder);
            ShowLimitBanner();
            ShowConversations();
        }));
        buttons.Children.Add(Action("Try now", async () =>
        {
            if (await _main.ResumeAsync(open) is { } problem) ShowNote(problem);
        }));

        // Another login of the same kind can take it on straight away, with everything said so far.
        var current = _main.AgentProviderFor(open);
        foreach (var login in _main.AgentLogins().Where(login => login.Id != current?.Id && login.Kind == current?.Kind))
        {
            var blocked = _main.Usage.BlockedUntil(login.Id) is { } until && until > DateTimeOffset.UtcNow;
            var other = Action($"Go on with {login.Label}", async () =>
            {
                _main.SwitchLogin(open, login);
                if (await _main.ResumeAsync(open) is { } problem) ShowNote(problem);
                ShowUsage();
            }, strong: true);
            other.IsEnabled = !blocked;
            other.ToolTip = blocked ? "That login has run out too." : "Switches this conversation to that login, with everything said so far, and goes on.";
            buttons.Children.Add(other);
        }
        var face = BannerFace(new StackPanel { Children = { title, said, buttons } }, new SolidColorBrush(Color.FromArgb(0x47, 0xFE, 0x9A, 0x00)), Ocean.WarningSurface);
        _limitBanner.Child = face;
    }

    // The plan it keeps for itself as it works, ticked off as it goes.
    private void ShowSteps()
    {
        if (_open?.Steps is not { Count: > 0 } steps)
        {
            _stepsBanner.Visibility = Visibility.Collapsed;
            return;
        }
        _stepsBanner.Visibility = Visibility.Visible;
        var done = steps.Count(step => step.Status == "completed");
        var chevron = AgentIcons.Line("chevron-down", 12, Ocean.MutedText);
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform(_stepsOpen ? 0 : -90);
        var head = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        var label = Ocean.IconText("list-checks", "Plan", Ocean.MutedText, 12, 14, FontWeights.Medium);
        label.Children.Add(Ocean.Text($"  {done}/{steps.Count}", Ocean.MutedText, 12));
        head.Children.Add(label);
        var toggle = Ocean.Ghost(head, () =>
        {
            _stepsOpen = !_stepsOpen;
            ShowSteps();
        }, 6, new Thickness(4, 0, 4, 0), Brushes.Transparent);
        toggle.Height = 24;
        toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(toggle, $"Plan, {done} of {steps.Count} done, {(_stepsOpen ? "showing" : "click to show")} each step");
        var rows = new StackPanel { Children = { toggle } };
        if (_stepsOpen)
        {
            var list = new StackPanel { Margin = new Thickness(4, 2, 4, 0) };
            foreach (var step in steps) list.Children.Add(StepRow(step));
            rows.Children.Add(new ScrollViewer { MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        }
        _stepsBanner.Child = BannerFace(rows);
        ((Border)_stepsBanner.Child).Padding = new Thickness(12, 6, 12, 25);
    }

    private FrameworkElement StepRow(AgentStep step)
    {
        var (icon, brush) = step.Status switch
        {
            "completed" => ("circle-check", Ocean.Success),
            "in_progress" => ("circle-dashed", Ocean.Info),
            _ => ("circle", Ocean.Secondary),
        };
        var mark = AgentIcons.Line(icon, 14, brush);
        mark.Margin = new Thickness(0, 3, 8, 0);
        mark.VerticalAlignment = VerticalAlignment.Top;
        var words = Ocean.Wrapped(step.Status == "in_progress" && step.ActiveForm is { Length: > 0 } doing ? doing : step.Content, step.Status switch { "completed" => Ocean.Secondary, "in_progress" => Ocean.Foreground, _ => Ocean.MutedText }, 13, 20);
        if (step.Status == "completed") words.TextDecorations = TextDecorations.Strikethrough;
        if (step.Status == "in_progress" && _open?.IsWorking == true) Ocean.Shine(words, Ocean.MutedText);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(words);
        return row;
    }

    // Plan mode's plan, as T3 Code's proposed plan card: go ahead with it, or keep planning.
    private FrameworkElement PlanCard(AgentItem item)
    {
        var waiting = item.RequestState == AgentRequestState.Waiting;
        var badge = new Border { Background = Ocean.Control, CornerRadius = new CornerRadius(8), Height = 24, Padding = new Thickness(8, 0, 8, 0), Child = Ocean.Text("Plan", Ocean.Foreground, 12, FontWeights.Medium), Margin = new Thickness(0, 0, 10, 0) };
        var state = waiting ? "Proposed plan" : item.RequestState == AgentRequestState.Allowed ? "Went ahead with it" : item.RequestState == AgentRequestState.Denied ? "Kept planning" : "No longer waiting";
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { badge, Ocean.Text(state, Ocean.Foreground, 14, FontWeights.Medium) } };

        var plan = new AgentMarkdown(new AgentMarkdown.Look(
            Ocean.Body, Ocean.Foreground, Ocean.MutedText, Ocean.Foreground, Ocean.CodeBack, Ocean.InlineCode, Ocean.Border, Ocean.Info,
            Inter, Ocean.Mono, 14, 22.75) { Rule = Ocean.Border60, Hover = Ocean.Accent });
        plan.View.SelectionBrush = Ocean.Focus;
        plan.Show(item.Text);
        var rows = new StackPanel { Children = { head, new Border { Margin = new Thickness(0, 16, 0, 0), Child = plan.View } } };

        if (waiting)
        {
            var go = Ocean.Ghost(Ocean.Text("Go ahead", Ocean.OnPrimary, 14, FontWeights.Medium), () => _open?.AnswerPlan(item, goAhead: true), 8, new Thickness(12, 0, 12, 0), Ocean.PrimaryHover, Ocean.Primary);
            go.Height = 28;
            go.ToolTip = "It starts on the plan, editing the project's files freely.";
            var keep = Ocean.Ghost(Ocean.Text("Keep planning", Ocean.Foreground, 14, FontWeights.Medium), () =>
            {
                _open?.AnswerPlan(item, goAhead: false);
                _input.Focus();
            }, 8, new Thickness(12, 0, 12, 0), new SolidColorBrush(Color.FromArgb(0xA3, 0x4F, 0x67, 0x7B)), new SolidColorBrush(Color.FromArgb(0x52, 0x4F, 0x67, 0x7B)));
            keep.Height = 28;
            keep.BorderBrush = Ocean.Input;
            keep.BorderThickness = new Thickness(1);
            keep.Margin = new Thickness(8, 0, 0, 0);
            keep.ToolTip = "It waits for what you say next about the plan.";
            rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { go, keep } });
        }
        return new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderBrush = Ocean.Border80,
            BorderThickness = new Thickness(1),
            Background = Ocean.Canvas,
            Padding = new Thickness(20),
            Margin = new Thickness(0, 4, 0, 12),
            Child = rows,
        };
    }

    // The Usage page, as T3 Code's: a panel over the window with a card for each limit of each login.
    private void ShowUsagePage()
    {
        _workPanelOpen = false;
        var refresh = Ocean.IconButton("refresh-cw", () => _ = _main.ProbeUsageAsync(), "Check again", 28, 16, Ocean.MutedText);
        var close = Ocean.IconButton("x", () => _overlay.Visibility = Visibility.Collapsed, "Close", 28, 16, Ocean.MutedText);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Children = { refresh, close } };
        var crumbs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { Ocean.Text("Usage", Ocean.MutedText, 14, FontWeights.Medium), Ocean.Text("  /  ", Ocean.Secondary, 14), Ocean.Text("Limits", Ocean.Foreground, 14, FontWeights.Medium) } };
        var bar = new DockPanel { Height = 48, Margin = new Thickness(20, 0, 8, 0) };
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        bar.Children.Add(crumbs);
        var header = new Border { BorderBrush = Ocean.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };

        var content = new StackPanel { MaxWidth = 1024, Margin = new Thickness(24, 24, 24, 48) };
        var logins = _main.AllAgentLogins();
        if (logins.Count == 0) content.Children.Add(Ocean.Wrapped("Add a login in Settings, under Agents, to see its usage here.", Ocean.MutedText, 14));
        foreach (var login in logins)
        {
            var claude = login.Kind == ProviderKind.Claude;
            var glyph = AgentIcons.Provider(claude, 16, Ocean.Foreground);
            glyph.Margin = new Thickness(0, 0, 8, 0);
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, content.Children.Count == 0 ? 0 : 20, 0, 12), Children = { glyph, Ocean.Text(claude ? "Claude" : "Codex", Ocean.Foreground, 14, FontWeights.Medium), Ocean.Text($"  {login.Label}", Ocean.MutedText, 14) } };
            content.Children.Add(heading);
            var limits = _main.Usage.For(login.Id).Where(limit => limit.Used is not null && limit.Kind != "overage").ToList();
            if (UsageNote(login, limits.Count > 0) is { } note)
            {
                var said = Ocean.Wrapped(note, Ocean.MutedText, 13, 20);
                said.Margin = new Thickness(0, 0, 0, limits.Count > 0 ? 12 : 0);
                content.Children.Add(said);
            }
            foreach (var limit in limits) content.Children.Add(LimitCard(limit, claude ? Ocean.Claude : Ocean.Foreground, login));
        }
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content });

        _overlay.Children.Clear();
        var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
        shade.MouseLeftButtonUp += (_, _) => _overlay.Visibility = Visibility.Collapsed;
        _overlay.Children.Add(shade);
        _overlay.Children.Add(new Border
        {
            Background = Ocean.Canvas,
            BorderBrush = Ocean.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(40),
            MaxWidth = 1280,
            MaxHeight = 960,
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 50, ShadowDepth = 20, Direction = 270, Opacity = 0.4 },
        });
        _overlay.Visibility = Visibility.Visible;
    }

    // Said above a login's limits: signed out, as its program says, or that they're as last heard,
    // and when. A login that's run out of its limit can stop saying them, and keeps what was heard (#120).
    private string? UsageNote(AgentProvider login, bool hasLimits)
    {
        var usage = _main.Usage;
        var signedOut = usage.NotSignedIn(login.Id) is { } why ? $"{why}, as its program says. Sign in again in Settings › Providers." : null;
        if (!hasLimits)
        {
            return signedOut
                ?? (usage.CheckedAt(login.Id) is null ? "Checking…" : "It didn't say its limits. A login that's run out of its limit can stop saying them until it resets.");
        }
        if (!usage.AsLastHeard(login.Id) || usage.HeardAt(login.Id) is not { } heard) return signedOut;
        var asHeard = $"As last heard, {When(heard)}.";
        return signedOut is not null ? $"{signedOut} {asHeard}"
            : usage.CheckedAt(login.Id) is null ? $"{asHeard} Checking…"
            : $"{asHeard} The latest check didn't say them, as a login that's run out of its limit can do.";
    }

    // A limit's card: what's left in big numbers, and its bar, as T3 Code's pooled limits.
    private FrameworkElement LimitCard(AgentLimit limit, Brush colour, AgentProvider login)
    {
        var left = Math.Clamp(100 - (limit.Used ?? 0), 0, 100);
        double? even = limit.ResetsAt is { } resets && limit.Length is { } length ? Math.Clamp(100 * (resets - DateTimeOffset.UtcNow) / length, 0, 100) : null;
        var pace = even is { } by ? (left < by - 5 ? "trending-up" : left > by + 5 ? "trending-down" : "gauge") : "gauge";

        var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        big.Children.Add(Ocean.Text($"{left:0}%", Ocean.Foreground, 30, FontWeights.SemiBold));
        var paceIcon = AgentIcons.Line(pace, 14, Ocean.MutedText);
        paceIcon.Margin = new Thickness(6, 0, 0, 0);
        var leftWord = new StackPanel { Orientation = Orientation.Horizontal, Children = { Ocean.Text("left", Ocean.MutedText, 14), paceIcon } };
        leftWord.VerticalAlignment = VerticalAlignment.Bottom;
        leftWord.Margin = new Thickness(8, 0, 0, 6);
        big.Children.Add(leftWord);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Ocean.Text(limit.Name, Ocean.Foreground, 14, FontWeights.Medium), big } };

        // The bar: what's left in the provider's colour, what a reset gives back hatched, and a marker where an even pace would be.
        var track = new Grid { Height = 32, Background = Ocean.Muted };
        track.SizeChanged += (_, _) => track.Clip = new RectangleGeometry(new Rect(track.RenderSize), 6, 6);
        var split = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) }, new ColumnDefinition { Width = new GridLength(100 - left, GridUnitType.Star) } } };
        split.Children.Add(new Rectangle { Fill = colour, Opacity = 0.35, RadiusX = 6, RadiusY = 6 });
        if (limit.ResetsAt is not null)
        {
            var stripes = new DrawingBrush(new GeometryDrawing(null, new Pen(colour, 1), Geometry.Parse("M0,5 L5,0 M-1,1 L1,-1 M4,6 L6,4")))
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 5, 5),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 5, 5),
                ViewboxUnits = BrushMappingMode.Absolute,
            };
            var hatch = new Rectangle { Fill = stripes, Opacity = 0.2 };
            Grid.SetColumn(hatch, 1);
            split.Children.Add(hatch);
        }
        track.Children.Add(split);
        if (even is { } at && at is > 1 and < 99)
        {
            var marks = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(at, GridUnitType.Star) }, new ColumnDefinition { Width = new GridLength(100 - at, GridUnitType.Star) } } };
            var mark = new Rectangle { Width = 2, Fill = Ocean.Foreground, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-1, 2, 0, 2) };
            Grid.SetColumn(mark, 1);
            marks.Children.Add(mark);
            track.Children.Add(marks);
        }
        if (limit.ResetsAt is { } when)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x17, 0x21, 0x2B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Child = Ocean.Text($"↻ {Until(when)}", Ocean.Foreground, 11, FontWeights.Medium),
            };
            track.Children.Add(badge);
        }

        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(176) }, new ColumnDefinition { Width = new GridLength(24) }, new ColumnDefinition() } };
        Grid.SetColumn(track, 2);
        track.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(words);
        grid.Children.Add(track);

        var card = new Border { CornerRadius = new CornerRadius(8), BorderBrush = Ocean.Border60, BorderThickness = new Thickness(1), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = grid, Background = Brushes.Transparent };
        card.ToolTip = LimitTip(limit, left, even, login);
        ToolTipService.SetInitialShowDelay(card, 300);
        AutomationProperties.SetName(card, $"{limit.Name}, {left:0}% left");
        return card;
    }

    // Its hover card, as T3 Code's: the login, what's left, when it resets, and where an even pace would be.
    private static FrameworkElement LimitTip(AgentLimit limit, double left, double? even, AgentProvider login)
    {
        var rows = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(72) }, new ColumnDefinition { Width = new GridLength(12) }, new ColumnDefinition() } };
        void Row(string label, string value)
        {
            var at = rows.RowDefinitions.Count;
            rows.RowDefinitions.Add(new RowDefinition());
            var name = Ocean.Text(label, Ocean.MutedText, 12);
            var shown = Ocean.Text(value, Ocean.Foreground, 12, FontWeights.Medium);
            name.Margin = shown.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(name, at);
            Grid.SetRow(shown, at);
            Grid.SetColumn(shown, 2);
            rows.Children.Add(name);
            rows.Children.Add(shown);
        }
        Row("Login", login.Label);
        Row("Left", $"{left:0}%");
        if (limit.ResetsAt is { } resets) Row("Resets", $"{resets.ToLocalTime().ToString("M/d H:mm", Clock)} · in {Until(resets)}");
        if (even is { } by) Row("Even pace", $"{by:0}% left by now");
        var title = Ocean.Text(limit.Name, Ocean.Foreground, 14, FontWeights.Medium);
        var line = new Border { Height = 1, Background = Ocean.Border60, Margin = new Thickness(0, 10, 0, 8) };
        return new StackPanel { Width = 260, Margin = new Thickness(6), Children = { title, line, rows } };
    }
}
