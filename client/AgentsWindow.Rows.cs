using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static AgentOrange.PanelParts;

namespace AgentOrange;

/// <summary>
/// T3 Code's sidebar thread row, copied from its code. A card of three lines:
/// <list type="bullet">
/// <item>the project, with the status or the time on the right;</item>
/// <item>the title;</item>
/// <item>the branch, with any pull request and the provider's icon.</item>
/// </list>
/// Under the mouse the time gives way to "✓ Settle". Settling shows "Settled, Ctrl+Z to undo" in
/// the footer for five seconds. Holding Ctrl shows Ctrl+1 to 9 on the first nine rows, which open
/// them. Settled conversations are slim rows on their shelf, with Un-settle under the mouse.
/// </summary>
public sealed partial class AgentsWindow
{
    private static readonly SolidColorBrush Indigo = Freeze(new SolidColorBrush(Color.FromRgb(0xA5, 0xB4, 0xFC)));
    private static readonly SolidColorBrush Emerald = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x6E, 0xE7, 0xB7)));
    private static readonly SolidColorBrush Title75 = Freeze(new SolidColorBrush(Color.FromArgb(0xBF, 0xFF, 0xFA, 0xFF)));

    private readonly Dictionary<AgentConversation, ContentControl> _statusSlots = [];
    private readonly List<AgentConversation> _rowOrder = [];
    private readonly StackPanel _undoNote = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private List<AgentConversation> _justSettled = [];
    private DispatcherTimer? _undoTimer;
    private DispatcherTimer? _jumpTimer;
    private bool _showJumps;

    private void BuildRowParts(StackPanel footer)
    {
        footer.Children.Add(_undoNote);

        // Ctrl+1 to 9 open the first nine; Ctrl+Z takes back the last settle.
        PreviewKeyDown += (_, key) =>
        {
            if (key.Key is Key.LeftCtrl or Key.RightCtrl && !key.IsRepeat)
            {
                _jumpTimer?.Stop();
                _jumpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _jumpTimer.Tick += (_, _) =>
                {
                    _jumpTimer.Stop();
                    _showJumps = true;
                    ShowConversationStates();
                };
                _jumpTimer.Start();
                return;
            }
            if (KeyState.Modifiers != ModifierKeys.Control) return;
            if (key.Key is >= Key.D1 and <= Key.D9 && key.Key - Key.D1 < _rowOrder.Count)
            {
                key.Handled = true;
                Show(_rowOrder[key.Key - Key.D1]);
                ShowConversations();
            }
            else if (key.Key == Key.Z && _justSettled.Count > 0 && Keyboard.FocusedElement is not TextBox)
            {
                key.Handled = true;
                foreach (var conversation in _justSettled) conversation.Settle(false);
                _justSettled = [];
                _undoNote.Children.Clear();
                ShowConversations();
            }
        };
        PreviewKeyUp += (_, key) =>
        {
            if (key.Key is not (Key.LeftCtrl or Key.RightCtrl)) return;
            _jumpTimer?.Stop();
            if (!_showJumps) return;
            _showJumps = false;
            ShowConversationStates();
        };
        Deactivated += (_, _) =>
        {
            _jumpTimer?.Stop();
            if (!_showJumps) return;
            _showJumps = false;
            ShowConversationStates();
        };
    }

    // Settles it from the list. The open one gives way to the next in the list, or a new one for its project.
    private void SettleFromList(AgentConversation conversation)
    {
        var next = _rowOrder.Count > 1 ? _rowOrder[(_rowOrder.IndexOf(conversation) + 1) % _rowOrder.Count] : null;
        if (!conversation.Settle(true))
        {
            ShowNote(conversation.IsWaitingOnWork
                ? "It's still waiting on work it started in the background. Stop that from the line above the box, or let it finish, before settling it."
                : "Stop it, or answer what it's asking, before settling it.");
            return;
        }
        if (_open == conversation) Show(next is not null && next != conversation ? next : _main.NewConversation(_main.AgentProjects().First(project => project.Id == conversation.ProjectId)));

        // "Settled, ⟲ Ctrl+Z to undo", for five seconds.
        _justSettled = _undoTimer?.IsEnabled == true ? [.. _justSettled.Where(each => each != conversation), conversation] : [conversation];
        _undoNote.Children.Clear();
        _undoNote.Children.Add(Ocean.Text(_justSettled.Count > 1 ? $"Settled {_justSettled.Count}, " : "Settled, ", Ocean.MutedText, 11));
        var undo = AgentIcons.Line("undo-2", 12, Ocean.MutedText);
        undo.Margin = new Thickness(0, 0, 4, 0);
        _undoNote.Children.Add(undo);
        _undoNote.Children.Add(Ocean.Text("Ctrl+Z to undo", Ocean.MutedText, 11));
        _undoTimer?.Stop();
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            _justSettled = [];
            _undoNote.Children.Clear();
        };
        _undoTimer.Start();
        ShowHeader();
        ShowConversations();
    }

    private FrameworkElement ConversationRow(AgentConversation conversation, int index)
    {
        var name = conversation.Title.Length > 0 ? OneLine(conversation.Title) : "New conversation";
        var selected = conversation == _open;
        var project = _main.ProjectName(conversation.ProjectId);
        var login = _main.AgentProviderFor(conversation);
        var folder = _main.ProjectFolder(conversation.ProjectId);
        var repo = folder is not null && RepoStates.TryGetValue(folder, out var known) ? known : null;
        if (folder is not null && repo is null && RepoAsked.Add(folder)) _ = RefreshRepoAsync(conversation.ProjectId, fetch: false);

        // Quiet when it isn't the open one and has nothing new to say.
        var muted = !selected && (conversation.IsWorking || conversation.IsWaitingOnWork || !conversation.Unseen && !conversation.IsWaiting);        var failed = conversation.Unseen && conversation.Items.LastOrDefault()?.Kind == AgentItemKind.Problem;

        // Line 1: the project, and the status or time, which the mouse swaps for the actions.
        var first = new DockPanel { Height = 20 };
        var mark = Monogram(project, 16);
        mark.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(mark, Dock.Left);
        first.Children.Add(mark);
        var status = new ContentControl { Content = ConversationState(conversation, index), Tag = index, VerticalAlignment = VerticalAlignment.Center };
        _statusSlots[conversation] = status;
        var settle = Ocean.Ghost(Ocean.IconText("check", "Settle", Ocean.MutedText, 12, 14, null, 4), () => SettleFromList(conversation), 6, new Thickness(6, 0, 6, 0), Brushes.Transparent);
        settle.Height = 20;
        settle.Margin = new Thickness(0, 0, -4, 0);
        settle.ToolTip = "Settle thread";
        AutomationProperties.SetName(settle, "Settle thread");
        settle.MouseEnter += (_, _) => Recolor(settle, Ocean.Foreground);
        settle.MouseLeave += (_, _) => Recolor(settle, Ocean.MutedText);
        settle.Click += (_, click) => click.Handled = true; // Not opening the row as well.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Children = { settle } };
        var slot = new Grid { MinWidth = 32, Children = { status, actions } };
        DockPanel.SetDock(slot, Dock.Right);
        first.Children.Add(slot);
        var projectName = Ocean.Text(project, Ocean.Secondary, 12);
        projectName.TextTrimming = TextTrimming.CharacterEllipsis;
        first.Children.Add(projectName);

        // Line 2: the title, with the provider's icon when there's no third line.
        var hasThird = repo?.Branch is not null;
        var second = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        FrameworkElement Provider()
        {
            var icon = ProviderMark(login, IsClaude(conversation), 14, Ocean.Sidebar);
            icon.Opacity = 0.6;
            icon.Margin = new Thickness(6, 0, 0, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Right);
            return icon;
        }
        if (!hasThird) second.Children.Add(Provider());
        var title = Ocean.Text(name, failed ? new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFA, 0xFF)) : muted ? Title75 : Ocean.Foreground, 14, muted ? FontWeights.Normal : FontWeights.Medium);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        second.Children.Add(title);

        var content = new StackPanel { Children = { first, second } };

        // Line 3: the branch, any pull request, and the provider.
        if (hasThird)
        {
            var third = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            third.Children.Add(Provider());
            if (repo!.PullRequestOpen && repo.PullRequestUrl is { } address)
            {
                var pr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                pr.Children.Add(WithGap(AgentIcons.Line("git-pull-request-arrow", 12, Emerald), 4));
                pr.Children.Add(Ocean.Text(address.TrimEnd('/').Split('/').LastOrDefault() ?? "", Ocean.Secondary, 12));
                DockPanel.SetDock(pr, Dock.Right);
                third.Children.Add(pr);
            }
            var branch = Ocean.Text(MiddleOut(repo.Branch!, 34), Ocean.Secondary, 12);
            third.Children.Add(branch);
            content.Children.Add(third);
        }

        var button = Ocean.Ghost(content, () => Show(conversation), 6, new Thickness(10, 8, 10, 8), Ocean.RowHover, selected ? Ocean.RowActive : null);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Margin = new Thickness(0, 2, 0, 2);
        if (muted && conversation.IsWorking) button.Opacity = 0.7;
        button.ToolTip = ConversationTip(conversation, name, project);
        ToolTipService.SetPlacement(button, PlacementMode.Right);
        ToolTipService.SetInitialShowDelay(button, 600);
        AutomationProperties.SetName(button, $"{name}, {project}");
        button.MouseEnter += (_, _) =>
        {
            button.Opacity = 1;
            if (muted) title.Foreground = Ocean.Foreground;
            if (_showJumps && index < 9) return;
            status.Visibility = Visibility.Hidden;
            actions.Visibility = Visibility.Visible;
        };
        button.MouseLeave += (_, _) =>
        {
            if (muted && conversation.IsWorking) button.Opacity = 0.7;
            if (muted) title.Foreground = Title75;
            status.Visibility = Visibility.Visible;
            actions.Visibility = Visibility.Collapsed;
        };
        button.MouseRightButtonUp += (_, click) =>
        {
            click.Handled = true;
            OfferActions(button, conversation);
        };
        return button;
    }

    // Titles kept from before they were made one line.
    private static string OneLine(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static void Recolor(Button button, Brush brush)
    {
        if (button.Content is not StackPanel row) return;
        foreach (var child in row.Children)
        {
            if (child is TextBlock words) words.Foreground = brush;
            else if (child is Viewbox { Child: Canvas { Children: [System.Windows.Shapes.Path path] } }) path.Stroke = brush;
        }
    }

    // A long branch keeps its start and end, as T3 cuts it in the middle.
    private static string MiddleOut(string text, int most) =>
        text.Length <= most ? text : $"{text[..(most / 2)]}…{text[^(most - most / 2 - 1)..]}";

    // T3's status: an icon and a word in the colour of the state, or the time since it last moved.
    private FrameworkElement ConversationState(AgentConversation conversation, int index)
    {
        if (_showJumps && index < 9) return Shortcut($"Ctrl+{index + 1}");
        FrameworkElement Status(string icon, string words, Brush brush, string? after = null)
        {
            var row = Ocean.IconText(icon, words, brush, 12, 16, FontWeights.Medium, 4);
            if (after is not null) row.Children.Add(Ocean.Text($" {after}", brush, 12, FontWeights.Medium));
            return row;
        }
        if (conversation.IsLimited && !conversation.IsWorking) return Status("circle-alert", "Limited", Ocean.Warning);
        if (conversation.IsWaiting)
        {
            var asking = conversation.Items.Any(item => item.Kind == AgentItemKind.Question && item.RequestState == AgentRequestState.Waiting);
            return asking ? Status("message-circle-question", "Input", Indigo) : Status("shield-question", "Approval", Ocean.Warning);
        }
        // T3's Waiting: its turn is held up by a wait, or its turn ended and it goes on once work it
        // waits on ends (a subagent, or a command it said it waits for), with how long it has waited.
        if (conversation.IsBlockedOnWait)
        {
            var since = conversation.Work.Where(work => work.IsRunning && work.Kind == BackgroundKind.Wait).Min(work => work.Started);
            return Waiting(Status("circle-dot-dashed", "Waiting", Ocean.MutedText, Took(DateTimeOffset.UtcNow - since)), conversation.Work.Last(work => work.IsRunning && work.Kind == BackgroundKind.Wait).Title);
        }
        if (conversation.IsWorking) return Status("circle-dashed", "Working", Ocean.Info, Took(DateTimeOffset.UtcNow - conversation.WorkingSince));
        if (conversation.IsWaitingOnWork) return Waiting(Status("circle-dot-dashed", "Waiting", Ocean.MutedText, Took(DateTimeOffset.UtcNow - conversation.TurnEndedAt)), WorkTitle(conversation.PendingWork).Title);
        if (conversation.Unseen && conversation.Items.LastOrDefault()?.Kind == AgentItemKind.Problem) return Status("circle-alert", "Failed", Ocean.Error);
        if (conversation.Unseen) return Status("circle-check", "Done", Ocean.Success);
        return Ocean.Text(Ago(conversation.Updated), Ocean.Secondary, 12);
    }

    // What it waits on, for the mouse, and for screen readers and the drive tool.
    private static FrameworkElement Waiting(FrameworkElement status, string onWhat)
    {
        status.ToolTip = onWhat;
        AutomationProperties.SetName(status, $"Waiting: {onWhat}");
        return status;
    }

    // Each second, and when Ctrl comes and goes: the rows' states, so "Working 12s" counts.
    private void ShowConversationStates()
    {
        foreach (var (conversation, slot) in _statusSlots)
        {
            if (slot.Tag is int index) slot.Content = ConversationState(conversation, index);
        }
    }

    // T3's slim settled row: dim until the mouse is over it, when the time gives way to Un-settle.
    private FrameworkElement SettledRow(AgentConversation conversation)
    {
        var name = conversation.Title.Length > 0 ? OneLine(conversation.Title) : "New conversation";
        var mark = Monogram(_main.ProjectName(conversation.ProjectId), 16);
        mark.Opacity = 0.4;
        mark.Margin = new Thickness(0, 0, 10, 0);
        var words = Ocean.Text(name, conversation == _open ? Ocean.Foreground : Ocean.Secondary, 14);
        words.TextTrimming = TextTrimming.CharacterEllipsis;
        var time = Ocean.Text(Ago(conversation.SettledAt ?? conversation.Updated), Ocean.Secondary, 12);
        var unsettle = Ocean.Ghost(AgentIcons.Line("undo-2", 14, Ocean.MutedText), () =>
        {
            conversation.Settle(false);
            ShowConversations();
            ShowHeader();
        }, 6, new Thickness(6, 0, 6, 0), Brushes.Transparent);
        unsettle.Height = 20;
        unsettle.ToolTip = "Un-settle thread";
        unsettle.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(unsettle, "Un-settle thread");
        unsettle.Click += (_, click) => click.Handled = true;
        var right = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Children = { time, unsettle } };
        var row = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(mark);
        row.Children.Add(right);
        row.Children.Add(words);
        var button = Ocean.Ghost(row, () => Show(conversation), 6, new Thickness(10, 0, 10, 0), Ocean.RowHover, conversation == _open ? Ocean.RowActive : null);
        button.Height = 36;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.MouseEnter += (_, _) =>
        {
            mark.Opacity = 1;
            words.Foreground = Ocean.Foreground;
            time.Visibility = Visibility.Hidden;
            unsettle.Visibility = Visibility.Visible;
        };
        button.MouseLeave += (_, _) =>
        {
            mark.Opacity = 0.4;
            words.Foreground = conversation == _open ? Ocean.Foreground : Ocean.Secondary;
            time.Visibility = Visibility.Visible;
            unsettle.Visibility = Visibility.Collapsed;
        };
        button.MouseRightButtonUp += (_, click) =>
        {
            click.Handled = true;
            OfferActions(button, conversation);
        };
        AutomationProperties.SetName(button, $"{name}, settled");
        return button;
    }
}
