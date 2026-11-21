using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// Test runs for agents (#117): AO's tools to start a run on a linked test machine for a pushed
/// commit, list the project's runs with their state, and read a run's verdict, checks and log,
/// rather than the person starting each run and typing its result back to the agent. A run goes
/// exactly as "Run tests on…" sends one, over this PC's links with the same checks here and on the
/// machine, and its signed job says an agent started it, which the run list shows. A machine
/// already testing that commit, or with it waiting, isn't sent it again. With none named, a machine
/// is picked by the tags the repo asks for (#93), and a run can be asked to test only what a filter
/// names, through the repo's own testWithFilter. A run can be cancelled as its window's Cancel run
/// does. And the checks that flip between pass and fail at the same commit, or differ between
/// machines (#105), with each one's history, so an agent looks for the cause before adding a retry.
/// </summary>
public partial class MainWindow
{
    private const int LongestRunWait = 300;
    private const int DefaultLogLines = 200;
    private const int MostLogLines = 2000;

    private List<McpTool> TestRunTools() =>
    [
        new("list_test_runs", "The test machines this PC is linked to, with whether each can take a run now and the tags it offers; the project's recent test runs, newest first, each with its id, machine, commit, state, who started it and how its checks went; and the checks that flip between pass and fail at the same commit or differ between machines.",
            new JsonObject { ["commit"] = Text("Only runs of this commit, by its id or the start of it.") }, [], (_, _) => "", ListTestRunsForAgentAsync),
        new("run_tests", "Runs the project's tests on a linked test machine, for a commit pushed to origin: the one checked out in the project's folder, or the one given. The machine runs what .agent-orange/tests.json says at that commit. With no machine named, one is picked that offers every tag tests.json asks for, preferring one already testing that commit, then one free now. If that machine is already testing the commit, or has it waiting, that run is given back instead of sending another. Then read_test_run with seconds waits for its verdict.",
            new JsonObject
            {
                ["machine"] = Text("The test machine, by its name as list_test_runs gives it. Left out, one is picked by the tags .agent-orange/tests.json asks for."),
                ["commit"] = Text("A commit id, the start of one, or a branch or tag, pushed to origin. The commit checked out in the project's folder if not given."),
                ["filter"] = Text($"Test only what this names, such as one check: it's put in tests.json's testWithFilter at that commit, where {TestsFile.FilterPlaceholder} is. One word of {TestsFile.FilterCharacters}. All the tests if not given."),
            },
            [], (_, _) => "", RunTestsForAgentAsync),
        new("cancel_test_run", "Cancels a test run that's waiting or running, as Cancel run in its window does: one waiting never starts, and the machine stops one running and ends it Cancelled. Any run of this project this PC sent or ran, by whoever started it.",
            new JsonObject { ["run"] = Text("The run's id, from run_tests or list_test_runs.") }, ["run"], (_, _) => "", CancelTestRunForAgentAsync),
        new("read_test_run", "A test run's state and verdict, where and at which commit it ran, who started it, how each check went (when the repo's tests.json names a results file) with any that flip, and the end of its log. With seconds, it first waits up to that long for the run to finish, instead of sleep. An agent task started with start_agent_on reads the same way: its log is its conversation there, ending with the branch its work was pushed to.",
            new JsonObject
            {
                ["run"] = Text("The run's id, from run_tests or list_test_runs."),
                ["seconds"] = Number($"The longest to wait for it to finish, {LongestRunWait} at most. It answers at once if not given."),
                ["lines"] = Number($"How many lines from the end of its log, {DefaultLogLines} if not given, {MostLogLines} at most."),
            },
            ["run"], (_, _) => "", ReadTestRunForAgentAsync),
        new("check_history", "One check's history across the project's test runs, newest first: passed, failed or skipped, on which machine, at which commit and in which run, and whether it flips between pass and fail at the same commit or differs between machines. Look here before adding a retry or an exception to a check.",
            new JsonObject { ["check"] = Text("The check's name, as read_test_run gives it.") }, ["check"], (_, _) => "", CheckHistoryForAgentAsync),
    ];

    // The links this PC sends runs over, as the Run tests on… list has them.
    private List<MachineLinkInfo> LinksThisPcSendsOver() =>
        _testMachines.Latest is { } latest ? [.. latest.Links.Where(link => link.Linker == latest.Me).OrderBy(link => link.MachineLabel, StringComparer.CurrentCultureIgnoreCase)] : [];

    private static string MachineState(MachineLinkInfo link) => link.Status switch
    {
        "reachable" => "can take a run now",
        "busy" => "running tests, so a run waits its turn",
        "expired" => "past its link's expiry, so it needs linking again in Settings › Test machines",
        "unreachable" => "not answering right now, so it can't take a run until Agent Orange runs there",
        "other_release" => "on another release of Agent Orange, so it can't take a run until it updates",
        var other => other,
    };

    private static string ShortCommit(string commit) => commit.Length > 12 ? commit[..12] : commit;

    private static string When(double at) => $"{DateTimeOffset.FromUnixTimeSeconds((long)at).ToLocalTime():d MMM HH:mm}";

    // A run in a line, as agents are told it.
    private static string RunLine(TestRunInfo run)
    {
        var said = new StringBuilder($"{(run.IsAgent ? "agent task" : "run")} {run.Id}: {TestRunner.VerdictLabel(run.Status)}");
        if (run.Status == "running" && run.CancelRequested) said.Append(", cancelling");
        if (run.Finished && run.ExitCode is { } code) said.Append($" (exit code {code})");
        if (run.IsAgent && TestJob.Read(run.Job)?.Title is { } title) said.Append($", \"{title}\"");
        said.Append($" on {run.MachineLabel} {(run.IsAgent ? "from" : "at")} {ShortCommit(run.Commit)}{(run.Filter is { } filter ? $", testing only {filter}" : "")}{(run.IsAgent ? $", on the branch {TestRunner.AgentBranch(run.Id)}" : "")}, sent {When(run.CreatedAt)}, started by {(run.ByAgent ? "an agent" : "a person")}");
        if (run.Checks > 0) said.Append($". Checks: {TestChecks.Summary(run.ChecksPassed, run.ChecksFailed, run.ChecksSkipped)}");
        said.Append('.');
        if (run.HeldBackText is { } heldBack) said.Append($" {heldBack}");
        return said.ToString();
    }

    private async Task<object> ListTestRunsForAgentAsync(Project project, JsonObject given)
    {
        var commit = Given(given, "commit") is { Length: > 0 } asked ? asked.ToLowerInvariant() : null;
        var said = new StringBuilder();
        var links = LinksThisPcSendsOver();
        if (links.Count == 0) said.AppendLine(_testMachines.Problem ?? "This PC isn't linked to any test machines. The person links one in Settings › Test machines.");
        else
        {
            said.AppendLine("Test machines this PC can run tests on:");
            foreach (var link in links) said.AppendLine($"- {link.MachineLabel}: {MachineState(link)}.{(link.MachineTags.Count > 0 ? $" Tags: {string.Join(", ", link.MachineTags)}." : "")}");
        }

        var runs = await TestRunsAsync(project.Id) ?? throw new McpException(-32603, TestMachines.DescribeProblem(null) + " The project's test runs couldn't be listed.");
        var shown = runs.Where(run => commit is null || run.Commit.StartsWith(commit, StringComparison.OrdinalIgnoreCase)).ToList();
        said.AppendLine();
        if (shown.Count == 0) said.AppendLine(commit is null ? $"{project.Name} has no test runs yet." : $"{project.Name} has no test runs of {commit}.");
        else
        {
            said.AppendLine($"Recent test runs of {project.Name}, newest first:");
            foreach (var run in shown) said.AppendLine($"- {RunLine(run)}");
        }

        if (await CheckHistoryAsync(project.Id) is { Count: > 0 } history && TestChecks.Flags(history) is { Count: > 0 } flags)
        {
            said.AppendLine();
            said.AppendLine("Checks that flip, from the runs recorded:");
            foreach (var flag in flags.Values.OrderBy(each => each.Name, StringComparer.Ordinal)) said.AppendLine($"- {flag.Name} {flag.Why}");
            said.AppendLine(TestChecks.FlagAdvice + " check_history gives a check's history.");
        }
        return said.ToString().TrimEnd();
    }

    private async Task<object> RunTestsForAgentAsync(Project project, JsonObject given)
    {
        var links = LinksThisPcSendsOver();
        if (links.Count == 0) throw new McpException(-32602, _testMachines.Problem ?? "This PC isn't linked to any test machines, so there's nowhere to run the tests. Ask the person to link one in Settings › Test machines.");
        var names = string.Join(", ", links.Select(each => each.MachineLabel));
        var filter = Given(given, "filter") is { Length: > 0 } filterAsked ? filterAsked : null;
        if (filter is not null && TestsFile.FilterProblem(filter) is { } unsafeFilter) throw new McpException(-32602, $"filter: {unsafeFilter}");

        // A machine named is looked at first; otherwise one is picked once the commit's tags are known.
        MachineLinkInfo? link = null;
        if (Given(given, "machine") is { Length: > 0 } asked)
        {
            link = links.FirstOrDefault(each => each.MachineLabel.Equals(asked, StringComparison.OrdinalIgnoreCase))
                ?? links.FirstOrDefault(each => each.MachineLabel.StartsWith(asked, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException(-32602, $"machine: this PC isn't linked to a test machine called {asked}. Its test machines: {names}.");

            // The same as the Run tests on… list allows.
            if (link.Status is not ("reachable" or "busy")) throw new McpException(-32602, $"machine: {link.MachineLabel} is {MachineState(link)}.");
        }
        var (folder, folderProblem) = await GitFolderAsync(project.Id);
        if (folder is null) throw new McpException(-32602, folderProblem!);
        var named = Given(given, "commit") is { Length: > 0 } commitAsked ? commitAsked : null;
        var (_, commit, setup, problem) = await TestMachines.WhatToSendAsync(folder, named);
        if (problem is not null || commit is null || setup is null) throw new McpException(-32602, $"{(named is null ? "" : "commit: ")}{problem ?? "That commit can't be sent."}");
        if (TestMachines.FilterProblem(setup, filter) is { } noFilter) throw new McpException(-32602, $"filter: {noFilter}");
        var runs = await TestRunsAsync(project.Id) ?? [];

        var picked = "";
        if (link is null) (link, picked) = PickTestMachine(links, setup, runs, project.Id, commit, filter);
        else if (setup.Tags.Where(tag => !TestMachines.HasTags(link.MachineTags, [tag])).ToList() is { Count: > 0 } missing)
            throw new McpException(-32602, $"machine: {link.MachineLabel} doesn't offer {string.Join(", ", missing)}, which {TestsFile.RelativePath} asks for.{(links.Count > 1 ? $" Its test machines: {names}." : "")}");

        // A run of this commit already there is the one to wait for, not a second.
        var only = filter is null ? "" : $", testing only {filter}";
        if (_testMachines.SameRun(runs, link.Id, project.Id, commit, filter) is { } same)
            return $"{picked}{link.MachineLabel} is already {(same.Status == "running" ? "testing" : "waiting to test")} {ShortCommit(commit)}{only}, so no other run was sent: {RunLine(same)} read_test_run with seconds waits for its verdict.";

        var (runId, failed, existing) = await _testMachines.RunTestsAsync(link, project, folder, commit, TestJob.ByAgent, filter);
        if (existing is not null) return $"{picked}{link.MachineLabel} is already testing {ShortCommit(commit)}{only}, or has it waiting, so no other run was sent: {RunLine(existing)}";
        if (runId is null) throw new McpException(-32603, $"{picked}{failed ?? "The tests couldn't be sent."}");
        var what = filter is null ? $"what {TestsFile.RelativePath} says at that commit" : $"testWithFilter from {TestsFile.RelativePath} at that commit, with {filter} for {TestsFile.FilterPlaceholder}";
        return $"{picked}Sent {ShortCommit(commit)} to {link.MachineLabel} as run {runId}. It {(link.Status == "busy" ? "waits its turn, as the machine is running tests" : "starts within a few seconds")}, running {what} ({setup.LimitsText}). read_test_run with run {runId} and seconds waits for its verdict.";
    }

    // With no machine named: one offering every tag the repo asks for at that commit, one already
    // testing it first, then one free now, then one running tests, by name. With more than one
    // linked, which one and why, said before the rest.
    private (MachineLinkInfo Link, string Said) PickTestMachine(List<MachineLinkInfo> links, TestSetup setup, List<TestRunInfo> runs, string projectId, string commit, string? filter)
    {
        var tags = string.Join(", ", setup.Tags);
        var offering = links.Where(link => TestMachines.HasTags(link.MachineTags, setup.Tags)).ToList();
        if (offering.Count == 0)
        {
            var offers = string.Join(" ", links.Select(link => $"{link.MachineLabel} offers {(link.MachineTags.Count > 0 ? string.Join(", ", link.MachineTags) : "no tags")}."));
            throw new McpException(-32602, $"No test machine this PC is linked to offers {tags}, which {TestsFile.RelativePath} asks for. {offers} A machine's tags are set in its own Settings › Test machines.");
        }
        var usable = offering.Where(link => link.Status is "reachable" or "busy").ToList();
        if (usable.Count == 0)
        {
            var fit = setup.Tags.Length > 0 ? $" offering {tags}, which {TestsFile.RelativePath} asks for," : "";
            throw new McpException(-32602, $"No test machine{fit} can take a run now: {string.Join(" ", offering.Select(link => $"{link.MachineLabel} is {MachineState(link)}."))}");
        }
        var link = usable
            .OrderBy(each => _testMachines.SameRun(runs, each.Id, projectId, commit, filter) is null ? 1 : 0)
            .ThenBy(each => each.Status == "reachable" ? 0 : 1)
            .ThenBy(each => each.MachineLabel, StringComparer.CurrentCultureIgnoreCase)
            .First();
        if (links.Count == 1) return (link, "");
        var why = setup.Tags.Length > 0 ? $"it offers {tags}, which {TestsFile.RelativePath} asks for, and " : "";
        var state = _testMachines.SameRun(runs, link.Id, projectId, commit, filter) is not null ? "it's already testing this commit"
            : link.Status == "reachable" ? "it can take a run now"
            : "no machine that fits is free now";
        return (link, $"Picked {link.MachineLabel}: {why}{state}. ");
    }

    private async Task<object> CancelTestRunForAgentAsync(Project project, JsonObject given)
    {
        var asked = Required(given, "run").ToLowerInvariant();
        var id = await RunIdForAgentAsync(project, asked);
        var (run, _) = await ReadTestRunAsync(id, int.MaxValue);
        if (run is null) throw new McpException(-32602, $"run: there's no run {asked} this PC sent or ran, or the server couldn't say. list_test_runs gives each run's id.");
        if (run.ProjectId != project.Id) throw new McpException(-32602, $"run: {asked} is a run of another project.");
        if (run.Finished) throw new McpException(-32602, $"run: {run.Id} has already ended: {TestRunner.VerdictLabel(run.Status)}. Only a run waiting or running can be cancelled.");
        if (run.CancelRequested) return $"{run.MachineLabel} was already asked to stop run {run.Id}, and ends it Cancelled once it has. read_test_run with seconds waits for that.";

        var cancelled = await CancelTestRunAsync(run.Id);
        if (cancelled.Status == System.Net.HttpStatusCode.Conflict)
        {
            var (now, _) = await ReadTestRunAsync(id, int.MaxValue);
            throw new McpException(-32602, $"run: {run.Id} ended before it could be cancelled{(now is { Finished: true } ? $": {TestRunner.VerdictLabel(now.Status)}" : "")}.");
        }
        if (!cancelled.Worked) throw new McpException(-32603, TestMachines.DescribeProblem(cancelled.Status) ?? "The server couldn't cancel the run.");
        return run.Status == "queued"
            ? $"Cancelled run {run.Id}: it was waiting on {run.MachineLabel}, so it never starts."
            : $"Asked {run.MachineLabel} to stop run {run.Id}. It ends it Cancelled within a few seconds, ending what the tests started. read_test_run with seconds waits for that.";
    }

    /// <summary>Cancels a run, as its window's Cancel run does: on the server, or a made-up one in headless checks.</summary>
    internal async Task<ServerAnswer<bool>> CancelTestRunAsync(string runId)
    {
#if DEBUG
        if (_testRunsForChecks is { } madeUp && madeUp.FindIndex(run => run.Id == runId) is >= 0 and var at)
        {
            if (madeUp[at].Finished) return new(false, System.Net.HttpStatusCode.Conflict);
            madeUp[at] = madeUp[at].Status == "queued" ? madeUp[at] with { Status = "cancelled", CancelRequested = true } : madeUp[at] with { CancelRequested = true };
            return new(true, System.Net.HttpStatusCode.NoContent);
        }
#endif
        return await _devices.CancelRunAsync(runId);
    }

    // A run's id, or the start of one the list has.
    private async Task<string> RunIdForAgentAsync(Project project, string asked)
    {
        if (MachineLinks.IsId(asked)) return asked;
        var recent = await TestRunsAsync(project.Id) ?? [];
        var like = recent.Where(run => run.Id == asked).ToList() is { Count: 1 } exact ? exact : [.. recent.Where(run => asked.Length >= 6 && run.Id.StartsWith(asked, StringComparison.OrdinalIgnoreCase))];
        return like.Count == 1 ? like[0].Id : throw new McpException(-32602, $"run: there's no run {asked}{(like.Count > 1 ? " alone: more than one run starts that way" : "")}. list_test_runs gives each run's id.");
    }

    private async Task<object> ReadTestRunForAgentAsync(Project project, JsonObject given)
    {
        var asked = Required(given, "run").ToLowerInvariant();
        var seconds = given["seconds"] is JsonValue waitValue ? waitValue.GetValue<int>() : 0;
        if (seconds < 0 || seconds > LongestRunWait) throw new McpException(-32602, $"seconds must be from 0 to {LongestRunWait}; it was {seconds}. A longer wait is another call.");
        var lines = given["lines"] is JsonValue linesValue ? linesValue.GetValue<int>() : DefaultLogLines;
        if (lines < 0 || lines > MostLogLines) throw new McpException(-32602, $"lines must be from 0 to {MostLogLines}; it was {lines}.");

        var id = await RunIdForAgentAsync(project, asked);

        // Waited for first, with no log, then read whole.
        var watch = Stopwatch.StartNew();
        var (run, _) = await ReadTestRunAsync(id, int.MaxValue);
        while (run is { Finished: false } or { AwaitingReport: true } && watch.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(2, Math.Max(0.1, seconds - watch.Elapsed.TotalSeconds))));
            (run, _) = await ReadTestRunAsync(id, int.MaxValue);
        }
        if (run is null) throw new McpException(-32602, $"run: there's no run {asked} this PC sent or ran, or the server couldn't say. list_test_runs gives each run's id.");
        if (run.ProjectId != project.Id) throw new McpException(-32602, $"run: {asked} is a run of another project.");

        var said = new StringBuilder(char.ToUpperInvariant(RunLine(run)[0]) + RunLine(run)[1..]);
        said.AppendLine();
        if (!run.Finished && seconds > 0) said.AppendLine($"Still {(run.Status == "running" ? "running" : "waiting")} after {seconds} s. Call read_test_run with seconds again to keep waiting.");
        else if (!run.Finished) said.AppendLine("It hasn't finished yet. read_test_run with seconds waits for it.");

        if (run.Checks > 0 && await RunChecksAsync(run.Id) is { } checks)
        {
            var flags = TestChecks.Flags(await CheckHistoryAsync(project.Id) ?? []);
            said.AppendLine();
            said.AppendLine($"Checks: {TestChecks.Summary(run.ChecksPassed, run.ChecksFailed, run.ChecksSkipped)}.");
            var failed = checks.Where(check => check.Outcome == TestChecks.Failed).ToList();
            foreach (var check in failed.Take(50)) said.AppendLine($"- Failed: {check.Name}{(flags.TryGetValue(check.Name, out var flag) ? $". Flagged: it {flag.Why}" : "")}");
            if (failed.Count > 50) said.AppendLine($"- and {failed.Count - 50} more failed.");
            foreach (var check in checks.Where(check => check.Outcome != TestChecks.Failed && flags.ContainsKey(check.Name)).Take(20)) said.AppendLine($"- {TestChecks.Label(check.Outcome)} this time, but flagged: {check.Name} {flags[check.Name].Why}");
            if (checks.Any(check => flags.ContainsKey(check.Name))) said.AppendLine(TestChecks.FlagAdvice);
        }

        if (lines > 0)
        {
            var log = await TestRunLogAsync(run.Id);
            var all = log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            said.AppendLine();
            if (log.Length == 0) said.AppendLine("Its log is empty so far.");
            else
            {
                said.AppendLine(all.Length > lines ? $"The last {lines} of its log's {all.Length} lines:" : "Its log:");
                foreach (var line in all.Skip(Math.Max(0, all.Length - lines))) said.AppendLine(line);
            }
        }
        return said.ToString().TrimEnd();
    }

    private async Task<object> CheckHistoryForAgentAsync(Project project, JsonObject given)
    {
        var name = Required(given, "check");
        var rows = await CheckHistoryAsync(project.Id, name) ?? throw new McpException(-32603, "The server couldn't give the check's history. " + (TestMachines.DescribeProblem(null) ?? ""));
        if (rows.Count == 0)
        {
            var flagged = TestChecks.Flags(await CheckHistoryAsync(project.Id) ?? []);
            var like = flagged.Keys.Where(each => each.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
            throw new McpException(-32602, $"check: no run of {project.Name} has a check called {name}.{(like.Count > 0 ? $" Checks like it that flip: {string.Join(", ", like)}." : "")} read_test_run gives the checks of a run, when its tests.json names a results file.");
        }
        var said = new StringBuilder();
        var flag = TestChecks.Flags(rows).GetValueOrDefault(name);
        said.AppendLine(flag is null ? $"{name} hasn't passed and failed at the same commit in the runs recorded." : $"{name} {flag.Why}");
        if (flag is not null) said.AppendLine(TestChecks.FlagAdvice);
        said.AppendLine();
        said.AppendLine($"Its history, newest first ({rows.Count(row => row.Outcome == TestChecks.Passed)} passed, {rows.Count(row => row.Outcome == TestChecks.Failed)} failed):");
        foreach (var line in TestChecks.HistoryLines(rows)) said.AppendLine($"- {line}");
        return said.ToString().TrimEnd();
    }

    // What the server has, or made up for headless checks, as with TestRunsAsync.

    private async Task<(TestRunInfo? Run, List<RunLogLine> Log)> ReadTestRunAsync(string runId, int after)
    {
#if DEBUG
        if (_testRunsForChecks is { } madeUp)
        {
            var found = madeUp.FirstOrDefault(run => run.Id == runId);
            return (found, found is null || after != -1 ? [] : [new RunLogLine(0, _testLogForChecks)]);
        }
#endif
        var read = await _devices.ReadRunAsync(runId, after);
        return (read.Value?.Run, read.Value?.Log ?? []);
    }

    // A run's whole log, a page at a time, as much as the server kept.
    private async Task<string> TestRunLogAsync(string runId)
    {
        var log = new StringBuilder();
        var after = -1;
        for (var pages = 0; pages < 100; pages++)
        {
            var (_, part) = await ReadTestRunAsync(runId, after);
            if (part.Count == 0) break;
            foreach (var line in part) log.Append(line.Text);
            after = part[^1].Seq;
        }
        return log.ToString();
    }

    internal async Task<List<CheckResult>?> RunChecksAsync(string runId)
    {
#if DEBUG
        if (_checksForChecks is { } madeUp) return madeUp.TryGetValue(runId, out var checks) ? checks : [];
#endif
        return (await _devices.RunChecksAsync(runId)).Value;
    }

    internal async Task<List<CheckHistoryRow>?> CheckHistoryAsync(string projectId, string? name = null)
    {
#if DEBUG
        if (_checkHistoryForChecks is { } madeUp) return [.. madeUp.Where(row => name is null ? madeUp.Any(each => each.Name == row.Name && each.Outcome == TestChecks.Failed) : row.Name == name)];
#endif
        return (await _devices.CheckHistoryAsync(projectId, name)).Value;
    }

#if DEBUG
    // Headless checks only: a made-up run's log and checks, and its project's check history.
    private string _testLogForChecks = "";
    private Dictionary<string, List<CheckResult>>? _checksForChecks;
    private List<CheckHistoryRow>? _checkHistoryForChecks;

    /// <summary>
    /// Headless checks only: as if this PC were linked to Office PC and Laptop, and the project had
    /// runs at its folder's commit with checks: Clock.DayNight passed and failed on Office PC there,
    /// Input.Jump passed on Laptop and failed on Office PC there, and Physics.Fall always failed.
    /// The latest run, by an agent, is check-run, in that state.
    /// </summary>
    internal async Task SetUpTestChecksForChecks(string projectName, string status)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var commit = ProjectFolder(project.Id) is { } folder && (await GitTools.PushedHeadAsync(folder)).Sha is { Length: > 0 } sha ? sha : "0123456789abcdef0123456789abcdef01234567";
        var office = new MachineLinkInfo("check-link", "office", "Office PC", ["windows"], "me", "This PC", "tests", 0, DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds(), "busy", 0);
        var laptop = new MachineLinkInfo("check-link-2", "laptop", "Laptop", [], "me", "This PC", "tests", 0, DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds(), "unreachable", 0);
        _testMachines.ShowForChecks(new MachinesInfo("me", null, [], [office, laptop]));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var agentJob = $"{{\"kind\":\"tests\",\"runId\":\"check-run\",\"startedBy\":\"{TestJob.ByAgent}\"}}";
        _testRunsForChecks =
        [
            new TestRunInfo("check-run", office.Id, office.Machine, office.MachineLabel, "me", "This PC", project.Id, commit, agentJob, "", status, status == "failed" ? 1 : null, false, now, now, status is "queued" or "running" ? null : now, "tests", null, 1, 3, 0),
            new TestRunInfo("check-run-laptop", laptop.Id, laptop.Machine, laptop.MachineLabel, "me", "This PC", project.Id, commit, "{}", "", "failed", 1, false, now - 600, now - 600, now - 500, "tests", null, 3, 1, 0),
            new TestRunInfo("check-run-earlier", office.Id, office.Machine, office.MachineLabel, "me", "This PC", project.Id, commit, "{}", "", "failed", 1, false, now - 1200, now - 1200, now - 1100, "tests", null, 2, 1, 0),
        ];
        _testLogForChecks = "Test run for check\n> run the tests\nClock.DayNight: expected day, it was night\nTest took 4s.\nVerdict: Failed (exit code 1)\n";
        _checksForChecks = new()
        {
            ["check-run"] = [new("Clock.DayNight", TestChecks.Failed), new("Input.Jump", TestChecks.Failed), new("Physics.Fall", TestChecks.Failed), new("Save.Load", TestChecks.Passed)],
        };
        CheckHistoryRow Row(string name, string outcome, TestRunInfo run) => new(name, outcome, run.Id, run.Machine, run.MachineLabel, run.Commit, run.CreatedAt);
        var (latest, onLaptop, earlier) = (_testRunsForChecks[0], _testRunsForChecks[1], _testRunsForChecks[2]);
        _checkHistoryForChecks =
        [
            Row("Clock.DayNight", TestChecks.Failed, latest), Row("Input.Jump", TestChecks.Failed, latest), Row("Physics.Fall", TestChecks.Failed, latest),
            Row("Clock.DayNight", TestChecks.Passed, onLaptop), Row("Input.Jump", TestChecks.Passed, onLaptop), Row("Physics.Fall", TestChecks.Failed, onLaptop),
            Row("Clock.DayNight", TestChecks.Passed, earlier), Row("Input.Jump", TestChecks.Failed, earlier), Row("Physics.Fall", TestChecks.Failed, earlier),
        ];
    }
#endif
}
