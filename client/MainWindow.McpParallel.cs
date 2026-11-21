using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// The tools a coordinating agent splits a milestone across agents with (#94): split_milestone (how it
/// would split, then with start the parts begin, and its conversation waits on them), parallel_status,
/// message_part to steer one, and merge_parts once they've all stopped.
/// </summary>
public partial class MainWindow
{
    private IEnumerable<McpTool> ParallelTools() =>
    [
        new("split_milestone",
            "Splits a milestone's open tasks across agent conversations that work at the same time, each part in a Git worktree of its own on a branch of its own, so they don't step on each other. "
            + "Tasks that share anything they use (add_task or update_task uses: a tool, or a name of the project's own) go one after another in one part; the part that uses the game's engine works in the project's own folder, as the engine opens the project there. "
            + $"Without start, it only says how it would split them, so give tasks their uses first. With start, the parts begin (at most {MostAtOnce} conversations work at once, and each part's conversation starts with your login), this conversation waits on them as background work and is told once they've all stopped; then merge_parts merges their branches in order.",
            new JsonObject
            {
                ["milestone"] = Text("The milestone, by its name or the start of it."),
                ["parts"] = Number($"The most parts to split it into, 2 to {MostAtOnceLimit}. As many as may work at once when not given."),
                ["start"] = Flag("Start the parts. Without it, only says how it would split them."),
            },
            ["milestone"], (_, _) => "", SplitMilestoneAsync),
        new("parallel_status", "How the parts of a split milestone stand: each one's state, how many of its tasks are done, its branch and folder, and its last words.",
            new JsonObject { ["milestone"] = Text("The milestone, by its name or the start of it. Every split milestone when not given.") },
            [], (project, given) => ParallelStatus(project, given)),
        new("message_part", "Sends a part of a split milestone a message in its conversation, to steer it: an answer, a correction, or more to do, such as committing what it left or merging the base branch into its branch to settle a conflict.",
            new JsonObject { ["milestone"] = Text("The milestone, by its name or the start of it."), ["part"] = Number("The part's number, from parallel_status."), ["message"] = Text("What to tell it.") },
            ["milestone", "part", "message"], (_, _) => "", MessagePartAsync),
        new("merge_parts", "Merges the branches of a split milestone's parts, in order, into the branch they were split from, once every part has stopped working: a fast-forward when nothing else changed, a merge commit otherwise. It stops at a part that conflicts or left changes it didn't commit, saying which files, with the project's folder as it was before that part. Merged parts' worktrees and branches are taken away.",
            new JsonObject { ["milestone"] = Text("The milestone, by its name or the start of it.") },
            ["milestone"], (_, _) => "", MergePartsAsync),
    ];

    private string SplitMilestoneId(Project project, JsonObject given) =>
        MilestoneId(project, Required(given, "milestone")) is { Length: > 0 } id ? id : throw new McpException(-32602, "milestone: give the milestone to split, by its name.");

    private async Task<object> SplitMilestoneAsync(Project project, JsonObject given)
    {
        var id = SplitMilestoneId(project, given);
        var parts = WholeNumber(given, "parts");
        if (parts is < 2 or > MostAtOnceLimit) throw new McpException(-32602, $"parts must be from 2 to {MostAtOnceLimit}; it was {parts}.");
        if (!Yes(given, "start"))
        {
            var (_, milestone, planned) = PlanSplit(project, id, parts);
            return DescribePlan(project, milestone, planned) + (planned.Count > 1 ? "\nCall split_milestone again with start true to start it." : "");
        }
        var (run, problem) = await StartSplitAsync(project, id, parts, CallingConversation);
        if (run is null) throw new McpException(-32602, problem ?? "It couldn't be split.");
        return $"Started.\n{PartsReport(run)}\n"
            + (run.Parent is null
                ? "parallel_status shows how they stand."
                : "This conversation waits on the parts as background work, and is told once they've all stopped: end your turn now rather than waiting in it. parallel_status shows how they stand meanwhile.");
    }

    private string ParallelStatus(Project project, JsonObject given)
    {
        var only = Given(given, "milestone") is { Length: > 0 } asked ? MilestoneId(project, asked) : null;
        var runs = ParallelRunsHere.Where(run => run.ProjectId == project.Id && (only is null ? !run.Over : run.Milestone == only)).ToList();
        if (only is not null && runs.Count > 1) runs = [runs[^1]];
        if (runs.Count == 0) return only is null ? "No milestone of this project is split across agents now." : "That milestone isn't split across agents. split_milestone splits it.";
        var said = new StringBuilder();
        foreach (var run in runs) said.AppendLine(PartsReport(run)).AppendLine();
        return said.ToString().TrimEnd();
    }

    private (ParallelRun Run, ParallelPart Part) PartAsked(Project project, JsonObject given)
    {
        var id = SplitMilestoneId(project, given);
        var run = RunOf(project.Id, id) ?? throw new McpException(-32602, "milestone: it isn't split across agents now. split_milestone splits it.");
        var number = WholeNumber(given, "part") ?? throw new McpMissingArgument("part");
        var part = run.Parts.FirstOrDefault(each => each.Number == number) ?? throw new McpException(-32602, $"part: {run.MilestoneName} has parts 1 to {run.Parts.Count}; it was {number}.");
        return (run, part);
    }

    private async Task<object> MessagePartAsync(Project project, JsonObject given)
    {
        var (run, part) = PartAsked(project, given);
        var message = Required(given, "message");
        if (PartConversation(part) is not { } conversation)
            throw new McpException(-32602, $"part: part {part.Number} hasn't started yet ({PartState(run, part).Words}), so there's no conversation to tell.");
        if (await AgentProblemAsync(conversation) is { } problem) throw new McpException(-32602, $"part: part {part.Number} can't be sent anything right now: {problem}");
        var from = CallingConversation is { } caller ? $"\"{TitleOf(caller)}\", which split this milestone" : "the conversation coordinating this milestone";
        conversation.Send($"From {from}:\n\n{message}", [], ViewLabel(), DescribeView(), () => PrepareAgent(conversation), automatic: true,
            sentFor: "Sent by the conversation that split this milestone");
        return $"Sent to part {part.Number}{(conversation.IsWorking ? ", which takes it up after what it's doing now" : "")}.";
    }

    private async Task<object> MergePartsAsync(Project project, JsonObject given)
    {
        var id = SplitMilestoneId(project, given);
        var run = RunOf(project.Id, id) ?? throw new McpException(-32602, "milestone: it isn't split across agents now, or its parts are all merged already.");
        return await MergeSplitAsync(run);
    }
}
