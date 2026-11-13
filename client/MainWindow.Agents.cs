using System.IO;
using System.Text.Json.Nodes;
using System.Windows;

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

    private string ConversationsFolder => Path.Combine(Settings.Folders.AgentFolder, "conversations");

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

    private void Keep(AgentConversation conversation)
    {
        _conversations!.Add(conversation);
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
        return new AgentConversation.Setup(provider, mcp, servers, AgentSkills.ClaudePlugin(agentFolder), AgentBriefing.Write(Settings.Editors, agentFolder));
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
            _agentsWindow.Show();
            return;
        }

        if (_agentsWindow.WindowState == WindowState.Minimized) _agentsWindow.WindowState = WindowState.Normal;
        _agentsWindow.Activate();
    }

    // Or the app would stay running for it once the main window's gone.
    private void CloseAgentsWindow() => _agentsWindow?.Close();
}
