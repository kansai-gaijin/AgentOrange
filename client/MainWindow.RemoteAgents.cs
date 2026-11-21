using System.IO;

namespace AgentOrange;

/// <summary>
/// Agent tasks from other PCs (#93): a PC linked to this one with Agents access sends a task for a
/// project both have, and this PC starts an agent conversation for it, as one of its own, with its
/// own login and its own settings, never anything sent from the other PC but the task's words. It
/// works in this PC's test clone of the project (the runner checks it out), never the person's own
/// folder. Each thing it does is said, a line at a time, into the run's log for the other PC to
/// read; anything it asks waits for someone here. Once it's done, it's settled, and stays listed
/// here, saying where it came from.
/// </summary>
public partial class MainWindow : IRemoteAgents
{
    // A turn that never started (its program wouldn't) is called over after this long.
    private static readonly TimeSpan RemoteAgentNoStart = TimeSpan.FromSeconds(15);

    async Task<string?> IRemoteAgents.ProblemAsync(string projectId, string repoUrl) =>
        await Dispatcher.InvokeAsync(() => RemoteAgentProblemAsync(projectId, repoUrl)).Task.Unwrap();

    Task<RemoteAgentEnd> IRemoteAgents.RunAsync(RemoteAgentStart start, Action<string> line, CancellationToken stop) =>
        Dispatcher.InvokeAsync(() => FollowRemoteAgentAsync(start, line, stop)).Task.Unwrap();

    // The same project, by its id, with a folder here whose own origin is the repo the task is for.
    private async Task<string?> RemoteAgentProblemAsync(string projectId, string repoUrl)
    {
        if (FindProject(projectId) is not { } project) return "This PC doesn't have that project, so an agent here would have nothing to work on. Projects come from the server, so it may have been deleted.";
        if (ProjectFolder(projectId) is not { } folder) return $"{project.Name} has no folder on this PC, so it isn't a project both PCs have yet. Set one up on its project page here.";
        var (origin, problem) = await GitTools.OriginForTestsAsync(folder);
        if (origin is null) return $"{project.Name}'s folder on this PC can't be checked against the task's repo. {problem}";
        var same = GitTools.SameRepo(origin, repoUrl);
#if DEBUG
        same |= Path.IsPathFullyQualified(origin) && Path.IsPathFullyQualified(repoUrl) && string.Equals(Path.GetFullPath(origin), Path.GetFullPath(repoUrl), StringComparison.OrdinalIgnoreCase);
#endif
        if (!same) return $"{project.Name}'s folder on this PC is a clone of another repo than the task is for, so it isn't the same project here.";
        if (DefaultAgentProvider() is null) return "This PC has no agent login turned on. Add a Claude Code or Codex login in Settings, under Agents, here.";
        return null;
    }

    private async Task<RemoteAgentEnd> FollowRemoteAgentAsync(RemoteAgentStart start, Action<string> line, CancellationToken stop)
    {
        if (FindProject(start.ProjectId) is not { } project) return new("couldnt_run", "This PC doesn't have that project.", null, null);
        if (DefaultAgentProvider() is not { } login) return new("couldnt_run", "This PC has no agent login turned on.", null, null);
        _ = Conversations;
        var conversation = NewConversation(project);
        conversation.WorkFolder = start.Folder;
        conversation.ProviderId = login.Id;
        conversation.PermissionMode = start.PermissionMode;
        var title = $"{start.Title} · from {start.From}";
        conversation.Title = title.Length > AgentConversation.MostTitle ? title[..AgentConversation.MostTitle].TrimEnd() : title;
        conversation.TitleFrom = AgentConversation.TitleFromLink;
        conversation.FromPc = start.From;
        conversation.FromRun = start.RunId;
        conversation.ResumeAtReset = false; // Not by itself later, once the run it came as has ended.
        if (await AgentProblemAsync(conversation) is { } problem)
        {
            _conversations?.Remove(conversation); // Never sent anything, so it was never listed.
            return new("couldnt_run", problem, null, null);
        }

        var finished = false;
        void Done() => finished = true;
        conversation.Finished += Done;
        line($"Started a conversation on this PC with its login {login.Label}: \"{conversation.Title}\".");
        AppLog.Write($"Started an agent task from {start.From} (run {start.RunId}) with {login.Label}, in {start.Folder}.");
        conversation.Send(start.Brief, [], $"From {start.From}", $"An agent task from {start.From}, another of the owner's PCs, linked to this one. Nobody may be at this PC.", () => PrepareAgent(conversation), automatic: true,
            sentFor: $"Sent by Agent Orange: an agent task from {start.From}");
        var sentAt = DateTimeOffset.UtcNow;
        var said = new Dictionary<string, string>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                SayRemoteItems(conversation, said, line, all: false);
                if (conversation.IsLimited) break;
                var idle = !conversation.IsWorking && !conversation.IsWaitingOnWork && !conversation.IsWaiting;
                if (idle && (finished || DateTimeOffset.UtcNow - sentAt > RemoteAgentNoStart)) break;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            conversation.Finished -= Done;
        }

        var stopped = stop.IsCancellationRequested;
        if (stopped && (conversation.IsWorking || conversation.IsWaitingOnWork))
        {
            line("Stopping the conversation…");
            conversation.Stop();
            for (var waited = 0; waited < 15 && conversation.IsWorking; waited++) await Task.Delay(TimeSpan.FromSeconds(1));
        }
        SayRemoteItems(conversation, said, line, all: true);

        var last = conversation.Items.LastOrDefault(item => item.Kind == AgentItemKind.Assistant)?.Text;
        var (verdict, note) = stopped ? ("cancelled", (string?)null)
            : conversation.IsLimited ? ("failed", $"Its login here, {login.Label}, ran out of usage, so it stopped. The conversation is left on this PC and doesn't go on by itself.")
            : conversation.Items.LastOrDefault() is { Kind: AgentItemKind.Problem } failed ? ("failed", $"It stopped with a problem: {failed.Text}")
            : ("finished", null);
        if (conversation.Settle(true)) conversation.Save(ConversationsFolder);
        AppLog.Write($"The agent task from {start.From} (run {start.RunId}) ended: {verdict}.");
        return new(verdict, note, last, conversation.Id);
    }

    // Each thing in the conversation said once, in order, and again only when it changes: a question
    // answered, a tool that failed. Without all, it stops at a reply still being written.
    private static void SayRemoteItems(AgentConversation conversation, Dictionary<string, string> said, Action<string> line, bool all)
    {
        foreach (var item in conversation.Items.ToList())
        {
            said.TryGetValue(item.Id, out var was);
            string state;
            string? text;
            switch (item.Kind)
            {
                case AgentItemKind.User:
                    (state, text) = ("sent", item.Automatic ? null : $"Someone at this PC wrote: {item.Text}");
                    break;
                case AgentItemKind.Assistant:
                    if (item.Streaming && !all) return;
                    (state, text) = ("done", item.Text.Trim().Length > 0 ? $"Agent: {item.Text.Trim()}" : null);
                    break;
                case AgentItemKind.Tool:
                    state = item.ToolState.ToString();
                    text = was is null ? $"> {item.Tool}{(item.Text.Length > 0 ? $": {item.Text}" : "")}{(item.ToolState == AgentToolState.Failed ? " (failed)" : "")}"
                        : item.ToolState == AgentToolState.Failed ? $"  {item.Tool} failed." : null;
                    break;
                case AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.Plan:
                    state = item.RequestState.ToString();
                    text = item.RequestState == AgentRequestState.Waiting
                        ? $"Waiting for someone at this PC to answer: {item.Text}"
                        : was is null ? $"Asked: {item.Text}. {item.RequestState} at this PC." : $"{item.RequestState} at this PC.";
                    break;
                case AgentItemKind.Notice:
                    (state, text) = ("said", $"Note: {item.Text}");
                    break;
                case AgentItemKind.Problem:
                    (state, text) = ("said", $"Problem: {item.Text}");
                    break;
                default:
                    (state, text) = ("skipped", null);
                    break;
            }
            if (was == state) continue;
            said[item.Id] = state;
            if (text is not null) line(text);
        }
    }

    /// <summary>A conversation started from another PC, as the sidebar's third line says it, or null for one that wasn't.</summary>
    internal static string? FromPcLine(AgentConversation conversation) =>
        conversation.FromPc is { } from ? $"From {from} · its log streams there" : null;
}
