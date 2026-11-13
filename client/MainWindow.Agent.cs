using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// The agent on a project's page. The page is drawn again whenever anything syncs, so each
/// project's conversation is built once and put back, or what's being typed would be lost.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, AgentChat> _agentChats = [];

    private sealed class AgentChat
    {
        public required StackPanel Root { get; init; }
        public required Border ProviderSlot { get; init; }
        public AgentSession? Session { get; set; }
        public string? ProviderId { get; set; }
        public bool Unseen { get; set; } // It finished while no one was looking at it.
    }

    private void MarkAgentRepliesSeen()
    {
        foreach (var chat in _agentChats.Values.Where(chat => chat.Root.IsVisible)) chat.Unseen = false;
    }

    private void AddAgent(StackPanel body, Project project, string folder, PageCheck found)
    {
        body.Children.Add(Section("Agent"));

        // An agent runs Git in the folder as it works, which runs whatever the folder's own settings say to.
        if (found.SettingsToAllow is not null)
        {
            var rows = new List<FrameworkElement>();
            AddFolderSettingsRow(rows, project, folder, found);
            body.Children.Add(Card(rows.ToArray()));
            return;
        }

        // Only Claude Code so far.
        var providers = Settings.Providers.List().Where(provider => provider.Enabled && provider.Kind == ProviderKind.Claude).ToList();
        var key = $"{project.Id}|{folder}";
        if (providers.Count == 0 && !_agentChats.ContainsKey(key))
        {
            body.Children.Add(new TextBlock { Text = "Add a Claude Code provider in Settings to work with an agent here.", Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(16, -4, 16, 0), TextWrapping = TextWrapping.Wrap });
            return;
        }

        if (!_agentChats.TryGetValue(key, out var chat)) chat = _agentChats[key] = NewAgentChat(project.Id, folder);
        (chat.Root.Parent as Panel)?.Children.Remove(chat.Root);

        // Providers can be added, renamed or turned off in Settings between one drawing and the next.
        if (providers.All(provider => provider.Id != chat.ProviderId) && chat.Session is not { IsRunning: true }) chat.ProviderId = providers.FirstOrDefault()?.Id;
        chat.ProviderSlot.Child = providers.Count > 1
            ? Choice(providers.Select(provider => provider.Label).ToArray(), providers.FirstOrDefault(provider => provider.Id == chat.ProviderId)?.Label ?? "", label =>
            {
                if (chat.Session is { IsRunning: true }) return;
                chat.ProviderId = providers.First(provider => provider.Label == label).Id;
            })
            : null;
        body.Children.Add(chat.Root);
        if (IsActive) chat.Unseen = false;
    }

    private AgentChat NewAgentChat(string projectId, string folder)
    {
        var transcript = new StackPanel();
        var input = Field("");
        input.Width = double.NaN;
        input.AcceptsReturn = true;
        input.TextWrapping = TextWrapping.Wrap;
        input.MinHeight = 34;
        input.MaxHeight = 160;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        System.Windows.Automation.AutomationProperties.SetName(input, "Message for the agent");

        var send = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Send", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        var providerSlot = new Border { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };

        var inputRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        inputRow.Children.Add(send);
        inputRow.Children.Add(input);

        var root = new StackPanel { Margin = new Thickness(16, -2, 16, 24), Children = { providerSlot, transcript, inputRow } };
        var chat = new AgentChat { Root = root, ProviderSlot = providerSlot };

        void Show(string text, string brush, double size = 13.5, bool own = false)
        {
            var line = new TextBlock { Text = text, Foreground = (Brush)FindResource(brush), FontSize = size, TextWrapping = TextWrapping.Wrap };
            transcript.Children.Add(own
                ? new Border { Background = (Brush)FindResource("HoverBackground"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 12, 0, 4), Child = line }
                : new Border { Margin = new Thickness(2, 6, 0, 0), Child = line });

            // Only while it's the page being looked at, as a reply can arrive for a project that's been left.
            if (root.IsVisible) _ = Dispatcher.InvokeAsync(ProjectView.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        async Task SendAsync()
        {
            if (chat.Session is { IsRunning: true } running)
            {
                running.Stop();
                return;
            }

            var prompt = input.Text.Trim();
            if (prompt.Length == 0) return;
            if (IsBusyWith(projectId))
            {
                Show("This project is busy. Send this once that finishes.", "TextMuted", 12.5);
                return;
            }

            // A different provider is a different login, which can't pick up the other's conversation.
            var provider = Settings.Providers.List().FirstOrDefault(known => known.Id == chat.ProviderId && known.Enabled);
            if (provider is null)
            {
                Show("That provider isn't available any more. Check Settings.", "TextMuted", 12.5);
                return;
            }
            if (chat.Session is null || chat.Session.Provider != provider) chat.Session = new AgentSession(provider, folder, () => AgentMcpConfig.Write(Settings.Editors, Settings.Folders.AgentFolder));

            input.Clear();
            Show(prompt, "TextStrong", own: true);
            send.Content = "Stop";
            try
            {
                await chat.Session.SendAsync(prompt, found => Show(found.Text, found.Kind == AgentEventKind.Text ? "TextNormal" : "TextMuted", found.Kind == AgentEventKind.Text ? 13.5 : 12.5));
            }
            finally
            {
                send.Content = "Send";
                if (!IsActive || !root.IsVisible) chat.Unseen = true;
            }
        }

        send.Click += async (_, _) => await SendAsync();
        input.PreviewKeyDown += async (_, pressed) =>
        {
            // Enter sends, and Shift+Enter starts a new line.
            if (pressed.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || chat.Session is { IsRunning: true }) return;
            pressed.Handled = true;
            await SendAsync();
        };
        return chat;
    }

    private bool AnAgentIsWorking => _agentChats.Values.Any(chat => chat.Session is { IsRunning: true });
}
