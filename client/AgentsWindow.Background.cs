using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// What the agent left going once its turn ends, and whether it waits on it, copied from T3 Code's
/// background work: one line above the box summing up all of it ("Waiting · Run the test suite
/// 35m 50s Details Stop"), and the Background work dialog from Details, a section for each piece.
/// The line shows only once a turn has ended, and goes when the last piece ends or a turn starts.
/// A person, or an agent driving the window, can tell a pause from a finish by it.
/// </summary>
public sealed partial class AgentsWindow
{
    private readonly Border _workLine = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(4, 0, 4, 8) };
    private StackPanel? _workSections; // The open Background work dialog's sections, redrawn as the work changes.

    // T3 lists its kinds in this order; parts of a milestone split across agents go first, as the biggest.
    private static readonly BackgroundKind[] KindOrder = [BackgroundKind.Part, BackgroundKind.Subagent, BackgroundKind.Command, BackgroundKind.Monitor, BackgroundKind.Task];

    /// <summary>T3's rule for the line: it waits if any of it is awaited, a subagent, or a background task, and only runs otherwise.</summary>
    private static bool LineWaits(IEnumerable<BackgroundWork> work) => work.Any(each => each.Holds);

    /// <summary>The line above the box, each second while any background work runs and the turn has ended.</summary>
    private void ShowWorkLine()
    {
        var pending = _open is { IsWorking: false } open ? open.PendingWork : [];
        if (_open is not { } conversation || pending.Count == 0)
        {
            _workLine.Visibility = Visibility.Collapsed;
            _workLine.Child = null;
            if (_workSections is not null) CloseWorkDialog();
            return;
        }

        var (title, mono) = WorkTitle(pending);
        var items = pending.Select(work => (Work: work, Label: LineLabel(work))).OrderBy(each => Array.IndexOf(KindOrder, each.Work.Kind)).ToList();

        // Muted after the title: a subagent's latest progress, or with several, every label.
        string? description;
        if (items.Count == 1) description = items[0].Work.Kind is BackgroundKind.Subagent or BackgroundKind.Part ? OneSpace(items[0].Work.Activity) : null;
        else
        {
            var latest = items.Where(each => OneSpace(each.Work.Activity) is not null).OrderByDescending(each => each.Work.ActivityAt).FirstOrDefault();
            description = latest.Work is not null ? $"{latest.Label}: {OneSpace(latest.Work.Activity)}" : string.Join(", ", items.Select(each => each.Label));
        }

        // As T3 lays it out: the icon, the words (cut short with an ellipsis when they don't fit), and the
        // actions straight after them, never squeezed.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = AgentIcons.Line(items.All(each => each.Work.Kind is not (BackgroundKind.Subagent or BackgroundKind.Part)) ? "terminal" : "circle-dashed", 14, Ocean.MutedText);
        icon.Margin = new Thickness(0, 1, 6, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);

        // Elapsed (one piece only), Details and Stop, 10 px apart, never squeezed.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (items.Count == 1) actions.Children.Add(Ocean.Text(Elapsed(DateTimeOffset.UtcNow - items[0].Work.Started), Ocean.MutedText, 12));
        var details = InlineButton("Details", ShowWorkDialog, strong: true);
        var stopping = conversation.IsStoppingWork;
        var stop = InlineButton(stopping ? "Stopping..." : "Stop", conversation.StopBackgroundWork, strong: true);
        stop.IsEnabled = !stopping;
        stop.Opacity = stopping ? 0.64 : 1;
        foreach (var action in new[] { details, stop })
        {
            if (actions.Children.Count > 0) action.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(action);
        }
        var words = new TextBlock { FontSize = 12, LineHeight = 16, Foreground = Ocean.MutedText, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (mono)
        {
            var split = title.IndexOf(" · ", StringComparison.Ordinal) + 3;
            words.Inlines.Add(new System.Windows.Documents.Run(title[..split]));
            words.Inlines.Add(new System.Windows.Documents.Run(title[split..]) { FontFamily = Ocean.Mono });
        }
        else
        {
            words.Inlines.Add(new System.Windows.Documents.Run(title));
        }
        if (description is { Length: > 0 }) words.Inlines.Add(new System.Windows.Documents.Run($"  {description}") { Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x96, 0x9C, 0xA6)) });
        words.ToolTip = description is { Length: > 0 } ? $"{title} {description}" : title;
        row.Children.Add(words);
        row.Children.Add(actions);
        actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // The line is as wide as the box under it, and is drawn again each second, so a resize catches up.
        var width = _composer.ActualWidth > 0 ? _composer.ActualWidth : ColumnWidth;
        words.MaxWidth = Math.Max(0, width - _workLine.Padding.Left - _workLine.Padding.Right - 20 - actions.DesiredSize.Width - actions.Margin.Left);

        AutomationProperties.SetName(row, description is { Length: > 0 } ? $"{title}. {description}" : title);
        _workLine.Child = row;
        _workLine.Visibility = Visibility.Visible;
        if (_workSections is not null) FillWorkDialog(conversation);
    }

    /// <summary>
    /// T3's title for the line: "Waiting · Run the tests" or "Running · npm run dev" for one piece
    /// (with the command in a fixed-width font when it's the command itself), "Waiting on a subagent"
    /// for one without a description, and for several, each kind counted: "Waiting on 1 subagent and
    /// 2 commands", "Running 3 commands".
    /// </summary>
    internal static (string Title, bool Mono) WorkTitle(IReadOnlyList<BackgroundWork> pending)
    {
        var waiting = LineWaits(pending);
        if (pending.Count == 1)
        {
            var work = pending[0];
            var label = LineLabel(work);
            var word = AgentConversation.KindWords(work.Kind).One;
            if (label == word) return ($"{(waiting ? "Waiting on" : "Running")} a {word}", false);
            return ($"{(waiting ? "Waiting" : "Running")} · {label}", work.Command is { } command && LineLabel(command) == label);
        }
        var groups = KindOrder.Select(kind => (Kind: kind, Count: pending.Count(each => each.Kind == kind))).Where(group => group.Count > 0)
            .Select(group => $"{group.Count} {(group.Count == 1 ? AgentConversation.KindWords(group.Kind).One : AgentConversation.KindWords(group.Kind).Several)}").ToList();
        var joined = groups.Count <= 1 ? string.Join("", groups) : $"{string.Join(", ", groups[..^1])} and {groups[^1]}";
        return (waiting ? $"Waiting on {joined}" : $"Running {joined}", false);
    }

    // T3's label for a piece of work on the line: a subagent's description without "Subagent: " (and a
    // path's last part, in words), a command's with its shell taken off and control characters shown,
    // or the kind's word when it has none.
    private static string LineLabel(BackgroundWork work)
    {
        var described = work.Title.Trim();
        if (described.Length == 0) described = work.Kind is BackgroundKind.Command or BackgroundKind.Monitor ? work.Command?.Trim() ?? "" : "";
        if (described.Length == 0) return AgentConversation.KindWords(work.Kind).One;
        return work.Kind switch
        {
            BackgroundKind.Subagent => SubagentLabel(described),
            BackgroundKind.Command or BackgroundKind.Monitor => LineLabel(described),
            _ => described,
        };
    }

    private static string SubagentLabel(string described)
    {
        var rest = Regex.Replace(described, @"^Subagent:\s*", "", RegexOptions.IgnoreCase);
        var path = Regex.Match(rest, @"^/root/(?:[^/]+/)*([^/]+)/?$");
        if (!path.Success) return rest.Trim();
        var named = Regex.Replace(Regex.Replace(path.Groups[1].Value, @"[_\s]+", " ").Trim(), @"(^|\s)\S", each => each.Value.ToUpperInvariant());
        return named.Length > 0 ? named : rest.Trim();
    }

    // A command as T3 shows it on the line: inside its shell (bash -lc "...", powershell -Command ...)
    // when it's wrapped in one, with control characters as their pictures.
    private static string LineLabel(string command)
    {
        var wrapped = Regex.Match(command.Trim(), "^\"?(?:[^\"\\s]*[\\\\/])?(?:bash|sh|zsh|dash|powershell|pwsh|cmd)(?:\\.exe)?\"?\\s+(?:-[A-Za-z]+\\s+)*?(?:-l?c|-Command|/c|/C)\\s+(.+)$", RegexOptions.Singleline);
        var inner = wrapped.Success ? wrapped.Groups[1].Value.Trim() : command.Trim();
        if (wrapped.Success && inner.Length >= 2 && (inner[0] == '"' && inner[^1] == '"' || inner[0] == '\'' && inner[^1] == '\'')) inner = inner[1..^1];
        return string.Concat(inner.Select(ch => ch == '\x7F' ? "␡" : ch < ' ' ? ((char)(0x2400 + ch)).ToString() : ch.ToString()));
    }

    private static string? OneSpace(string? text) => text is null ? null : Regex.Replace(text, @"\s+", " ").Trim() is { Length: > 0 } one ? one : null;

    /// <summary>T3's elapsed time: 5.3s under ten seconds, 42s, 35m 50s, 1h 2m 3s, never under a second.</summary>
    internal static string Elapsed(TimeSpan span)
    {
        var ms = Math.Max(1000, span.TotalMilliseconds);
        if (ms < 10_000) return $"{(Math.Round(ms / 100) / 10).ToString(CultureInfo.InvariantCulture)}s";
        var seconds = (long)Math.Round(ms / 1000);
        if (seconds < 60) return $"{seconds}s";
        var (hours, minutes, rest) = (seconds / 3600, seconds % 3600 / 60, seconds % 60);
        return hours == 0 ? $"{minutes}m {rest}s" : minutes > 0 ? $"{hours}h {minutes}m {rest}s" : rest > 0 ? $"{hours}h {rest}s" : $"{hours}h";
    }

    // T3's Background work dialog: its title, and a section for each piece, in the order it started.
    private void ShowWorkDialog()
    {
        if (_open is not { } conversation) return;
        var title = Ocean.Wrapped("Background work", Ocean.Foreground, 20, 26);
        title.FontWeight = FontWeights.SemiBold;
        var header = new Border { Padding = new Thickness(24, 24, 24, 12), Child = title };
        _workSections = new StackPanel();
        var panel = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 4, 24, 24), Content = _workSections };
        var closer = Ocean.IconButton("x", CloseWorkDialog, "Close", 28, 16, Ocean.MutedText);
        closer.HorizontalAlignment = HorizontalAlignment.Right;
        closer.VerticalAlignment = VerticalAlignment.Top;
        closer.Margin = new Thickness(0, 8, 8, 0);
        var stack = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        stack.Children.Add(header);
        stack.Children.Add(panel);
        var card = new Border
        {
            MaxWidth = 512,
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Ocean.Glass,
            BorderBrush = Ocean.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = new Grid { Children = { stack, closer } },
        };
        AutomationProperties.SetName(card, "Background work");
        _overlays.ShowDialog(card);
        FillWorkDialog(conversation);
    }

    private void CloseWorkDialog()
    {
        if (_workSections is null) return;
        _workSections = null;
        _overlays.CloseDialog();
    }

    private void FillWorkDialog(AgentConversation conversation)
    {
        if (_workSections is null) return;
        // Closed some other way, such as a click on the dimmed window.
        if (_overlays.Dialogs.Visibility != Visibility.Visible)
        {
            _workSections = null;
            return;
        }
        _workSections.Children.Clear();
        foreach (var work in conversation.PendingWork)
        {
            var section = WorkSection(work);
            if (_workSections.Children.Count > 0) section.Margin = new Thickness(0, 20, 0, 0);
            _workSections.Children.Add(section);
        }
    }

    // One piece: its title, Waiting or Running on the right, and a command's command line.
    private FrameworkElement WorkSection(BackgroundWork work)
    {
        var command = work.Command?.Trim();
        var described = work.Title.Trim();
        var title = described.Length > 0 && described != command ? described : command is { Length: > 0 } ? "Command" : AgentConversation.KindWords(work.Kind).One;
        // A part says how it stands, such as Running, 1 of 2 tasks done.
        var stateWords = work.Kind == BackgroundKind.Part && OneSpace(work.Activity) is { } part ? part : work.Awaited ? "Waiting" : "Running";
        var state = Ocean.Text(stateWords, Ocean.MutedText, 12);
        state.Margin = new Thickness(12, 0, 0, 0);
        state.VerticalAlignment = VerticalAlignment.Bottom;
        var heading = Ocean.Wrapped(title, Ocean.Foreground, 14, 20);
        heading.FontWeight = FontWeights.Medium;
        var top = new DockPanel();
        DockPanel.SetDock(state, Dock.Right);
        top.Children.Add(state);
        top.Children.Add(heading);
        var section = new StackPanel { Children = { top } };

        if (command is { Length: > 0 })
        {
            var code = Ocean.Wrapped(command, new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFA, 0xFF)), 12, 16);
            code.FontFamily = Ocean.Mono;
            section.Children.Add(new Border
            {
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                BorderBrush = Ocean.Border,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0x23, 0x35, 0x44)),
                Child = code,
            });
        }

        // Its output so far, while it runs, as T3 streams a command's: Codex sends it as it comes.
        if (work.Kind is BackgroundKind.Command && _open?.Items.FirstOrDefault(item => item.Id == work.Id)?.Detail is { Length: > 0 } output)
        {
            var shown = new TextBox
            {
                Text = output.TrimEnd(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = Ocean.MutedText,
                FontFamily = Ocean.Mono,
                FontSize = 12,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 8, 0, 0),
            };
            shown.Loaded += (_, _) => shown.ScrollToEnd();
            AutomationProperties.SetName(shown, $"Output of {title}");
            section.Children.Add(shown);
        }
        AutomationProperties.SetName(section, $"{title}, {stateWords}");
        return section;
    }
}
