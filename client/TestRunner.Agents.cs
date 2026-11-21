using System.Diagnostics;
using System.IO;
using System.Text;

namespace AgentOrange;

/// <summary>What an agent task from another PC starts with on this one: where, on which branch, from whom, and what it's told.</summary>
public sealed record RemoteAgentStart(string RunId, string ProjectId, string Folder, string Branch, string From, string Title, string Brief, string PermissionMode);

/// <summary>How an agent task's conversation ended: finished or failed, why, its last reply, and the conversation.</summary>
public sealed record RemoteAgentEnd(string Verdict, string? Note, string? LastReply, string? ConversationId);

/// <summary>
/// What starts and follows an agent conversation on this PC for a job from another (#93): the app,
/// which has the conversations, the logins and the project. Everything it starts is with this PC's
/// own login and settings.
/// </summary>
public interface IRemoteAgents
{
    /// <summary>
    /// Why this PC can't take an agent task for the project from that repo, or null when it can: the
    /// project is here with a folder whose own origin is the same repo, and there's a login.
    /// </summary>
    Task<string?> ProblemAsync(string projectId, string repoUrl);

    /// <summary>Starts the conversation and follows it until it's done or stopped, each thing it does said as a line.</summary>
    Task<RemoteAgentEnd> RunAsync(RemoteAgentStart start, Action<string> line, CancellationToken stop);
}

/// <summary>
/// An agent task sent over a link of Agents access (#93), run as a job is: once every check on the
/// job has passed, in this PC's own clone of the project under the work folder, checked out at the
/// signed commit from the signed repo, on a new branch of its own. The conversation is this PC's,
/// with its own login and its own permission mode for agents from other PCs, and its every message,
/// tool and question goes into the run's log, which the PC that sent it reads. When it's done, its
/// commits are pushed to origin as that new branch, and nothing else.
/// </summary>
public sealed partial class TestRunner
{
    /// <summary>How long an agent task may take here, unless this PC's own time limit says otherwise.</summary>
    public static readonly TimeSpan AgentTaskLimit = TimeSpan.FromHours(8);

    /// <summary>What does the agent's work on this PC: set by the app, and null where there's none, as in a test run's check.</summary>
    public IRemoteAgents? Agents { get; init; }

    /// <summary>The branch an agent task works on, made new for it, which its work is pushed to.</summary>
    public static string AgentBranch(string runId) => $"ao/remote-{runId.ToLowerInvariant()[..Math.Min(8, runId.Length)]}";

    private async Task<(string Verdict, int? ExitCode, RunHere Record)> RunAgentAsync(TestJob job, RunHere record)
    {
        var folder = Path.Combine(settings.WorkFolder, FolderName(job));
        var branch = AgentBranch(run.Id);
        var title = job.Title is { Length: > 0 } named ? named : "An agent task";
        record = record with { Project = job.ProjectName, Folder = folder, Shell = $"{settings.AgentsPermissionMode} allows", Test = title, Task = job.Task, Branch = branch };
        Line($"Agent task for {job.ProjectName}, from {run.LinkerLabel}, starting at {job.Commit}");
        Line($"Repo: {job.RepoUrl}");
        Line($"Folder: {folder}, on the new branch {branch}");

        (string, int?, RunHere) CouldNot(string why)
        {
            Line(why);
            return ("couldnt_run", null, record with { Note = why });
        }

        // Only for a project this PC has too, from the same repo, and with a login here.
        if (Agents is null) return CouldNot("This copy of Agent Orange can't start agents for other PCs.");
        if (await Agents.ProblemAsync(job.ProjectId, job.RepoUrl) is { } cant) return CouldNot(cant);

        var started = DateTimeOffset.UtcNow;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var limitText = settings.TimeoutMinutes is { } own ? $"this PC's time limit gives the whole run {TestsFile.Minutes(own)}" : $"an agent task may take {AgentTaskLimit.TotalHours:0} hours here";
        limit.CancelAfter(settings.TimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : AgentTaskLimit);
        (string, int?, RunHere) TimedOut(string during)
        {
            var note = $"Timed out during {during}, {Duration(DateTimeOffset.UtcNow - started)} into the run: {limitText}.";
            Line(note);
            return ("timed_out", null, record with { Note = note });
        }

        var step = Stopwatch.StartNew();
        var checkout = await GitTools.TestCheckoutAsync(job.RepoUrl, folder, job.Commit, limit.Token, Line, settings.BorrowFor(job.ProjectId));
        Took("Checking out", step);
        if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record);
        if (limit.IsCancellationRequested) return TimedOut("checking out");
        if (!checkout.Worked) return CouldNot(checkout.Value);
        if (await GitTools.CreateBranchAsync(folder, branch) is { Worked: false } noBranch) return CouldNot(noBranch.Value);

        // Exactly what it's asked, as the PC that sent it wrote it.
        Line($"Login and settings: this PC's own. It may do what {settings.AgentsPermissionMode} allows without asking; anything it asks waits for someone at this PC.");
        Line("Task:");
        foreach (var said in job.Task!.Replace("\r\n", "\n").Split('\n')) Line($"  {said}");
        Line("");

        step.Restart();
        var start = new RemoteAgentStart(run.Id, job.ProjectId, folder, branch, run.LinkerLabel, title, AgentBrief(job, run.LinkerLabel, folder, branch), settings.AgentsPermissionMode);
        var end = await Agents.RunAsync(start, Line, limit.Token);
        Took("The agent", step);
        if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record with { Note = end.Note });
        if (limit.IsCancellationRequested) return TimedOut("the agent's work");
        if (end.Verdict == "couldnt_run") return CouldNot(end.Note ?? "The agent couldn't start.");

        // What it did reaches the other PC as a branch on origin, made new, and nothing else.
        var note = end.Note;
        var commits = await GitTools.CommitsPastAsync(folder, job.Commit, branch) ?? 0;
        if (await GitTools.NotCommittedAsync(folder) is { Count: > 0 } left)
        {
            Line($"It left changes it didn't commit, which stay in {folder} on this PC, not pushed: {string.Join(", ", left.Take(10))}{(left.Count > 10 ? ", …" : "")}");
        }
        if (commits == 0)
        {
            Line("It made no commits, so nothing was pushed.");
            record = record with { Branch = null };
        }
        else
        {
            Line($"Pushing its {commits} commit{(commits == 1 ? "" : "s")} to origin as the new branch {branch}…");
            var pushed = await GitTools.PushNewBranchAsync(folder, branch, job.RepoUrl, _stop.Token);
            if (pushed.Worked)
            {
                Line($"Pushed {branch}. Fetch it from origin to have its work: git fetch origin {branch}");
            }
            else
            {
                Line(pushed.Value);
                Line($"Its work is still on {branch} in {folder} on this PC.");
                note = $"{note} Its commits couldn't be pushed: {pushed.Value}".Trim();
                record = record with { Branch = null };
            }
        }
        if (end.LastReply is { Length: > 0 } reply)
        {
            Line("");
            Line("Its last words:");
            foreach (var said in reply.Replace("\r\n", "\n").Split('\n')) Line($"  {said}");
        }
        return (end.Verdict, null, record with { Note = note });
    }

    /// <summary>
    /// What the agent here is first sent: where it works and how, then the task in the other PC's
    /// own words, marked as theirs, as nothing in it changes the rules it's given first.
    /// </summary>
    public static string AgentBrief(TestJob job, string from, string folder, string branch)
    {
        var said = new StringBuilder();
        said.AppendLine($"This task comes from {from}, another of the owner's PCs, linked to this one in Agent Orange with Agents access. It reads this conversation as you work. Nobody may be at this PC to answer questions.");
        said.AppendLine();
        said.AppendLine($"Work only in {folder}: this PC's own clone of {job.ProjectName}, checked out at {job.Commit[..Math.Min(12, job.Commit.Length)]} on the new branch {branch}.");
        said.AppendLine($"- Commit your work on {branch} as you go, with clear messages. Don't push, merge, rebase or switch branches: once you're done, Agent Orange pushes {branch} to origin, as a new branch, for {from} to fetch.");
        said.AppendLine("- Don't change anything outside that folder, and don't read or send files from elsewhere on this PC, such as its logins or settings.");
        said.AppendLine("- If you can't go on without an answer, say what you need in a line and stop, rather than wait.");
        said.AppendLine();
        said.AppendLine($"The task, as {from} wrote it:");
        said.AppendLine("<task>");
        said.AppendLine(job.Task!.Replace("</task>", "</ task>", StringComparison.OrdinalIgnoreCase).Trim());
        said.AppendLine("</task>");
        said.AppendLine();
        said.Append("When it's done and committed, say what you did in a few lines and stop.");
        return said.ToString();
    }
}
