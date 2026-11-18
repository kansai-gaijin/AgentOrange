using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

/// <summary>
/// What a test machine is sent: a project's tests, at one pushed commit. The PC linking writes it,
/// with a run id and the time it was sent, and signs it with the link's key. Plain commands, run
/// in the project's folder, whatever the project is made with.
/// </summary>
public sealed record TestJob(string Kind, string RunId, long SentAt, string ProjectId, string ProjectName, string RepoUrl, string Commit, string Prepare, string Test, int? CouldntRunCode, string[] Tags, int TimeoutMinutes)
{
    public const string TestsKind = "tests";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>How this PC runs tests for others: its own, never synced, and applied over what a job asks for.</summary>
public sealed record TestMachineSettings(
    bool Accepting = false,
    string Label = "",
    string[]? Tags = null,
    string WorkFolder = "",
    string Shell = TestMachineSettings.PowerShell,
    string Environment = "",
    int? TimeoutMinutes = null)
{
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

/// <summary>A run this PC did, with exactly what it ran, kept on this PC so it can show them.</summary>
public sealed record RunHere(string RunId, DateTimeOffset At, string From, string Project, string Commit, string Shell, string Folder, string Prepare, string Test, string Verdict, int? ExitCode, string? Note);

/// <summary>What a person on this PC is asked to allow before a run's commands run here.</summary>
public sealed record TestApproval(string RunId, string From, string Project, string RepoUrl, string Commit, string Prepare, string Test, string Shell, string Folder);

/// <summary>
/// Runs one job on this PC. It checks the job came over a link this PC holds, unexpired, with the
/// access it needs, signed with that link's key, with a run id this PC hasn't run and sent within
/// the last few hours. Commands a person here hasn't allowed for that link and project wait for
/// one to allow them. Then it checks the commit out in its own folder under the work folder, runs
/// prepare and test in a Windows job of their own with this PC's overrides, and streams the log.
/// Whatever happens, the run ends on the server.
/// </summary>
public sealed class TestRunner(LocalDatabase database, DeviceSession devices, TestMachineSettings settings, TestRunInfo run, Func<TestApproval, CancellationToken, Task<bool>> askToAllow)
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
    private volatile bool _truncated;
    private volatile bool _lost;
    private volatile bool _cancelled;
    private volatile bool _waiting;
    private volatile bool _beatNow;

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
                Line($"Verdict: {VerdictLabel(verdict)}{(exitCode is { } code ? $" (exit code {code})" : "")}");
                await FlushAsync();
            }
            catch (Exception error)
            {
                AppLog.Write($"Test run {run.Id} couldn't send the end of its log: {error.Message}");
            }
            if (!_lost) await EndOnServerAsync(verdict, exitCode);
        }
        return record with
        {
            Verdict = _lost ? "lost" : verdict,
            ExitCode = exitCode,
            Note = _truncated ? $"{record.Note} Its log was cut short.".Trim() : record.Note,
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
        _ => status,
    };

    // Until the server has it, as the PC that sent it is waiting. With no answer at all, it's
    // tried again for a while; after that its lease runs out and it's called lost.
    private async Task EndOnServerAsync(string verdict, int? exitCode)
    {
        var wait = TimeSpan.FromSeconds(3);
        for (var tries = 0; tries < 10; tries++)
        {
            var ended = await devices.EndRunAsync(run.Id, verdict, exitCode);
            if (ended.Status is not null) return;
            await Task.Delay(wait);
            wait = TimeSpan.FromSeconds(Math.Min(30, wait.TotalSeconds * 2));
        }
        AppLog.Write($"Test run {run.Id} ended as {verdict}, but the server couldn't be told.");
    }

    private async Task<(string Verdict, int? ExitCode, RunHere Record)> RunJobAsync(RunHere record)
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
        if (!MachineLinks.Allows(key.Access, "tests") || run.LinkAccess is null || !MachineLinks.Allows(run.LinkAccess, "tests")) return Refuse("the link doesn't allow running tests.");
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
        if (job is null || job.Kind != TestJob.TestsKind || job.ProjectId != run.ProjectId || !string.Equals(job.Commit, run.Commit, StringComparison.OrdinalIgnoreCase) || job.Test.Trim().Length == 0)
        {
            return Refuse("it isn't a test job this version can read.");
        }

        // Signed, but it could still be one sent before, sent again.
        if (!string.Equals(job.RunId, run.Id, StringComparison.OrdinalIgnoreCase)) return Refuse("its run id isn't the one it was signed with.");
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(job.SentAt);
        if (age > MachineLinks.JobMaxAge) return Refuse($"it was sent more than {MachineLinks.JobMaxAge.TotalHours:0} hours ago.");
        if (age < -FutureSlack) return Refuse("it says it was sent in the future, so this PC's clock or the other's is wrong.");
        if (!MachineLinks.TakeRunId(database, run.Id)) return Refuse("this PC has run it already.");

        if (settings.WorkFolder.Trim().Length == 0 || !Path.IsPathFullyQualified(settings.WorkFolder)) return Refuse("this PC has no work folder set.");
        var folder = Path.Combine(settings.WorkFolder, FolderName(job));
        var timeout = TimeSpan.FromMinutes(Math.Clamp(settings.TimeoutMinutes ?? job.TimeoutMinutes, 1, 24 * 60));
        record = record with { Project = job.ProjectName, Folder = folder, Prepare = job.Prepare, Test = job.Test };

        // Exactly what runs, first.
        Line($"Test run for {job.ProjectName}, from {run.LinkerLabel}, at {job.Commit}");
        Line($"Repo: {job.RepoUrl}");
        Line($"Folder: {folder}");
        Line($"Shell: {settings.Shell}. Time limit: {(int)timeout.TotalMinutes} minutes.");
        if (job.Prepare.Trim().Length > 0) Line($"Prepare: {job.Prepare}");
        Line($"Test: {job.Test}");
        foreach (var (name, _) in settings.Variables()) Line($"Environment: {name} is set by this PC.");
        Line("");

        // Commands come from synced data, which the server or any paired PC can change, so a person
        // here allows them, once for each link and project, and again whenever they change.
        var commands = MachineLinks.CommandsHash(job.RepoUrl, job.Prepare, job.Test);
        if (!MachineLinks.IsAllowedHere(database, run.LinkId, job.ProjectId, commands))
        {
            Line("Waiting for someone on this PC to allow these commands.");
            _waiting = true;
            _beatNow = true;
            bool allowed;
            try
            {
                allowed = await askToAllow(new TestApproval(run.Id, run.LinkerLabel, job.ProjectName, job.RepoUrl, job.Commit, job.Prepare, job.Test, settings.Shell, folder), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                allowed = false;
            }
            _waiting = false;
            _beatNow = true;
            if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record);
            if (!allowed)
            {
                Line("Refused: a person on this PC didn't allow these commands.");
                return ("refused", null, record with { Note = "A person on this PC didn't allow these commands." });
            }
            MachineLinks.AllowHere(database, run.LinkId, job.ProjectId, commands);
            Line("Allowed by a person on this PC.");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(timeout);
        var checkout = await GitTools.TestCheckoutAsync(job.RepoUrl, folder, job.Commit, limit.Token, Line);
        if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record);
        if (limit.IsCancellationRequested) return ("timed_out", null, record);
        if (!checkout.Worked)
        {
            Line(checkout.Value);
            return ("couldnt_run", null, record with { Note = checkout.Value });
        }

        // Prepare failing means the tests never got to run.
        if (job.Prepare.Trim().Length > 0)
        {
            Line($"> {job.Prepare}");
            var prepared = await RunCommandAsync(job.Prepare, folder, limit.Token);
            if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record);
            if (limit.IsCancellationRequested) return ("timed_out", null, record);
            if (prepared != 0) return ("couldnt_run", prepared, record with { Note = prepared is null ? "The shell couldn't be started." : "Prepare failed." });
        }

        Line($"> {job.Test}");
        var tested = await RunCommandAsync(job.Test, folder, limit.Token);
        if (_cancelled || _lost) return (_lost ? "lost" : "cancelled", null, record);
        if (limit.IsCancellationRequested) return ("timed_out", null, record);
        return tested switch
        {
            null => ("couldnt_run", null, record with { Note = "The shell couldn't be started." }),
            0 => ("passed", 0, record),
            var code when code == job.CouldntRunCode => ("couldnt_run", code, record),
            var code => ("failed", code, record),
        };
    }

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
                if (!_beatNow && DateTimeOffset.UtcNow - lastBeat < HeartbeatEvery) continue;
                _beatNow = false;
                lastBeat = DateTimeOffset.UtcNow;
                Hear(await devices.HeartbeatAsync(run.Id, _waiting));
            }
            catch (Exception error)
            {
                AppLog.Write($"Test run {run.Id} couldn't reach the server: {error.Message}");
            }
        }
    }

    private void Hear(ServerAnswer<RunKeepGoing> answer)
    {
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
            if (sent.Status is { } status && (int)status is >= 400 and < 500 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Gone))
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
    private string TakeChunk()
    {
        var bytes = 0;
        var end = 0;
        while (end < _pending.Length)
        {
            var letter = _pending[end];
            var pair = char.IsHighSurrogate(letter) && end + 1 < _pending.Length && char.IsLowSurrogate(_pending[end + 1]);
            var size = pair ? 4 : letter < 0x80 ? 1 : letter < 0x800 ? 2 : 3;
            if (bytes + size > MaxChunkBytes) break;
            bytes += size;
            end += pair ? 2 : 1;
        }
        var chunk = _pending.ToString(0, end);
        _pending.Remove(0, end);
        return chunk;
    }
}
