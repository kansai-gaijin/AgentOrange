using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// A milestone's open tasks split across agent conversations that run at once (#94), each part in a
/// Git worktree of its own so parts never step on each other's files. What a task uses (a tool, or a
/// name of the project's own) keeps tasks that share it in one part, one after another; a tool marked
/// one agent at a time (Settings › Editors, on for engines) is never used by two conversations at
/// once; the part that uses the game's engine works in the project's own folder, where the engine
/// opens the project. At most so many conversations work at once (Settings › Providers › Parallel
/// work), and a part starts only with a login that has room, as Go on with another login says (#109).
/// Each part commits on its branch; once all have finished, Agent Orange merges them in order into the
/// branch they were split from, and stops on a conflict, saying where. A person starts one from the
/// Tasks page, and a coordinating agent with split_milestone, which then waits on the parts as its
/// background work and is told when they're done.
/// </summary>
public partial class MainWindow
{
    internal const string MostAtOnceSetting = "parallel_most_at_once";
    internal const int DefaultMostAtOnce = 3;
    internal const int MostAtOnceLimit = 8;

    // A tool used one agent at a time stays with the conversation that last used it this long, or while it works.
    private static readonly TimeSpan ToolHeldFor = TimeSpan.FromMinutes(10);

    private const string UsesNote = "What it works on that another task running at the same time mustn't, separated by commas: a tool by its name, such as the game's engine or Blender, or a name of the project's own, such as save files, or a folder such as src/net. When a milestone is split across agents, tasks that share any of these go one after another in one part; tasks with nothing in common can run at once. Leave out when it's independent.";

    private List<ParallelRun>? _parallelRuns;
    private readonly Dictionary<string, (string Conversation, DateTimeOffset At)> _toolUsers = []; // Tools used one at a time, by who used each last.
    private readonly Dictionary<string, string> _partWords = []; // How each part last stood, so the page is drawn again only when that changes.
    private bool _scheduling;
    private bool _scheduleAgain;

    /// <summary>The milestones split across agents on this PC.</summary>
    internal List<ParallelRun> ParallelRunsHere => _parallelRuns ??= ParallelRuns.Load(Settings.Folders.AgentFolder);

    private void SaveParallelRuns() => ParallelRuns.Save(Settings.Folders.AgentFolder, ParallelRunsHere);

    /// <summary>How many agent conversations may work at once, as set in Settings › Providers: parts of a split wait for a place.</summary>
    internal int MostAtOnce => int.TryParse(_database.GetSetting(MostAtOnceSetting), out var most) && most is >= 1 and <= MostAtOnceLimit ? most : DefaultMostAtOnce;

    /// <summary>The split of a milestone still going (some part not merged or stopped), if it has one.</summary>
    internal ParallelRun? RunOf(string projectId, string milestoneId) => ParallelRunsHere.LastOrDefault(run => run.ProjectId == projectId && run.Milestone == milestoneId && !run.Over);

    // What a task's uses say, as the split reads them

    /// <summary>A task's uses as given to add_task or update_task: words separated by commas, or a list. Null when not given.</summary>
    private static string[]? UsesGiven(JsonObject given) => given["uses"] switch
    {
        null => null,
        JsonArray list => [.. list.Select(each => each is JsonValue value && value.TryGetValue<string>(out var word) ? word.Trim() : null).OfType<string>().Where(word => word.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
        JsonValue one when one.TryGetValue<string>(out var words) => SplitUses(words),
        _ => throw new McpException(-32602, "uses: give what it uses separated by commas, such as Unreal Engine, save files."),
    };

    internal static string[] SplitUses(string words) =>
        [.. words.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(word => word.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A tool by what a task or an agent calls it: its name or id, or "engine" or "editor" for the game's own engine.</summary>
    private static EditorDefinition? ToolUsed(Project project, string name)
    {
        if (EditorMonitor.Named(name) is { } named) return named;
        if (name.Trim().Equals("Unreal", StringComparison.OrdinalIgnoreCase)) return EditorMonitor.Named("unreal");
        return name.Trim().ToLowerInvariant() is "engine" or "editor" or "the engine" or "the editor" or "game engine" or "the game's engine"
            ? EditorMonitor.Definitions.FirstOrDefault(editor => editor.IsEngine && editor.Name == project.Engine)
            : null;
    }

    // As the split reads a use: a tool, whether one agent uses it at a time, and whether it's this game's engine.
    private (string Name, bool OneAtATime, bool GameEngine)? SplitTool(Project project, string use) =>
        ToolUsed(project, use) is { } tool ? (tool.Name, Settings.Editors.IsOneAtATime(tool), tool.Name == project.Engine) : null;

    // Splitting

    /// <summary>The milestone, and how its open tasks would be split: at most as many parts as may work at once, or as asked, if fewer.</summary>
    private (string Id, Milestone Milestone, List<ParallelPlan.Part> Parts) PlanSplit(Project project, string milestoneId, int? parts)
    {
        var stage = _database.ProjectStage(project.Id);
        var milestone = _database.ListMilestones(project.Id, stage).FirstOrDefault(each => each.Id == milestoneId);
        if (milestone.Milestone is null) throw new McpException(-32602, "milestone: there's no such milestone in the stage the project is at.");
        var open = _database.ListTasks(project.Id, stage).Where(each => each.Task.Milestone == milestoneId && !each.Task.Done).OrderBy(each => each.Task.Order).ToList();
        if (open.Count < 2)
            throw new McpException(-32602, $"milestone: {milestone.Milestone.Name} has {(open.Count == 0 ? "no tasks" : "one task")} left to do, so there's nothing to split. Work on it in one conversation.");
        var most = Math.Min(parts ?? MostAtOnce, MostAtOnceLimit);
        if (most < 2) throw new McpException(-32602, $"parts: a split needs at least 2 parts{(parts is null ? $", and Settings › Providers › Parallel work lets only {MostAtOnce} conversation work at once" : "")}.");
        var planned = ParallelPlan.Split([.. open.Select(each => new ParallelPlan.Item(each.Id, each.Task.Title, each.Task.Uses ?? []))], use => SplitTool(project, use), most);
        return (milestone.Id, milestone.Milestone, planned);
    }

    /// <summary>How a split would go, in words, for the start form and split_milestone without start.</summary>
    private string DescribePlan(Project project, Milestone milestone, List<ParallelPlan.Part> parts)
    {
        var said = new StringBuilder($"{milestone.Name} would be split into {parts.Count} parts, each a conversation of its own, at most {MostAtOnce} working at once:\n");
        foreach (var (part, at) in parts.Select((part, at) => (part, at)))
        {
            var where = part.InProjectFolder ? $"in the project's own folder, as {project.Engine} opens the project there" : "in a Git worktree of its own, on a branch of its own";
            said.AppendLine($"- Part {at + 1}, {where}{(part.Uses.Count > 0 ? $", using {string.Join(", ", part.Uses)}" : "")}{(part.OneAtATime.Count > 0 ? $" ({string.Join(", ", part.OneAtATime)} one agent at a time)" : "")}:");
            foreach (var task in part.Tasks) said.AppendLine($"  - {task.Title} (task {Short(task.Id)})");
        }
        if (parts.Count < 2) said.AppendLine("Its tasks all use something in common, so they'd go one after another in one part anyway: work on it in one conversation, or give the tasks that are independent fewer uses.");
        return said.ToString().TrimEnd();
    }

    /// <summary>
    /// Splits a milestone across agents: a worktree and branch for each part not in the project's
    /// folder, made now, then each part's conversation as it gets a place. The parent, when an agent
    /// split it, waits on the parts. Gives the split, or why it can't be.
    /// </summary>
    internal async Task<(ParallelRun? Run, string? Problem)> StartSplitAsync(Project project, string milestoneId, int? parts, AgentConversation? parent, AgentProvider? login = null)
    {
        if (RunOf(project.Id, milestoneId) is { } going) return (null, $"{going.MilestoneName} is split across agents already. parallel_status shows how its parts stand; merge_parts merges them once they're done.");
        string id;
        Milestone milestone;
        List<ParallelPlan.Part> planned;
        try
        {
            (id, milestone, planned) = PlanSplit(project, milestoneId, parts);
        }
        catch (McpException refused)
        {
            return (null, refused.Message.Replace("milestone: ", "").Replace("parts: ", ""));
        }
        if (planned.Count < 2) return (null, DescribePlan(project, milestone, planned));

        var (folder, problem) = await GitFolderAsync(project.Id);
        if (folder is null) return (null, problem);
        if (await GitTools.CheckedOutAsync(folder) is not { } checkedOut) return (null, "The project's folder isn't a Git repo with a commit yet, so there's nothing for the parts to start from.");
        if (checkedOut.Branch is not { } baseBranch) return (null, "The project's folder is on no branch (a detached HEAD). Check a branch out first, so the parts have one to merge back into.");
        if (await GitTools.NotCommittedAsync(folder, trackedOnly: true) is not { } changed) return (null, "Git couldn't say what's changed in the project's folder. Try again in a moment.");
        if (changed.Count > 0) return (null, $"The project's folder has changes not committed yet ({string.Join(", ", changed.Take(5))}{(changed.Count > 5 ? ", …" : "")}). The parts start from its last commit and wouldn't have them, so commit them first.");

        login ??= parent is not null ? AgentProviderFor(parent) : DefaultAgentProvider();
        if (login is null) return (null, "Add a Claude Code or Codex login in Settings, under Agents, to work with agents.");

        var run = new ParallelRun
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            ProjectId = project.Id,
            Stage = _database.ProjectStage(project.Id),
            Milestone = id,
            MilestoneName = milestone.Name,
            BaseBranch = baseBranch,
            BaseCommit = checkedOut.Commit,
            Parent = parent?.Id,
            ProviderId = login.Id,
            Model = parent?.Model,
            Effort = parent?.Effort,
            PermissionMode = parent?.PermissionMode ?? "auto",
        };
        var slug = ParallelPlan.Slug(milestone.Name);
        var worktrees = Path.Combine(Settings.Folders.AgentFolder, "worktrees", Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
        foreach (var (plan, at) in planned.Select((plan, at) => (plan, at)))
        {
            var part = new ParallelPart { Number = at + 1, Tasks = [.. plan.Tasks.Select(task => task.Id)], Uses = plan.Uses, OneAtATime = plan.OneAtATime };
            if (plan.InProjectFolder)
            {
                part.Branch = baseBranch;
            }
            else
            {
                part.Branch = $"ao/{slug}-{run.Id}-{part.Number}";
                part.Worktree = Path.Combine(worktrees, $"{slug}-{run.Id}-{part.Number}");
                if (await GitTools.AddWorktreeAsync(folder, part.Worktree, part.Branch, run.BaseCommit) is { } refused)
                {
                    // None of it is left half made.
                    foreach (var made in run.Parts.Where(each => each.Worktree is not null)) await GitTools.RemoveWorktreeAsync(folder, made.Worktree!, made.Branch!);
                    return (null, refused);
                }
            }
            run.Parts.Add(part);
        }
        ParallelRunsHere.Add(run);
        SaveParallelRuns();
        AppLog.Write($"Split {milestone.Name} across {run.Parts.Count} agents ({run.Id}), from {baseBranch} at {run.BaseCommit[..Math.Min(8, run.BaseCommit.Length)]}.");
        await ScheduleParallelAsync();
        return (run, null);
    }

    // How parts stand

    /// <summary>How a part stands now, in a few words, and whether it's done with (finished, merged or stopped).</summary>
    internal (string Words, bool Over, bool Busy) PartState(ParallelRun run, ParallelPart part)
    {
        var tasks = _database.ListTasks(run.ProjectId, run.Stage).Where(each => part.Tasks.Contains(each.Id)).ToList();
        var done = $"{tasks.Count(each => each.Task.Done)} of {part.Tasks.Count} {(part.Tasks.Count == 1 ? "task" : "tasks")} done";
        switch (part.State)
        {
            case ParallelPart.Merged:
                return ($"Merged, {done}", true, false);
            case ParallelPart.Stopped:
                return ($"Stopped{(part.Note is { Length: > 0 } why ? $": {why}" : "")}", true, false);
            case ParallelPart.Waiting:
                return (part.Note ?? "Waiting to start", false, false);
        }
        if (PartConversation(part) is not { } conversation) return ($"Its conversation is gone, {done}", true, false);
        if (conversation.IsWaiting) return ($"Needs you, {done}", false, true);
        if (conversation.IsLimited) return ($"Stopped by its login's usage limit, {done}", false, true);
        if (conversation.IsWorking || conversation.IsWaitingOnWork) return ($"Working, {done}", false, true);
        return (tasks.All(each => each.Task.Done) ? $"Finished, {done}" : $"Stopped working, {done}", true, false);
    }

    /// <summary>A part's conversation's line in the sidebar: which part, its branch and how it stands, or null for a conversation that isn't one.</summary>
    internal string? PartLine(AgentConversation conversation)
    {
        if (conversation.PartOf is not { } runId || ParallelRunsHere.FirstOrDefault(run => run.Id == runId) is not { } run) return null;
        if (run.Parts.FirstOrDefault(part => part.Number == conversation.PartNumber) is not { } part) return null;
        return $"Part {part.Number} of {run.Parts.Count} · {PartState(run, part).Words} · {(part.InProjectFolder ? run.BaseBranch : part.Branch)}";
    }

    private AgentConversation? PartConversation(ParallelPart part) => part.Conversation is { } id ? Conversations.FirstOrDefault(each => each.Id == id) : null;

    // Working, or wanting the person: what counts against how many may work at once.
    private static bool IsAtWork(AgentConversation conversation) => conversation.IsWorking || conversation.IsWaiting;

    /// <summary>Every part done with, and at least one finished rather than stopped: what can be merged.</summary>
    internal bool ReadyToMerge(ParallelRun run) => !run.Over && run.Parts.All(part => PartState(run, part).Over) && run.Parts.Any(part => part.State == ParallelPart.Started);

    /// <summary>
    /// Something about conversations changed: parts waiting start if they now can, the conversations
    /// waiting on parts are told how they stand, and the Tasks page is drawn again if a part changed.
    /// </summary>
    private void ParallelChanged()
    {
        if (_parallelRuns is null && !File.Exists(Path.Combine(Settings.Folders.AgentFolder, "parallel-runs.json"))) return;
        _ = ScheduleParallelAsync();
    }

    private async Task ScheduleParallelAsync()
    {
        if (_scheduling)
        {
            _scheduleAgain = true;
            return;
        }
        _scheduling = true;
        try
        {
            do
            {
                _scheduleAgain = false;
                await StartWaitingPartsAsync();
                ShowParts();
            }
            while (_scheduleAgain);
        }
        finally
        {
            _scheduling = false;
        }
    }

    private async Task StartWaitingPartsAsync()
    {
        foreach (var run in ParallelRunsHere.Where(run => !run.Over).ToList())
        {
            if (FindProject(run.ProjectId) is not { } project) continue;
            foreach (var part in run.Parts.Where(part => part.State == ParallelPart.Waiting).ToList())
            {
                var note = await WhyPartWaitsAsync(project, run, part);
                if (note is not null)
                {
                    if (part.Note != note)
                    {
                        part.Note = note;
                        SaveParallelRuns();
                    }
                    continue;
                }
                await StartPartAsync(project, run, part);
            }
        }
    }

    // Why a part can't start yet, or null when it can: a place, its tools, the project's folder, a login with room.
    private Task<string?> WhyPartWaitsAsync(Project project, ParallelRun run, ParallelPart part)
    {
        var atWork = Conversations.Count(IsAtWork);
        if (atWork >= MostAtOnce) return Task.FromResult<string?>($"Waits its turn: {atWork} conversations are working, and Settings › Providers › Parallel work allows {MostAtOnce} at once.");
        foreach (var name in part.OneAtATime)
        {
            if (ToolUsed(project, name) is { } tool && ToolHolder(tool, null) is { } holder)
                return Task.FromResult<string?>($"Waits for {tool.Name}, which \"{TitleOf(holder)}\" is using, as it's used one agent at a time.");
        }
        if (part.InProjectFolder && ParallelRunsHere.Any(other => other != run && other.ProjectId == run.ProjectId && !other.Over && other.Parts.Any(each => each.InProjectFolder && each.State == ParallelPart.Started && !PartState(other, each).Over)))
            return Task.FromResult<string?>("Waits for the project's own folder, where a part of another split milestone works.");
        var (login, why) = LoginForPart(run);
        return Task.FromResult(login is null ? why : null);
    }

    // The split's login, or another of its kind with room when it has none and Go on with another login is on (#109).
    private (AgentProvider? Login, string? Why) LoginForPart(ParallelRun run)
    {
        var logins = Settings.Providers.List().Where(provider => provider.Enabled).ToList();
        var login = logins.FirstOrDefault(each => each.Id == run.ProviderId) ?? DefaultAgentProvider();
        if (login is null) return (null, "Waits for a login: add a Claude Code or Codex login in Settings, under Agents.");

        // Limits not known yet don't hold it back, as for a conversation the person starts.
        if (Usage.For(login.Id).Count == 0 || HasRoom(login)) return (login, null);
        if (SwitchesLoginAtLimit && MostRoom(logins.Where(each => each.Id != login.Id && each.Kind == login.Kind)) is { } other) return (other, null);
        return (null, $"Waits for {login.Label}'s usage limit to reset{(SwitchesLoginAtLimit ? $", as no other {AgentProviders.KindName(login.Kind)} login has room" : "")}.");
    }

    private async Task StartPartAsync(Project project, ParallelRun run, ParallelPart part)
    {
        var (login, _) = LoginForPart(run);
        if (login is null) return;
        _ = Conversations;
        var conversation = NewConversation(project);
        conversation.PartOf = run.Id;
        conversation.PartNumber = part.Number;
        conversation.WorkFolder = part.Worktree;
        conversation.ProviderId = login.Id;
        conversation.Model = login.Id == run.ProviderId ? run.Model : null;
        conversation.Effort = login.Id == run.ProviderId ? run.Effort : null;
        conversation.PermissionMode = run.PermissionMode;
        conversation.Title = $"{run.MilestoneName} · part {part.Number} of {run.Parts.Count}";
        if (await AgentProblemAsync(conversation) is { } problem)
        {
            _conversations?.Remove(conversation); // Never sent anything, so it was never listed.
            part.Note = $"Can't start yet: {problem}";
            SaveParallelRuns();
            return;
        }
        part.State = ParallelPart.Started;
        part.Conversation = conversation.Id;
        part.Note = null;
        SaveParallelRuns();
        foreach (var name in part.OneAtATime)
        {
            if (ToolUsed(project, name) is { } tool) _toolUsers[tool.Id] = (conversation.Id, DateTimeOffset.UtcNow);
        }
        AppLog.Write($"Started part {part.Number} of {run.MilestoneName} ({run.Id}) with {login.Label}, in {conversation.Folder}.");
        conversation.Send(PartBrief(project, run, part), [], ViewLabel(), DescribeView(), () => PrepareAgent(conversation), automatic: true,
            sentFor: $"Sent by Agent Orange: part {part.Number} of {run.MilestoneName}, split across agents");
    }

    /// <summary>What a part's conversation is first sent: its tasks, where it works, and the rules of working beside the others.</summary>
    internal string PartBrief(Project project, ParallelRun run, ParallelPart part)
    {
        var tasks = _database.ListTasks(run.ProjectId, run.Stage).ToDictionary(each => each.Id, each => each.Task);
        string Line(string id) => tasks.TryGetValue(id, out var task) ? $"{task.Title} (task {Short(id)}){(task.Notes is { Length: > 0 } notes ? $": {notes.Split('\n')[0].Trim()}" : "")}" : $"task {Short(id)}";
        var said = new StringBuilder();
        said.AppendLine($"Agent Orange split the milestone \"{run.MilestoneName}\" across {run.Parts.Count} agent conversations working at the same time, and this one is part {part.Number}. Work only on these tasks, in this order:");
        foreach (var (id, at) in part.Tasks.Select((id, at) => (id, at))) said.AppendLine($"{at + 1}. {Line(id)}");
        said.AppendLine();
        said.AppendLine(part.InProjectFolder
            ? $"Work in the project's own folder, {ProjectFolder(project.Id)}, on its branch {run.BaseBranch}, as {project.Engine} opens the project there. The other parts work in Git worktrees of their own."
            : $"Work in {part.Worktree}: a Git worktree of the project of its own, on the branch {part.Branch}, made from {run.BaseBranch} at {run.BaseCommit[..Math.Min(8, run.BaseCommit.Length)]}. Nothing you change there touches the other parts until they're merged.");
        said.AppendLine($"- Commit your work on {(part.InProjectFolder ? run.BaseBranch : "that branch")} as you go, with clear messages. Don't push, merge, rebase or switch branches: Agent Orange merges the parts in order once every part is done.");
        said.AppendLine("- Tick each task with update_task as you finish it. If one can't be finished, write a handoff note on it with write_handoff saying why, and go on with the next.");
        var othersOnly = run.Parts.Where(other => other != part).SelectMany(other => other.OneAtATime).Except(part.OneAtATime, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (part.OneAtATime.Count > 0) said.AppendLine($"- Only this part uses {string.Join(" and ", part.OneAtATime)}, which only one agent uses at a time.");
        if (othersOnly.Count > 0) said.AppendLine($"- Don't open {string.Join(" or ", othersOnly)}: another part uses {(othersOnly.Count == 1 ? "it" : "them")}, one agent at a time.");
        foreach (var other in run.Parts.Where(other => other != part))
        {
            said.AppendLine($"- Part {other.Number} does {string.Join("; ", other.Tasks.Select(id => tasks.TryGetValue(id, out var task) ? task.Title : Short(id)))}{(other.Uses.Count > 0 ? $", and works on {string.Join(", ", other.Uses)}" : "")}. Leave those to it.");
        }
        said.AppendLine("- You may use subagents for independent pieces inside a task, but never a second copy of a tool that's used one agent at a time.");
        said.AppendLine();
        said.Append("When every task is done and committed, say so in a line and stop.");
        return said.ToString();
    }

    // The conversations waiting on parts are told how they stand, and the Tasks page is drawn again when a part changed.
    private void ShowParts()
    {
        var changedProjects = new HashSet<string>();
        foreach (var run in ParallelRunsHere.Where(run => !run.Over))
        {
            foreach (var part in run.Parts)
            {
                var words = PartState(run, part).Words;
                var key = $"{run.Id}-{part.Number}";
                if (_partWords.TryGetValue(key, out var was) && was == words) continue;
                _partWords[key] = words;
                changedProjects.Add(run.ProjectId);
            }
        }

        foreach (var parent in Conversations.Where(each => ParallelRunsHere.Any(run => run.Parent == each.Id)).ToList())
        {
            var works = new List<BackgroundWork>();
            foreach (var run in ParallelRunsHere.Where(run => run.Parent == parent.Id && !run.Over))
            {
                foreach (var part in run.Parts)
                {
                    var (words, over, _) = PartState(run, part);
                    works.Add(new BackgroundWork
                    {
                        Id = $"{run.Id}-{part.Number}",
                        Kind = BackgroundKind.Part,
                        Title = $"{run.MilestoneName} · part {part.Number} of {run.Parts.Count}",
                        Activity = words,
                        ActivityAt = DateTimeOffset.UtcNow,
                        Awaited = true,
                        Started = run.Started,
                        Ended = over ? DateTimeOffset.UtcNow : null,
                    });
                }

                // All done with: the agent that split it is told, once, to go on from there.
                if (!run.ParentTold && run.Parts.All(part => PartState(run, part).Over))
                {
                    run.ParentTold = true;
                    SaveParallelRuns();
                    _ = TellParentAsync(parent, run);
                }
            }
            parent.SetParts(works);
        }

        if (_openProject is { } open && changedProjects.Contains(open.Id) && _openSection is { Name: TasksPage } section && section.ProjectId == open.Id) RenderProject(open);
    }

    private async Task TellParentAsync(AgentConversation parent, ParallelRun run)
    {
        if (parent.Settled || await AgentProblemAsync(parent) is not null) return;
        parent.Send(PartsReport(run, done: true), [], ViewLabel(), DescribeView(), () => PrepareAgent(parent), automatic: true,
            sentFor: $"Sent by Agent Orange: the parts of {run.MilestoneName} it waited on have stopped");
    }

    /// <summary>How a split's parts stand, for the agent that split it and for parallel_status.</summary>
    internal string PartsReport(ParallelRun run, bool done = false)
    {
        var said = new StringBuilder(done
            ? $"The parts of {run.MilestoneName} have all stopped working.{(ReadyToMerge(run) ? " Merge them with merge_parts, or first ask a part for more with message_part." : "")}\n"
            : $"{run.MilestoneName}, split across {run.Parts.Count} agents from {run.BaseBranch}:\n");
        foreach (var part in run.Parts)
        {
            var conversation = PartConversation(part);
            var where = part.InProjectFolder ? $"in the project's folder, on {run.BaseBranch}" : $"branch {part.Branch}, in {part.Worktree}";
            said.AppendLine($"- Part {part.Number}: {PartState(run, part).Words}. {where}.");
            if (conversation?.Items.LastOrDefault(item => item.Kind == AgentItemKind.Assistant) is { } last) said.AppendLine($"  Its last words: {Shortened(last.Text.ReplaceLineEndings(" "), 300)}");
        }
        if (run.MergeNote is { Length: > 0 } note) said.AppendLine($"Last merge: {note}");
        return said.ToString().TrimEnd();
    }

    // Stop, from the conversation waiting on its parts: those working are asked to stop, and those not started never are.
    private void StopParts(AgentConversation parent)
    {
        foreach (var run in ParallelRunsHere.Where(run => run.Parent == parent.Id && !run.Over)) StopSplit(run);
    }

    /// <summary>Stops a split: parts not started never are, and parts at work are asked to stop. What they committed stays on their branches.</summary>
    internal void StopSplit(ParallelRun run)
    {
        foreach (var part in run.Parts)
        {
            if (part.State == ParallelPart.Waiting)
            {
                part.State = ParallelPart.Stopped;
                part.Note = "it never started";
            }
            else if (part.State == ParallelPart.Started && PartConversation(part) is { IsWorking: true } working)
            {
                working.Stop();
            }
        }
        SaveParallelRuns();
        _ = ScheduleParallelAsync();
    }

    // Tools used one agent at a time

    // Who has a tool used one at a time, other than the one asking: a part at work that uses it, or the
    // conversation that last used it while it still works or for a while after.
    private AgentConversation? ToolHolder(EditorDefinition tool, AgentConversation? asking)
    {
        foreach (var run in ParallelRunsHere.Where(run => !run.Over))
        {
            foreach (var part in run.Parts.Where(part => part.State == ParallelPart.Started && part.OneAtATime.Contains(tool.Name, StringComparer.OrdinalIgnoreCase)))
            {
                if (PartConversation(part) is { } holder && holder != asking && !PartState(run, part).Over) return holder;
            }
        }
        if (_toolUsers.TryGetValue(tool.Id, out var last) && last.Conversation != asking?.Id && Conversations.FirstOrDefault(each => each.Id == last.Conversation) is { Settled: false } user
            && (IsAtWork(user) || user.IsWaitingOnWork || DateTimeOffset.UtcNow - last.At < ToolHeldFor))
        {
            return user;
        }
        return null;
    }

    /// <summary>
    /// Before an agent's call to a tool used one agent at a time: refused while another conversation has
    /// it, and otherwise noted as this conversation's. A session that didn't say which conversation it's
    /// for isn't held back, as nothing can be told about it.
    /// </summary>
    private void TakeToolForCall(Project project, string toolName)
    {
        if (ToolUsed(project, toolName) is not { } tool || !Settings.Editors.IsOneAtATime(tool) || CallingConversation is not { } caller) return;
        if (ToolHolder(tool, caller) is { } holder)
        {
            throw new McpException(-32602, $"tool: {tool.Name} is used by one agent at a time (Settings › Editors), and the conversation \"{TitleOf(holder)}\" is using it now. Work on something that doesn't need it, or end your turn saying you wait for it; it's free once that conversation is done with it.");
        }
        _toolUsers[tool.Id] = (caller.Id, DateTimeOffset.UtcNow);
    }

    private static string TitleOf(AgentConversation conversation) => conversation.Title.Length > 0 ? conversation.Title : "a conversation";

    // Merging

    /// <summary>
    /// Merges the finished parts' branches, in order, into the branch they were split from, in the
    /// project's folder: a fast-forward when nothing else changed, a merge commit otherwise. Stops at
    /// the first that conflicts or has changes it didn't commit, saying so, with the folder as it was
    /// before that part. Each merged part's worktree and branch are taken away and its conversation
    /// settled. Gives what it did.
    /// </summary>
    internal async Task<string> MergeSplitAsync(ParallelRun run)
    {
        if (run.Over) return $"{run.MilestoneName}'s parts are all merged or stopped already.";
        foreach (var part in run.Parts)
        {
            var (words, over, _) = PartState(run, part);
            if (!over) return $"Part {part.Number} isn't done yet ({words}). Merge once every part has stopped working.";
        }
        var (folder, problem) = await GitFolderAsync(run.ProjectId);
        if (folder is null) return problem!;
        if (await GitTools.CheckedOutAsync(folder) is not { } checkedOut) return "Git couldn't read the project's folder. Try again in a moment.";
        if (checkedOut.Branch != run.BaseBranch) return Noted(run, $"The project's folder is on {checkedOut.Branch ?? "no branch"} now, not {run.BaseBranch}, which the parts were split from. Check {run.BaseBranch} out there, then merge again.");
        if (await GitTools.NotCommittedAsync(folder, trackedOnly: true) is not { } changed) return "Git couldn't say what's changed in the project's folder. Try again in a moment.";
        if (changed.Count > 0) return Noted(run, $"The project's folder has changes not committed ({string.Join(", ", changed.Take(5))}{(changed.Count > 5 ? ", …" : "")}). Commit them or put them away, then merge again.");

        var merged = new List<int>();
        string Before() => merged.Count == 0 ? "" : $" Part{(merged.Count == 1 ? "" : "s")} {string.Join(", ", merged)} {(merged.Count == 1 ? "was" : "were")} merged first.";
        foreach (var part in run.Parts.Where(part => part.State == ParallelPart.Started).OrderBy(part => part.Number))
        {
            if (part.InProjectFolder)
            {
                // Its commits are on the branch already.
                FinishPart(part);
                merged.Add(part.Number);
                continue;
            }
            if (await GitTools.NotCommittedAsync(part.Worktree!) is not { } left) return Noted(run, $"Git couldn't read part {part.Number}'s worktree at {part.Worktree}.{Before()}");
            if (left.Count > 0)
            {
                SaveParallelRuns();
                return Noted(run, $"Part {part.Number} left changes it didn't commit, in {part.Worktree}: {string.Join(", ", left.Take(5))}{(left.Count > 5 ? ", …" : "")}. Ask it to commit them on its branch (in its conversation, or with message_part), then merge again.{Before()}");
            }
            if (await GitTools.CommitsPastAsync(folder, run.BaseCommit, part.Branch!) is 0)
            {
                await GitTools.RemoveWorktreeAsync(folder, part.Worktree!, part.Branch!);
                FinishPart(part);
                continue;
            }
            var result = await GitTools.MergeBranchAsync(folder, part.Branch!, $"Merge part {part.Number} of {run.MilestoneName} ({part.Branch})");
            if (result.Conflicts.Count > 0)
            {
                return Noted(run, $"Part {part.Number}'s branch {part.Branch} conflicts with what's on {run.BaseBranch} now, in {string.Join(", ", result.Conflicts.Take(8))}{(result.Conflicts.Count > 8 ? ", …" : "")}. Nothing of part {part.Number} was merged, and the project's folder is as it was before it.{Before()} "
                    + $"To go on, have part {part.Number} merge {run.BaseBranch} into its branch and settle the conflicts there (in its conversation, or with message_part), then merge again.");
            }
            if (!result.Merged) return Noted(run, $"Couldn't merge part {part.Number}'s branch {part.Branch}: {result.Problem}{Before()}");
            merged.Add(part.Number);
            if (await GitTools.RemoveWorktreeAsync(folder, part.Worktree!, part.Branch!) is { } notRemoved) AppLog.Write(notRemoved);
            FinishPart(part);
        }
        var said = merged.Count == 0 ? "None of the parts had anything to merge." : $"Merged part{(merged.Count == 1 ? "" : "s")} {string.Join(", ", merged)} of {run.MilestoneName} into {run.BaseBranch}, in order. Their worktrees and branches are taken away, and their conversations settled.";
        return Noted(run, said);

        void FinishPart(ParallelPart part)
        {
            part.State = ParallelPart.Merged;
            part.Note = null;
            SaveParallelRuns();
            if (PartConversation(part) is { } conversation && conversation.Settle(true)) conversation.Save(ConversationsFolder);
        }
    }

    private string Noted(ParallelRun run, string said)
    {
        run.MergeNote = said;
        SaveParallelRuns();
        AppLog.Write($"Merging {run.MilestoneName} ({run.Id}): {said}");
        _ = ScheduleParallelAsync();
        if (FindProject(run.ProjectId) is { } project && _openProject?.Id == project.Id) RenderProject(project);
        return said;
    }
}
