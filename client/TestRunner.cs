using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrange;

/// <summary>
/// What a test machine is sent: which commit of which repo to test, never what to run. The PC
/// linking writes it, with a run id and the time it was sent, the repo URL from its own folder's
/// origin, the access it needs and the tags it asked for, and signs it with the link's key. The
/// commands are in the repo, in .agent-orange/tests.json at that commit. StartedBy is "agent" for a
/// run an agent started with run_tests (#117), and left out for a person's, so the run list can
/// say which, signed with the rest. It changes nothing about what runs or who may run it. Filter,
/// when set, asks for only some of the tests: the target checks it's one plain word and puts it
/// in the repo's own testWithFilter at that commit, so it decides nothing else about what runs.
///
/// An agent job (Kind "agent", Access "agents") asks instead for an agent conversation on the
/// target, for that project, starting from that commit: Task is what the PC linking asks of it,
/// in its own words, and Title what to call it. The target only takes one over a link of Agents
/// access, and runs it with its own login and settings, in its own clone of the project.
/// </summary>
public sealed record TestJob(string Kind, string RunId, long SentAt, string ProjectId, string ProjectName, string RepoUrl, string Commit, string Access, string[] Tags,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StartedBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Filter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Task = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null)
{
    public const string TestsKind = "tests";
    public const string AgentKind = "agent";
    public const string ByAgent = "agent";

    /// <summary>The most an agent job's task may say, well within what the server keeps of a job.</summary>
    public const int MostTaskLength = 20_000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The job, or null when it can't be read.</summary>
    public static TestJob? Read(string job)
    {
        try
        {
            return JsonSerializer.Deserialize<TestJob>(job, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Who a job says started it, "agent" or null for a person, or null when it can't be read.</summary>
    public static string? StartedByIn(string job) => Read(job)?.StartedBy;
}

/// <summary>How this PC runs tests for others: its own, never synced, and applied over what a job asks for.</summary>
public sealed record TestMachineSettings(
    bool Accepting = false,
    string Label = "",
    string[]? Tags = null,
    string WorkFolder = "",
    string Shell = TestMachineSettings.PowerShell,
    string Environment = "",
    int? TimeoutMinutes = null,
    Dictionary<string, string>? BorrowFrom = null,
    Dictionary<string, string>? Grants = null,
    string AgentsMode = "auto")
{
    /// <summary>
    /// The highest access the person here grants a PC that links to this one, by that PC's install:
    /// Tests unless they chose more, in Authorized clients. A link is made with the lower of this
    /// and what that PC asked for.
    /// </summary>
    public string GrantFor(string linker) =>
        Grants is not null && Grants.TryGetValue(linker, out var granted) && MachineLinks.Offered.Contains(granted) ? granted : "tests";

    /// <summary>
    /// What an agent conversation another PC starts here may do without asking, as Claude Code's
    /// permission modes say: auto by default, which asks only before something risky. Anything it
    /// asks waits for someone at this PC. Never one that asks for nothing.
    /// </summary>
    public static readonly string[] AgentsModes = ["auto", "acceptEdits", "default"];

    public string AgentsPermissionMode => AgentsModes.Contains(AgentsMode) ? AgentsMode : "auto";
    /// <summary>
    /// The clone on this PC a project's first test checkout copies from instead of downloading the
    /// whole repo, as the person chose it here, by project id (#112). Only ever read.
    /// </summary>
    public string? BorrowFor(string projectId) =>
        BorrowFrom is not null && BorrowFrom.TryGetValue(projectId, out var folder) && folder.Trim().Length > 0 ? folder : null;

    public const string PowerShell = "PowerShell";
    public const string Cmd = "cmd";
    public const string Bash = "bash";
    public static readonly string[] Shells = [PowerShell, Cmd, Bash];

    /// <summary>KEY=VALUE, one a line. Lines without an = are left out.</summary>
    public IEnumerable<(string Key, string Value)> Variables() =>
        Environment.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
            .Select(parts => (parts[0].Trim(), parts[1]));
}

/// <summary>
/// A run this PC did, with exactly what it ran, kept on this PC so it can show them. Task is what an
/// agent task asked, in the words of the PC that sent it, and Branch where its work was pushed.
/// </summary>
public sealed record RunHere(string RunId, DateTimeOffset At, string From, string Project, string Commit, string Shell, string Folder, string Prepare, string Test, string Verdict, int? ExitCode, string? Note,
    string? Task = null, string? Branch = null);

/// <summary>
/// Runs one job on this PC, with nobody needing to be at it. It checks the job came over a link
/// this PC holds, unexpired, with the access it needs, signed with that link's key, with a run id
/// this PC hasn't run and sent within the last few hours. Then it checks the signed commit out of
/// the signed repo in its own folder under the work folder, reads the commands from the repo's
/// .agent-orange/tests.json there, runs prepare and test in a Windows job of their own with this
/// PC's overrides, and streams the log. Whatever happens, the run ends on the server.
/// </summary>
public sealed partial class TestRunner(LocalDatabase database, IRunServer devices, TestMachineSettings settings, TestRunInfo run)
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FutureSlack = TimeSpan.FromMinutes(10);

    // Under the server's 64 KiB a part, counted in UTF-8 as it counts them.
    private const int MaxChunkBytes = 60_000;

    private readonly CancellationTokenSource _stop = new();
    private readonly StringBuilder _pending = new();
    private readonly object _pendingLock = new();
    private (int Seq, string Text)? _inFlight; // Sent again exactly as it was, under its own number, until it's taken.
    private int _nextSeq;
    private List<CheckResult> _checks = [];
    private volatile bool _truncated;
    private volatile bool _lost;
    private volatile bool _cancelled;

    public TestRunInfo Run => run;

    /// <summary>Stops it from this PC, which counts as cancelled.</summary>
    public void Cancel()
    {
        _cancelled = true;
        _stop.Cancel();
    }

    public async Task<RunHere> RunAsync()
    {
        var record = new RunHere(run.Id, DateTimeOffset.Now, run.LinkerLabel, run.ProjectId, run.Commit, settings.Shell, "", "", "", "couldnt_run", null, null);
        var verdict = "couldnt_run";
        int? exitCode = null;
        var keepAlive = KeepAliveAsync();
        try
        {
            (verdict, exitCode, record) = await RunJobAsync(record);
        }
        catch (Exception error)
        {
            // Whatever went wrong, the run still ends below, and nothing is left running.
            Line($"The run stopped: {error.Message}");
            AppLog.Write($"Test run {run.Id} stopped: {error.GetType().Name}: {error.Message}");
            verdict = _cancelled ? "cancelled" : "couldnt_run";
            exitCode = null;
            record = record with { Note = error.Message };
        }
        finally
        {
            _stop.Cancel(); // Ends the heartbeat.
            try
            {
                await keepAlive;
                SaySteps();
                Line($"Verdict: {VerdictLabel(verdict)}{(exitCode is { } code ? $" (exit code {code})" : "")}");
                await FlushAsync();
            }
            catch (Exception error)
            {
                AppLog.Write($"Test run {run.Id} couldn't send the end of its log: {error.Message}");
            }
            if (!_lost && !_heldBack) await EndOnServerAsync(verdict, exitCode);

            // The server is on another release and turned it away, so the rest waits for this PC to update.
            if (!_lost && _heldBack) HoldBack(verdict, exitCode);
        }
        var note = _truncated ? $"{record.Note} Its log was cut short.".Trim() : record.Note;
        return record with
        {
            Verdict = _lost ? "lost" : verdict,
            ExitCode = exitCode,
            Note = !_lost && _heldBack ? $"{note} {HeldBackNote}".Trim() : note,
        };
    }

    public static string VerdictLabel(string status) => status switch
    {
        "queued" => "Waiting",
        "running" => "Running",
        "passed" => "Passed",
        "failed" => "Failed",
        "couldnt_run" => "Couldn't run",
        "timed_out" => "Timed out",
        "cancelled" => "Cancelled",
        "lost" => "Lost",
        "refused" => "Refused",
        "expired" => "Expired, never started",
        "finished" => "Finished",
        _ => status,
    };

    // Until the server has it, as the PC that sent it is waiting. With no answer at all, it's
    // tried again for a while; after that its lease runs out and it's called lost.
    private async Task EndOnServerAsync(string verdict, int? exitCode)
    {
        var wait = TimeSpan.FromSeconds(3);
        for (var tries = 0; tries < 10; tries++)
        {
            var ended = await devices.EndRunAsync(run.Id, verdict, exitCode, _checks);
            if (ended.VersionsDiffer)
            {
                HeldBackBy("its end");
                return;
            }

            // Checks the server won't take never keep the run from ending: it ends without them.
            if (ended.Status is { } refused && (int)refused is >= 400 and < 500 && refused != HttpStatusCode.Gone && _checks.Count > 0)
            {
                AppLog.Write($"Test run {run.Id}'s checks were refused by the server ({(int)refused}), so it ends without them.");
                _checks = [];
                continue;
            }
            if (ended.Status is not null) return;
            await Task.Delay(wait);
            wait = TimeSpan.FromSeconds(Math.Min(30, wait.TotalSeconds * 2));
        }
        AppLog.Write($"Test run {run.Id} ended as {verdict}, but the server couldn't be told.");
    }

    // Only a job that passes every check runs.
    private async Task<(string Verdict, int? ExitCode, RunHere Record)> CheckedJobAsync(RunHere record)
    {
        (string, int?, RunHere) Refuse(string why)
        {
            Line($"Refused: {why}");
            AppLog.Write($"Refused test run {run.Id}: {why}");
            return ("refused", null, record with { Note = why });
        }

        // This PC's own record of the link is what counts, as the server can't make one up.
        var key = MachineLinks.ReadKey(database, run.LinkId);
        if (key is null || !key.ThisPcIsMachine) return Refuse("this PC isn't linked to the PC that sent it, or the link was revoked.");
        if (run.LinkExpiresAt is null) return Refuse("the link it was sent over was revoked.");
        if (key.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return Refuse("the link it was sent over expired. Link the two PCs again.");
        if (!MachineLinks.Allows(key.Access, "tests") || run.LinkAccess is null || !MachineLinks.Allows(run.LinkAccess, "tests")) return Refuse("the link doesn't allow running anything.");
        if (!MachineLinks.JobMacMatches(key.Key, run)) return Refuse("its signature doesn't match the link's key, so the PC it says it's from didn't make it.");

        TestJob? job;
        try
        {
            job = JsonSerializer.Deserialize<TestJob>(run.Job, TestJob.Json);
        }
        catch (JsonException)
        {
            job = null;
        }
        if (job is null || job.Kind is not (TestJob.TestsKind or TestJob.AgentKind) || job.ProjectId != run.ProjectId || !string.Equals(job.Commit, run.Commit, StringComparison.OrdinalIgnoreCase) || job.RepoUrl.Trim().Length == 0)
        {
            return Refuse("it isn't a job this version can read.");
        }

        // What it does decides the access it needs, which its signed job has to say, and this PC's
        // own record of the link has to give, as must the link as the server has it now.
        var needed = job.Kind == TestJob.AgentKind ? "agents" : "tests";
        if (job.Access != needed) return Refuse($"it says it needs {MachineLinks.AccessLabel(job.Access)} access, which isn't what {(needed == "agents" ? "an agent task" : "a test run")} needs.");
        if (!MachineLinks.Allows(key.Access, needed) || !MachineLinks.Allows(run.LinkAccess, needed))
        {
            return Refuse($"it needs {MachineLinks.AccessLabel(needed)} access, more than the link gives ({MachineLinks.AccessLabel(MachineLinks.Lower(key.Access, run.LinkAccess))}).");
        }
        if (job.Filter is { } filter && TestsFile.FilterProblem(filter) is not null) return Refuse("its filter isn't one plain word, so it can't be put in a test command.");
        if (job.Kind == TestJob.AgentKind && (job.Task is not { Length: > 0 and <= TestJob.MostTaskLength } || job.Filter is not null)) return Refuse("it's an agent task with no task to do, or one too long.");

        // Signed, but it could still be one sent before, sent again.
        if (!string.Equals(job.RunId, run.Id, StringComparison.OrdinalIgnoreCase)) return Refuse("its run id isn't the one it was signed with.");
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(job.SentAt);
        if (age > MachineLinks.JobMaxAge) return Refuse($"it was sent more than {MachineLinks.JobMaxAge.TotalHours:0} hours ago.");
        if (age < -FutureSlack) return Refuse("it says it was sent in the future, so this PC's clock or the other's is wrong.");
        if (!MachineLinks.TakeRunId(database, run.Id)) return Refuse("this PC has run it already.");

        if (settings.WorkFolder.Trim().Length == 0 || !Path.IsPathFullyQualified(settings.WorkFolder)) return Refuse("this PC has no work folder set.");
        return job.Kind == TestJob.AgentKind ? await RunAgentAsync(job, record) : await RunStepsAsync(job, record);
    }

    private async Task<(string Verdict, int? ExitCode, RunHere Record)> RunJobAsync(RunHere record)
    {
#if DEBUG
        if (_jobForChecks is { } madeUp) return madeUp.Kind == TestJob.AgentKind ? await RunAgentAsync(madeUp, record) : await RunStepsAsync(madeUp, record);
#endif
        return await CheckedJobAsync(record);
    }

    // The job checked out: its steps, from checking out to the test command's exit code.
    private async Task<(string Verdict, int? ExitCode, RunHere Record)> RunStepsAsync(TestJob job, RunHere record)
    {
        var folder = Path.Combine(settings.WorkFolder, FolderName(job));
        record = record with { Project = job.ProjectName, Folder = folder };
        Line($"Test run for {job.ProjectName}, from {run.LinkerLabel}, at {job.Commit}");
        Line($"Repo: {job.RepoUrl}");
        Line($"Folder: {folder}");

        // Until the repo says how long it may take, this PC's own limit, or a day at most.
        var started = DateTimeOffset.UtcNow;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);

        // Which step the limit stopped, how far into the run, and which limit it was.
        (string, int?, RunHere) TimedOut(string during, string why)
        {
            var note = $"Timed out during {during}, {Duration(DateTimeOffset.UtcNow - started)} into the run: {why}.";
            Line(note);
            return ("timed_out", null, record with { Note = note });
        }

        var pcLimitText = settings.TimeoutMinutes is { } own ? $"this PC's time limit gives the whole run {TestsFile.Minutes(own)}" : null;
        limit.CancelAfter(TimeSpan.FromMinutes(settings.TimeoutMinutes ?? 24 * 60));
        var step = Stopwatch.StartNew();
        var checkout = await GitTools.TestCheckoutAsync(job.RepoUrl, folder, job.Commit, limit.Token, Line, settings.BorrowFor(job.ProjectId));
        Took("Checking out", step);
        if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record);
        if (limit.IsCancellationRequested) return TimedOut("checking out", pcLimitText ?? "a run may take a day at most");
        if (!checkout.Worked)
        {
            Line(checkout.Value);
            return ("couldnt_run", null, record with { Note = checkout.Value });
        }

        // What to run is what the repo says at this commit, and nothing else.
        var (setup, problem, exists) = TestsFile.Read(folder);
        if (setup is null)
        {
            var why = exists ? problem! : $"This commit has no {TestsFile.RelativePath}, which says how its tests run. Add it to the repo, commit and push it.";
            Line(why);
            return ("couldnt_run", null, record with { Note = why });
        }

        // Only some of the tests, when asked: the repo's own command for that, with the filter put in.
        if (job.Filter is not null && setup.TestWithFilter is null)
        {
            var why = $"This run asked to test only {job.Filter}, but {TestsFile.RelativePath} at this commit has no \"testWithFilter\" saying how to pass a filter to its tests.";
            Line(why);
            return ("couldnt_run", null, record with { Note = why });
        }
        var testCommand = setup.TestCommand(job.Filter);

        // This PC's own limit is for the whole run, in place of the repo's. Otherwise the repo's: one
        // for the whole run, or, with prepareTimeoutMinutes, one for checking out and prepare and
        // another for the test command, from when it starts.
        var split = settings.TimeoutMinutes is null && setup.PrepareTimeoutMinutes is not null;
        var wholeRunText = pcLimitText ?? $"timeoutMinutes in {TestsFile.RelativePath} gives the whole run {TestsFile.Minutes(setup.TimeoutMinutes)}, checking out and prepare included";
        var prepareLimitText = split ? $"prepareTimeoutMinutes in {TestsFile.RelativePath} gives checking out and prepare {TestsFile.Minutes(setup.PrepareTimeoutMinutes!.Value)}" : wholeRunText;
        var first = TimeSpan.FromMinutes(Math.Clamp(settings.TimeoutMinutes ?? setup.PrepareTimeoutMinutes ?? setup.TimeoutMinutes, 1, 24 * 60)) - (DateTimeOffset.UtcNow - started);
        limit.CancelAfter(first > TimeSpan.Zero ? first : TimeSpan.Zero);
        record = record with { Prepare = setup.Prepare, Test = testCommand };

        // Exactly what runs.
        Line($"From {TestsFile.RelativePath} at this commit:");
        Line($"Shell: {settings.Shell}. Time limit: {(settings.TimeoutMinutes is { } shown ? $"{TestsFile.Minutes(shown)} for the whole run, set on this PC" : setup.LimitsText)}.");
        if (setup.Prepare.Length > 0) Line($"Prepare: {setup.Prepare}");
        if (job.Filter is { } filter) Line($"Filter: {filter}, put in testWithFilter, as this run asked to test only that.");
        Line($"Test: {testCommand}");
        if (setup.Results is { } resultsAt) Line($"Results file: {resultsAt}");
        foreach (var (name, _) in settings.Variables()) Line($"Environment: {name} is set by this PC.");
        Line("");

        // Prepare failing means the tests never got to run.
        if (setup.Prepare.Length > 0)
        {
            Line($"> {setup.Prepare}");
            step.Restart();
            var prepared = await RunCommandAsync(setup.Prepare, folder, limit.Token);
            Took("Prepare", step);
            if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record);
            if (limit.IsCancellationRequested) return TimedOut("prepare", prepareLimitText);
            if (prepared != 0) return ("couldnt_run", prepared, record with { Note = prepared is null ? "The shell couldn't be started." : "Prepare failed." });
        }

        // With a limit of its own, the test command's starts now.
        var testLimitText = wholeRunText;
        if (split)
        {
            limit.CancelAfter(TimeSpan.FromMinutes(setup.TimeoutMinutes));
            testLimitText = $"timeoutMinutes in {TestsFile.RelativePath} gives the test command {TestsFile.Minutes(setup.TimeoutMinutes)}";
        }
        Line($"> {testCommand}");
        step.Restart();
        var testStarted = DateTime.UtcNow;
        var tested = await RunCommandAsync(testCommand, folder, limit.Token);
        Took("Test", step);
        if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record);
        if (limit.IsCancellationRequested) return TimedOut("the test command", testLimitText);

        // How each check went, when the repo names where its test command writes that (#105).
        if (tested is not null && setup.Results is { } results)
        {
            var (checks, said) = TestChecks.Read(folder, results, testStarted);
            _checks = checks;
            Line(said);
        }
        return tested switch
        {
            null => ("couldnt_run", null, record with { Note = "The shell couldn't be started." }),
            0 => ("passed", 0, record),
            var code when code == setup.CouldNotRunExitCode => ("couldnt_run", code, record),
            var code => ("failed", code, record),
        };
    }

    // How long each step took, said as it ends and again before the verdict.
    private readonly List<(string Step, TimeSpan Took)> _steps = [];

    private void Took(string name, Stopwatch watch)
    {
        _steps.Add((name, watch.Elapsed));
        Line($"{name} took {Duration(watch.Elapsed)}.");
    }

    private void SaySteps()
    {
        if (_steps.Count > 0) Line($"Steps: {string.Join(", ", _steps.Select(each => $"{each.Step.ToLowerInvariant()} {Duration(each.Took)}"))}.");
    }

#if DEBUG
    /// <summary>
    /// Headless checks only: a run's steps on this PC, from a local repo, without the server or a
    /// link, which a real run never goes without. The verdict, its note and the whole log.
    /// </summary>
    internal static async Task<(string Verdict, string? Note, string Log)> RunStepsForChecksAsync(TestMachineSettings settings, string repo, string commit, string projectName, string? filter = null)
    {
        var run = new TestRunInfo(MachineLinks.NewId(), "", "", "", "", "a check", "check-project", commit, "", "", "running", null, false, 0, null, null, null, null);
        var runner = new TestRunner(null!, null!, settings, run);
        var job = new TestJob(TestJob.TestsKind, run.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), run.ProjectId, projectName, repo, commit, "tests", [], Filter: filter);
        var (verdict, _, record) = await runner.RunStepsAsync(job, new RunHere(run.Id, DateTimeOffset.Now, run.LinkerLabel, run.ProjectId, commit, settings.Shell, "", "", "", "couldnt_run", null, null));
        runner.SaySteps();
        lock (runner._pendingLock) return (verdict, record.Note, runner._pending.ToString());
    }

    // Headless checks only: the job run as it is, without a link, by RunForChecksAsync.
    private TestJob? _jobForChecks;

    /// <summary>
    /// Headless checks only: a whole run, from a local repo, without a link, telling the server
    /// it's given (a made-up one) about it as a real run does, ending and all. What this PC keeps of it.
    /// </summary>
    internal static async Task<RunHere> RunForChecksAsync(LocalDatabase database, IRunServer server, TestMachineSettings settings, string repo, string commit, string projectName)
    {
        var run = new TestRunInfo(MachineLinks.NewId(), "", "", "", "", "a check", "check-project", commit, "", "", "running", null, false, 0, null, null, null, null);
        var runner = new TestRunner(database, server, settings, run)
        {
            _jobForChecks = new TestJob(TestJob.TestsKind, run.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), run.ProjectId, projectName, repo, commit, "tests", []),
        };
        return await runner.RunAsync();
    }
#endif

    /// <summary>A length of time as the log says it: 8s, 2m 14s, 1h 02m.</summary>
    public static string Duration(TimeSpan length) =>
        length.TotalHours >= 1 ? $"{(int)length.TotalHours}h {length.Minutes:00}m"
        : length.TotalMinutes >= 1 ? $"{(int)length.TotalMinutes}m {length.Seconds:00}s"
        : $"{Math.Max(0, (int)length.TotalSeconds)}s";

    // A plain name, as the project's name comes from another PC, with a part of its id so two of the same name don't share.
    private static string FolderName(TestJob job)
    {
        var name = new string([.. job.ProjectName.Select(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_' ? letter : '-')]).Trim('-');
        if (name.Length > 40) name = name[..40];
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.ProjectId)))[..8].ToLowerInvariant();
        return name.Length > 0 ? $"{name}-{id}" : id;
    }

    // As GitHub Actions runs a step's script in each shell, so a command behaves here as it would
    // there, in a Windows job of its own. Null when the shell couldn't be started, or was stopped.
    private async Task<int?> RunCommandAsync(string command, string folder, CancellationToken cancel)
    {
        var scripts = Path.Combine(Path.GetTempPath(), "agent-orange-test-runs");
        Directory.CreateDirectory(scripts);
        var name = Path.Combine(scripts, $"{run.Id}-{Guid.NewGuid():N}"[..48]);

        string program;
        string commandLine;
        switch (settings.Shell)
        {
            case TestMachineSettings.Cmd:
                name += ".cmd";
                await File.WriteAllTextAsync(name, command + "\r\n", new UTF8Encoding(false), CancellationToken.None);
                program = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec && Path.IsPathFullyQualified(comSpec) ? comSpec : Path.Combine(Environment.SystemDirectory, "cmd.exe");
                commandLine = $"{JobProcess.Quote(program)} /D /E:ON /V:OFF /S /C \"CALL \"{name}\"\"";
                break;
            case TestMachineSettings.Bash:
                if (GitBash() is not { } bash)
                {
                    Line("Git Bash wasn't found on this PC. Install Git for Windows, or choose another shell in Settings › Test machines.");
                    return null;
                }
                name += ".sh";
                await File.WriteAllTextAsync(name, command.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false), CancellationToken.None);
                program = bash;
                commandLine = $"{JobProcess.Quote(bash)} --noprofile --norc -eo pipefail {JobProcess.Quote(name.Replace('\\', '/'))}";
                break;
            default:
                // -File rather than GitHub's -Command ". 'script'", as Windows PowerShell 5.1 turns
                // the script's exit code into 1 that way.
                name += ".ps1";
                await File.WriteAllTextAsync(name, $"$ErrorActionPreference = 'stop'\r\n{command}\r\nif ((Test-Path -LiteralPath variable:\\LASTEXITCODE)) {{ exit $LASTEXITCODE }}\r\n", new UTF8Encoding(true), CancellationToken.None);
                program = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                commandLine = $"{JobProcess.Quote(program)} -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {JobProcess.Quote(name)}";
                break;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables()) environment[(string)variable.Key] = (string?)variable.Value ?? "";
        foreach (var (key, value) in settings.Variables()) environment[key] = value;

        try
        {
            JobProcess process;
            try
            {
                process = JobProcess.Start(program, commandLine, folder, environment, Line);
            }
            catch (Win32Exception error)
            {
                Line($"{error.Message} {new Win32Exception(error.NativeErrorCode).Message}");
                return null;
            }
            using (process)
            {
                return await process.WaitAsync(cancel);
            }
        }
        finally
        {
            try
            {
                File.Delete(name);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Temp, so it goes in time anyway.
            }
        }
    }

    // Git for Windows' own bash, beside its git. Not Windows' bash.exe, which is WSL's.
    private static string? GitBash()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var git = Path.Combine(folder.Trim('"'), "git.exe");
            if (!File.Exists(git)) continue;
            for (var up = Path.GetDirectoryName(folder.Trim('"')); up is not null; up = Path.GetDirectoryName(up))
            {
                var bash = Path.Combine(up, "bin", "bash.exe");
                if (File.Exists(bash)) return bash;
                if (Path.GetFileName(up).Equals("Git", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(installed) ? installed : null;
    }

    // The server can't keep a nul, and would refuse the whole part.
    private void Line(string text)
    {
        lock (_pendingLock) _pending.Append(text.Replace('\0', '�')).Append('\n');
    }

    // Renews the lease, sends the log, and hears about a cancel, until the run ends.
    private async Task KeepAliveAsync()
    {
        var lastBeat = DateTimeOffset.UtcNow;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LogEvery, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await FlushAsync();
                if (_heldBack || DateTimeOffset.UtcNow - lastBeat < HeartbeatEvery) continue;
                lastBeat = DateTimeOffset.UtcNow;
                Hear(await devices.HeartbeatAsync(run.Id));
            }
            catch (Exception error)
            {
                AppLog.Write($"Test run {run.Id} couldn't reach the server: {error.Message}");
            }
        }
    }

    private void Hear(ServerAnswer<RunKeepGoing> answer)
    {
        if (answer.VersionsDiffer)
        {
            HeldBackBy("its report");
            return;
        }
        if (answer.Status == HttpStatusCode.Gone)
        {
            // Its lease ran out while this PC couldn't reach the server, so it's been called lost.
            _lost = true;
            _stop.Cancel();
            return;
        }
        if (answer.Value is { } keepGoing)
        {
            if (keepGoing.Truncated) _truncated = true;
            if (keepGoing.Cancel && !_cancelled)
            {
                _cancelled = true;
                _stop.Cancel();
            }
        }
    }

    // Sent in order, a part at a time, each numbered. A part that got no answer goes again exactly
    // as it was, under the same number. One the server refuses outright ends the log there.
    private async Task FlushAsync()
    {
        while (!_truncated)
        {
            if (_heldBack) return; // Kept for after this PC has updated.
            if (_inFlight is null)
            {
                lock (_pendingLock)
                {
                    if (_pending.Length == 0) return;
                    _inFlight = (_nextSeq, TakeChunk());
                }
            }
            var (seq, text) = _inFlight.Value;
            var sent = await devices.AppendLogAsync(run.Id, seq, text);
            Hear(sent);
            if (sent.Worked)
            {
                _inFlight = null;
                _nextSeq++;
                continue;
            }
            if (sent.Status is { } status && (int)status is >= 400 and < 500 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Gone or HttpStatusCode.UpgradeRequired))
            {
                AppLog.Write($"Test run {run.Id}'s log was refused by the server ({(int)status}), so the rest isn't sent.");
                _truncated = true;
            }
            return; // Tried again next time, if it can be.
        }
        lock (_pendingLock) _pending.Clear();
        _inFlight = null;
    }

    // As much from the front as fits in a part, by its UTF-8 size, never splitting a surrogate pair.
    private string TakeChunk() => TakeChunk(_pending);

    private static string TakeChunk(StringBuilder pending)
    {
        var bytes = 0;
        var end = 0;
        while (end < pending.Length)
        {
            var letter = pending[end];
            var pair = char.IsHighSurrogate(letter) && end + 1 < pending.Length && char.IsLowSurrogate(pending[end + 1]);
            var size = pair ? 4 : letter < 0x80 ? 1 : letter < 0x800 ? 2 : 3;
            if (bytes + size > MaxChunkBytes) break;
            bytes += size;
            end += pair ? 2 : 1;
        }
        var chunk = pending.ToString(0, end);
        pending.Remove(0, end);
        return chunk;
    }
}
