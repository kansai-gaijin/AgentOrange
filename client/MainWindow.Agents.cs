using System.IO;
using System.Text.Json;
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
                    if (AgentConversation.Load(file) is not { } loaded) continue;
                    Keep(loaded);
                    PickUp(loaded);
                }
            }
            return _conversations;
        }
    }

    /// <summary>Where each conversation's agent program runs, a folder for each, which outlives the app (SessionHost).</summary>
    internal string SessionsFolder(AgentConversation conversation) => Path.Combine(Settings.Folders.AgentFolder, "sessions", conversation.Id);

    // As it's read, the first time since the app started: its agent program, if it went on while the app
    // was away, is taken back, and a turn a restart cut off is told to carry on (#101).
    private void PickUp(AgentConversation conversation)
    {
        switch (conversation.PickUp(FolderOf(conversation)))
        {
            case PickedUp.PickedWorking:
                conversation.Note("Agent Orange restarted while it worked. It went on meanwhile, and is picked back up from where it was.");
                break;
            case PickedUp.CutOff:
                Dispatcher.BeginInvoke(() => GoOnAfterCutOffAsync(conversation));
                break;
        }
    }

    /// <summary>
    /// Its turn was cut off as Agent Orange restarted, with nothing left to pick back up: it's said so,
    /// and the agent is told, so it carries on by itself rather than the person writing out where it was.
    /// </summary>
    private async Task GoOnAfterCutOffAsync(AgentConversation conversation)
    {
        conversation.Note("Its turn was cut off as Agent Orange restarted.");
        if (conversation.IsLimited || conversation.Settled || conversation.IsWorking) return;
        if (await AgentProblemAsync(conversation) is { } problem)
        {
            conversation.Note($"It carries on once it can be sent a message again. {problem}");
            return;
        }
        conversation.Send(AgentConversation.CutOffMessage, [], ViewLabel(), DescribeView(), () => PrepareAgent(conversation), automatic: true);
    }

    /// <summary>Saves the conversations whose programs are running, so after a restart each is read on from as late as can be.</summary>
    internal void SaveRunningConversations()
    {
        foreach (var conversation in _conversations ?? [])
        {
            if (conversation.Live is not null) conversation.Save(ConversationsFolder);
        }
    }

    /// <summary>Each login's subscription limits, as last said.</summary>
    internal AgentUsage Usage { get; } = new();

    private DispatcherTimer? _agentClock;

    private void Keep(AgentConversation conversation)
    {
        _conversations!.Add(conversation);
        conversation.LimitsReported += (provider, limits, complete) => Usage.Update(provider, limits, complete);
        conversation.ExtraUsageStarted += limit => ExtraUsageStarted(conversation, limit);
        conversation.SkillUsed += (skill, how) => RecordSkillUse(conversation, skill, how);
        // Done, with nothing it started still running, or wanting something, with no one looking at it.
        // A turn that ends while its background work goes on isn't done, so that says nothing yet.
        conversation.Finished += () =>
        {
            if (_agentsWindow?.IsLookingAt(conversation) != true) conversation.Unseen = true;
        };
        conversation.StateChanged += () => ConversationChanged(conversation);
        conversation.TurnCutOff += () => _ = GoOnAfterCutOffAsync(conversation);
        conversation.PartsStopAsked += () => StopParts(conversation);
    }

    // It started or stopped working, or wants the person.
    private void ConversationChanged(AgentConversation conversation)
    {
        // A usage limit stopped it before it could say where it left off, so Agent Orange does, from what it has.
        if (conversation.IsLimited && !conversation.LimitHandoffWritten)
        {
            conversation.LimitHandoffWritten = true;
            WriteLimitHandoff(conversation);

            // Turned on in Settings › Providers: another login with room takes it on now.
            if (SwitchesLoginAtLimit && conversation.ResumeAtReset && !conversation.Settled) _ = GoOnWithAnotherLoginAsync(conversation);
        }
        // Asked for its handoff note as it was settled, and done answering.
        if (conversation.SettlesAfterHandoff && !conversation.IsWorking && !conversation.IsWaiting)
        {
            conversation.SettlesAfterHandoff = false;
            conversation.Settle(true);
            return; // Settling says so again.
        }
        // Wanting something, with no one looking at it. Being done says so through Finished, once
        // nothing it started is still running.
        if (conversation.IsWaiting && _agentsWindow?.IsLookingAt(conversation) != true) conversation.Unseen = true;

        // Its first reply says what it's about, so it's named from that rather than its first words (#125).
        WriteTitleIfDue(conversation);
        conversation.Save(ConversationsFolder);
        _agentsWindow?.ShowConversations();

        // A part of a split milestone, or one waiting on parts, or one that frees a tool or a place for the next part.
        ParallelChanged();
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
            if (Directory.Exists(SessionsFolder(conversation))) Directory.Delete(SessionsFolder(conversation), recursive: true);

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

    // Its own login, or another of the same kind if that one's gone, or for a new one, the first turned on.
    internal AgentProvider? AgentProviderFor(AgentConversation conversation)
    {
        var all = Settings.Providers.List();
        var providers = all.Where(provider => provider.Enabled).ToList();
        if (providers.FirstOrDefault(provider => provider.Id == conversation.ProviderId) is { } own) return own;
        var kind = all.FirstOrDefault(provider => provider.Id == conversation.ProviderId)?.Kind;
        return kind is { } same ? providers.FirstOrDefault(provider => provider.Kind == same) : FirstLogin(providers);
    }

    /// <summary>The login a new conversation would start with, for writing done for a project with no conversation, such as its commit messages.</summary>
    internal AgentProvider? DefaultAgentProvider() => FirstLogin(Settings.Providers.List().Where(provider => provider.Enabled));

    private static AgentProvider? FirstLogin(IEnumerable<AgentProvider> turnedOn) => turnedOn.OrderBy(provider => provider.Kind).FirstOrDefault();

    /// <summary>Why the conversation can't be sent anything right now, or null when it can.</summary>
    internal async Task<string?> AgentProblemAsync(AgentConversation conversation)
    {
        if (AgentProviderFor(conversation) is null) return "Add a Claude Code or Codex login in Settings, under Agents, to work with an agent.";
        if (_database.GetProjectFolder(conversation.ProjectId) is not { } folder || !Directory.Exists(folder)) return "This project has no folder on this PC yet. Set one up on its project page.";
        if (IsBusyWith(conversation.ProjectId)) return "This project is busy. Send this once that finishes.";

        // An agent runs Git in the folder as it works, which runs whatever the folder's own settings say to.
        if (await AgentsGitProblemAsync(folder) is { } problem) return problem;

        // A part of a split milestone works in its own worktree of the project, which shares its Git settings.
        if (conversation.WorkFolder is { } own && !Directory.Exists(own)) return $"Its worktree at {own} is gone, so it has nowhere to work. Its branch is still in the project's repo.";
        conversation.Folder = conversation.WorkFolder ?? folder;
        return null;
    }

    /// <summary>Where a conversation works: its own worktree, for a part of a split milestone, or else the project's folder.</summary>
    internal string? FolderOf(AgentConversation conversation) => conversation.WorkFolder is { } own && Directory.Exists(own) ? own : ProjectFolder(conversation.ProjectId);

    /// <summary>The project's folder on this PC, if it has one.</summary>
    internal string? ProjectFolder(string projectId) =>
        _database.GetProjectFolder(projectId) is { } folder && Directory.Exists(folder) ? folder : null;

    /// <summary>The project's folder, once its own Git settings are allowed, as Git runs whatever they say to. Otherwise why not.</summary>
    internal async Task<(string? Folder, string? Problem)> GitFolderAsync(string projectId)
    {
        if (ProjectFolder(projectId) is not { } folder) return (null, "This project has no folder on this PC yet. Set one up on its project page.");
        if (await AgentsGitProblemAsync(folder) is { } problem) return (null, problem);
        return (folder, null);
    }

    // Agents, and the app's own Git for them, use everything the folder's settings and hooks say
    // to, not only how Git reaches the repo (#20). Null when they're allowed.
    private static async Task<string?> AgentsGitProblemAsync(string folder)
    {
        var look = await GitTools.LookBeforeAgentsAsync(folder);
        if (look is null) return "This folder's own Git settings couldn't be read, so nothing runs Git in it yet. Try again in a moment.";
        if (look.Refused is { } refused) return refused;
        return GitTools.AreAllowed(GitTools.AgentsApprovalKey(folder), look.Settings) ? null : "This folder's own Git settings and hooks need allowing first, on the project's page.";
    }

    internal AgentConversation.Setup PrepareAgent(AgentConversation conversation)
    {
        var agentFolder = Settings.Folders.AgentFolder;
        var provider = AgentProviderFor(conversation) ?? throw new IOException("There's no Claude Code provider.");
        var mcp = AgentMcpConfig.Write(Settings.Editors, agentFolder, conversation.ProjectId);
        var servers = (JsonNode.Parse(File.ReadAllText(mcp))?["mcpServers"] as JsonObject)?.Select(server => server.Key).ToList() ?? [];
        Directory.CreateDirectory(AttachmentsFolder);

        var (others, references) = SessionSkills(conversation.ProjectId, conversation.Folder ?? ProjectFolder(conversation.ProjectId));
        _sessionSkills[conversation.Id] = [.. AgentSkills.List(agentFolder), .. others];
        return new AgentConversation.Setup(provider, mcp, servers, AgentSkills.ClaudePlugin(agentFolder, conversation.ProjectId, others),
            AgentBriefing.Write(Settings.Editors, agentFolder, conversation.ProjectId, others, references, ProjectBriefingNotes(conversation.ProjectId)), AttachmentsFolder,
            WriteClaudeSettings(mcp, _sessionSkills[conversation.Id], references, agentFolder, WriteHelperBriefing(agentFolder, conversation.ProjectId)),
            [.. _sessionSkills[conversation.Id].Select(skill => Path.GetFullPath(skill.Folder)), .. references.Select(reference => reference.Folder)],
            SessionsFolder(conversation), AppCopy.Program(agentFolder), Ripgrep.Folder(agentFolder));
    }

    // The skills a project's sessions get besides Agent Orange's own: those the creative tools set up
    // here ship with them, for a game made in Unreal the skills its engine ships and its own, and one
    // for each tool's scripting API reference, so names get looked up rather than guessed.
    private (List<AgentSkill> Others, List<ToolReference> References) SessionSkills(string projectId, string? folder)
    {
        var engine = FindProject(projectId)?.Engine;
        List<AgentSkill> others = [.. ToolSkills.For(Settings.Editors)];
        if (engine == "Unreal Engine")
        {
            others.AddRange(UnrealSkills.ForFolder(folder).Where(skill => !others.Any(had => had.Name == skill.Name)));
        }
        var references = ToolSkills.References(Settings.Editors, folder, engine);
        others.AddRange(ToolSkills.ReferenceSkills(references, Settings.Folders.AgentFolder, projectId).Where(skill => !others.Any(had => had.Name == skill.Name)));
        return (others, references);
    }

    // The briefing points the agent at each skill's SKILL.md, wherever it is (Agent Orange's folder, the
    // engine's, a tool's), and at each tool's API reference, so reading those folders never needs asking.
    // Claude Code matches Windows paths in their POSIX form, "//c/Program Files/...". Every shell
    // command, the agent's and its helpers', goes past Agent Orange's check for patch scripts first
    // (PatchScripts), and each helper agent is given the project's rules and lessons as it starts
    // (ProjectLessons).
    private static string WriteClaudeSettings(string mcpConfig, IEnumerable<AgentSkill> skills, IEnumerable<ToolReference> references, string agentFolder, string helperBriefing)
    {
        var rules = new JsonArray();
        var folders = skills.Select(skill => Path.GetDirectoryName(Path.GetFullPath(skill.Folder))).OfType<string>().Concat(references.Select(reference => reference.Folder));
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var posix = folder.Replace(Path.DirectorySeparatorChar, '/');
            if (posix.Length > 1 && posix[1] == ':') posix = $"//{char.ToLowerInvariant(posix[0])}{posix[2..]}";
            rules.Add($"Read({posix.TrimEnd('/')}/**)");
        }
        var path = Path.ChangeExtension(mcpConfig, null) + "-settings.json";
        var program = AgentMcpConfig.RelayProgram(agentFolder);
        var hooks = PatchScripts.Hooks(program);
        hooks["SubagentStart"] = ProjectLessons.HelperHooks(program, helperBriefing);
        var settings = new JsonObject
        {
            ["permissions"] = new JsonObject { ["allow"] = rules },
            ["hooks"] = hooks,

            // Claude Code lists skills in 1% of a model's context and leaves out descriptions past that, so
            // with 70 skills some were only names (#130). This is the most the list may take; it takes what it needs.
            ["skillListingBudgetFraction"] = 0.1,
        };
        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // The skills each conversation's session was given, by conversation, for saying where a skill it used came from.
    private readonly Dictionary<string, List<AgentSkill>> _sessionSkills = [];

    // Kept on this PC, for Settings › Skills to say which skills get used, where and how.
    private void RecordSkillUse(AgentConversation conversation, string skill, string how)
    {
        var name = skill.StartsWith(AgentSkills.PluginPrefix, StringComparison.Ordinal) ? skill[AgentSkills.PluginPrefix.Length..] : skill;
        var provider = AgentProviderFor(conversation);
        var given = _sessionSkills.TryGetValue(conversation.Id, out var skills) ? skills.FirstOrDefault(each => each.Name == name) : null;
        var from = given?.From ?? (provider?.Kind == ProviderKind.Codex ? "Codex" : "Claude Code");
        try
        {
            _database.AddSkillUse(new SkillUse(DateTimeOffset.Now, name, from, conversation.ProjectId, conversation.Id, provider?.Kind.ToString() ?? "", how));
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            AppLog.Write($"Couldn't note that {name} was used: {error.Message}");
        }
    }

    /// <summary>The logins a conversation can go on with: every Claude Code one turned on.</summary>
    /// <summary>Every login turned on, of either kind, for showing their usage.</summary>
    internal List<AgentProvider> AllAgentLogins() => [.. Settings.Providers.List().Where(provider => provider.Enabled)];

    /// <summary>
    /// The logins a conversation can go on with: any turned on before it's begun, and after that only
    /// those of its own kind, as one kind's conversation can't be carried to the other.
    /// </summary>
    internal List<AgentProvider> AgentLogins(AgentConversation? conversation = null)
    {
        var enabled = Settings.Providers.List().Where(provider => provider.Enabled).ToList();
        if (conversation is null || !conversation.Items.Any(item => item.Kind == AgentItemKind.User)) return enabled;
        var kind = AgentProviderFor(conversation)?.Kind;
        return [.. enabled.Where(provider => provider.Kind == kind)];
    }

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
                // Another login has come to have room, such as one whose limit has reset since.
                if (SwitchesLoginAtLimit && LoginToGoOnWith(conversation) is not null)
                {
                    await GoOnWithAnotherLoginAsync(conversation);
                    continue;
                }

                // Long overdue, it's been left on purpose, so it isn't woken.
                if (ResumesAt(conversation) is not { } resets || resets > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - resets > TimeSpan.FromHours(24)) continue;
                await ResumeAsync(conversation);
            }

            // Parts of split milestones waiting for a place, a tool or a login with room.
            ParallelChanged();

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
        foreach (var login in AllAgentLogins())
        {
            // One running already says it as it goes.
            if (_conversations?.Any(each => each.IsWorking && each.ProviderId == login.Id) == true) continue;
            if (await Usage.ProbeAsync(login, folder)) continue;

            // It didn't say. Only its own login status says it's signed out, as a login that's run
            // out of its limit can also say nothing.
            var status = await Settings.Providers.CheckAsync(login);
            Usage.SetNotSignedIn(login.Id, status.ProgramFound && !status.SignedIn && status.Detail != "Couldn't read the login status" ? status.Detail : null);
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
            if (section.Name == ReviewPage && OpenReviewOf(project) is { } review) parts.Add(review.Name);
        }
        return string.Join(" › ", parts);
    }

    /// <summary>Where pictures sent to agents are kept, on this PC only.</summary>
    internal string AgentFolder => Settings.Folders.AgentFolder;

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
