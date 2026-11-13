using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// Agent conversations, kept here rather than in their window, so they go on working with the window
/// closed. Each is saved to its own file in the agent files folder, and stays on this PC, as the agent
/// program's own record of it does.
/// </summary>
public partial class MainWindow
{
    private List<AgentConversation>? _conversations;
    private AgentsWindow? _agentsWindow;

    internal string ConversationsFolder => Path.Combine(Settings.Folders.AgentFolder, "conversations");

    internal List<AgentConversation> Conversations
    {
        get
        {
            if (_conversations is not null) return _conversations;
            _conversations = [];
            if (Directory.Exists(ConversationsFolder))
            {
                foreach (var file in Directory.EnumerateFiles(ConversationsFolder, "*.json"))
                {
                    if (AgentConversation.Load(file) is { } loaded) Keep(loaded);
                }
            }
            return _conversations;
        }
    }

    /// <summary>Each login's subscription limits, as last said.</summary>
    internal AgentUsage Usage { get; } = new();

    private DispatcherTimer? _agentClock;

    private void Keep(AgentConversation conversation)
    {
        _conversations!.Add(conversation);
        conversation.LimitsReported += (provider, limits, complete) => Usage.Update(provider, limits, complete);
        conversation.StateChanged += () =>
        {
            // Done, or wanting something, with no one looking at it.
            if ((!conversation.IsWorking || conversation.IsWaiting) && _agentsWindow?.IsLookingAt(conversation) != true) conversation.Unseen = true;
            conversation.Save(ConversationsFolder);
            _agentsWindow?.ShowConversations();
        };
    }

    internal AgentConversation NewConversation(Project project)
    {
        var conversation = new AgentConversation { Id = Guid.NewGuid().ToString("N"), ProjectId = project.Id };
        Keep(conversation);
        return conversation;
    }

    internal void DeleteConversation(AgentConversation conversation)
    {
        conversation.End();
        _conversations?.Remove(conversation);
        try
        {
            File.Delete(Path.Combine(ConversationsFolder, $"{conversation.Id}.json"));

            // Its pictures went nowhere else. Files it was sent are the person's own and stay.
            foreach (var picture in conversation.Items.SelectMany(item => item.Attachments ?? []).Where(attachment => attachment.IsImage && Path.GetDirectoryName(attachment.Path) == AttachmentsFolder))
            {
                File.Delete(picture.Path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't delete an agent conversation: {error.Message}");
        }
    }

    internal List<Project> AgentProjects() => _database.ListProjects();

    internal Project? ProjectOnScreen => _openProject;

    // Only Claude Code so far.
    internal AgentProvider? AgentProviderFor(AgentConversation conversation)
    {
        var providers = Settings.Providers.List().Where(provider => provider.Enabled && provider.Kind == ProviderKind.Claude).ToList();
        return providers.FirstOrDefault(provider => provider.Id == conversation.ProviderId) ?? providers.FirstOrDefault();
    }

    /// <summary>Why the conversation can't be sent anything right now, or null when it can.</summary>
    internal async Task<string?> AgentProblemAsync(AgentConversation conversation)
    {
        if (AgentProviderFor(conversation) is null) return "Add a Claude Code provider in Settings, under Agents, to work with an agent.";
        if (_database.GetProjectFolder(conversation.ProjectId) is not { } folder || !Directory.Exists(folder)) return "This project has no folder on this PC yet. Set one up on its project page.";
        if (IsBusyWith(conversation.ProjectId)) return "This project is busy. Send this once that finishes.";

        // An agent runs Git in the folder as it works, which runs whatever the folder's own settings say to.
        var runnable = await GitTools.FolderRunnableSettingsAsync(folder);
        if (runnable is null || !GitTools.AreAllowed(folder, runnable)) return "This folder's own Git settings need allowing first, on the project's page.";

        conversation.Folder = folder;
        return null;
    }

    internal AgentConversation.Setup PrepareAgent(AgentConversation conversation)
    {
        var agentFolder = Settings.Folders.AgentFolder;
        var provider = AgentProviderFor(conversation) ?? throw new IOException("There's no Claude Code provider.");
        var mcp = AgentMcpConfig.Write(Settings.Editors, agentFolder);
        var servers = (JsonNode.Parse(File.ReadAllText(mcp))?["mcpServers"] as JsonObject)?.Select(server => server.Key).ToList() ?? [];
        Directory.CreateDirectory(AttachmentsFolder);
        return new AgentConversation.Setup(provider, mcp, servers, AgentSkills.ClaudePlugin(agentFolder), AgentBriefing.Write(Settings.Editors, agentFolder), AttachmentsFolder);
    }

    /// <summary>The logins a conversation can go on with: every Claude Code one turned on.</summary>
    internal List<AgentProvider> AgentLogins() => [.. Settings.Providers.List().Where(provider => provider.Enabled && provider.Kind == ProviderKind.Claude)];

    internal void SwitchLogin(AgentConversation conversation, AgentProvider to)
    {
        if (AgentProviderFor(conversation) is { } from) conversation.SwitchProvider(from, to);
    }

    /// <summary>When a conversation stopped by its login's limit can go on: when it said, or else when its login's limits do.</summary>
    internal DateTimeOffset? ResumesAt(AgentConversation conversation) =>
        conversation.LimitResetsAt ?? Usage.BlockedUntil(AgentProviderFor(conversation)?.Id);

    // Every 20 seconds: conversations whose limit has reset go on by themselves, as in T3 Code, and
    // every five minutes, with the agents window open or a conversation waiting on a limit, each
    // login is asked how much of its limits are used.
    private void StartAgentClock()
    {
        if (_agentClock is not null) return;
        var lastProbe = DateTimeOffset.MinValue;
        _agentClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _agentClock.Tick += async (_, _) =>
        {
            var conversations = _conversations ?? [];
            foreach (var conversation in conversations.Where(each => each.IsLimited && each.ResumeAtReset && !each.IsWorking && !each.Settled).ToList())
            {
                // Long overdue, it's been left on purpose, so it isn't woken.
                if (ResumesAt(conversation) is not { } resets || resets > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - resets > TimeSpan.FromHours(24)) continue;
                await ResumeAsync(conversation);
            }

            var wanted = _agentsWindow is not null || conversations.Any(each => each.IsLimited);
            if (!wanted || DateTimeOffset.UtcNow - lastProbe < TimeSpan.FromMinutes(5)) return;
            lastProbe = DateTimeOffset.UtcNow;
            await ProbeUsageAsync();
        };
        _agentClock.Start();
    }

    internal async Task ProbeUsageAsync()
    {
        var folder = Path.Combine(Settings.Folders.AgentFolder, "usage-check");
        foreach (var login in AgentLogins())
        {
            // One running already says it as it goes.
            if (_conversations?.Any(each => each.IsWorking && each.ProviderId == login.Id) == true) continue;
            await Usage.ProbeAsync(login, folder);
        }
    }

    /// <summary>Goes on where it stopped, as the person would by saying so.</summary>
    internal async Task<string?> ResumeAsync(AgentConversation conversation)
    {
        if (await AgentProblemAsync(conversation) is { } problem) return problem;
        conversation.Send("Continue where you left off.", [], ViewLabel(), DescribeView(), () => PrepareAgent(conversation), automatic: true);
        return null;
    }

    /// <summary>What the person is looking at, in a few words, such as "Animal Planet › Characters › Player".</summary>
    internal string ViewLabel()
    {
        if (_openProject is not { } project) return "the list of projects";
        var parts = new List<string> { project.Name };
        if (_openSection is { } section && section.ProjectId == project.Id)
        {
            parts.Add(section.Name);
            if (OpenCardOf(project) is { } card && CardPages.ContainsKey(section.Name)) parts.Add(CardName(card, section.Name));
        }
        return string.Join(" › ", parts);
    }

    /// <summary>Where pictures sent to agents are kept, on this PC only.</summary>
    internal string AttachmentsFolder => Path.Combine(Settings.Folders.AgentFolder, "attachments");

    internal string? ProjectFolderOf(AgentConversation conversation) => _database.GetProjectFolder(conversation.ProjectId);

    private ScreenshotHotkey? _screenshotHotkey;

    // From anywhere: a screenshot for the conversation open in the agents window, or a new one about the project on screen.
    private void StartScreenshotHotkey()
    {
        StartAgentClock();
#if DEBUG
        if (Headless.Active) return;
#endif
        _screenshotHotkey = new ScreenshotHotkey(this, async () =>
        {
            if (_takingScreenshot) return;
            _takingScreenshot = true;
            try
            {
                var taken = await AgentAttachments.TakeScreenshotAsync();
                if (taken is null) return;
                OpenAgents_Click(this, new RoutedEventArgs());
                _agentsWindow?.AttachScreenshotHere(taken);
            }
            finally
            {
                _takingScreenshot = false;
            }
        });
    }

    private bool _takingScreenshot;

    internal string ProjectName(string projectId) => _database.ListProjects().FirstOrDefault(project => project.Id == projectId)?.Name ?? "A project that's gone";

    private bool AnAgentIsWorking => _conversations?.Any(conversation => conversation.IsWorking) == true;

    private int UnseenAgents => _conversations?.Count(conversation => conversation.Unseen) ?? 0;

    // A window of its own, so it can stay beside whatever the main one shows.
    private void OpenAgents_Click(object sender, RoutedEventArgs e)
    {
        if (_agentsWindow is null)
        {
            _agentsWindow = new AgentsWindow(this) { Icon = Icon };
            _agentsWindow.Closed += (_, _) => _agentsWindow = null;
#if DEBUG
            if (Headless.Active) Headless.KeepOffScreen(_agentsWindow);
#endif
            _agentsWindow.Show();
            return;
        }

        if (_agentsWindow.WindowState == WindowState.Minimized) _agentsWindow.WindowState = WindowState.Normal;
        _agentsWindow.Activate();
    }

    // Or the app would stay running for it once the main window's gone.
    private void CloseAgentsWindow()
    {
        _agentsWindow?.Close();
        _screenshotHotkey?.Dispose();
    }
}
