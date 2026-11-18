using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

/// <summary>
/// What a test machine is sent: a project's tests, at one pushed commit. The PC linking writes it
/// and signs it with the link's key. Plain commands, run in the project's folder, whatever the
/// project is made with.
/// </summary>
public sealed record TestJob(string Kind, string ProjectId, string ProjectName, string RepoUrl, string Commit, string Prepare, string Test, int? CouldntRunCode, string[] Tags, int TimeoutMinutes)
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

/// <summary>
/// Runs one job on this PC: checks it came over a link this PC holds, unexpired, with the access it
/// needs, and signed with that link's key; then checks out the commit in its own folder under the
/// work folder, runs prepare and test with this PC's own overrides, and streams the log.
/// </summary>
public sealed class TestRunner(LocalDatabase database, DeviceSession devices, TestMachineSettings settings, TestRunInfo run)
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(1);
    private const int MaxChunkChars = 32_000;

    private readonly CancellationTokenSource _stop = new();
    private readonly StringBuilder _pending = new();
    private readonly object _pendingLock = new();
    private int _nextSeq;
    private bool _truncated;
    private bool _lost;
    private bool _cancelled;

    public TestRunInfo Run => run;

    /// <summary>Stops it from this PC, which counts as cancelled.</summary>
    public void Cancel()
    {
        _cancelled = true;
        _stop.Cancel();
    }

    public async Task<RunHere> RunAsync()
    {
        var keepAlive = KeepAliveAsync();
        var (verdict, exitCode, record) = await RunJobAsync();
        _stop.Cancel(); // Ends the heartbeat.
        await keepAlive;
        Line($"Verdict: {VerdictLabel(verdict)}{(exitCode is { } code ? $" (exit code {code})" : "")}");
        await FlushAsync();
        if (!_lost) await devices.EndRunAsync(run.Id, verdict, exitCode);
        return record with { Verdict = _lost ? "lost" : verdict, ExitCode = exitCode };
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
        _ => status,
    };

    private async Task<(string Verdict, int? ExitCode, RunHere Record)> RunJobAsync()
    {
        var record = new RunHere(run.Id, DateTimeOffset.Now, run.LinkerLabel, run.ProjectId, run.Commit, settings.Shell, "", "", "", "couldnt_run", null, null);
        (string, int?, RunHere) Refuse(string why)
        {
            Line($"Refused: {why}");
            AppLog.Write($"Refused test run {run.Id}: {why}");
            return ("couldnt_run", null, record with { Note = why });
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

        if (settings.WorkFolder.Trim().Length == 0 || !Path.IsPathFullyQualified(settings.WorkFolder)) return Refuse("this PC has no work folder set.");
        var folder = Path.Combine(settings.WorkFolder, FolderName(job));
        var timeout = TimeSpan.FromMinutes(Math.Clamp(settings.TimeoutMinutes ?? job.TimeoutMinutes, 1, 24 * 60));
        record = record with { Project = job.ProjectName, Folder = folder, Prepare = job.Prepare, Test = job.Test };

        // Exactly what runs, first, as any PC can change a project's synced commands.
        Line($"Test run for {job.ProjectName}, from {run.LinkerLabel}, at {job.Commit}");
        Line($"Folder: {folder}");
        Line($"Shell: {settings.Shell}. Time limit: {(int)timeout.TotalMinutes} minutes.");
        if (job.Prepare.Trim().Length > 0) Line($"Prepare: {job.Prepare}");
        Line($"Test: {job.Test}");
        foreach (var (name, _) in settings.Variables()) Line($"Environment: {name} is set by this PC.");
        Line("");

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

    // As GitHub Actions runs a step's script in each shell, so a command behaves here as it would there.
    // Null when the shell couldn't be started.
    private async Task<int?> RunCommandAsync(string command, string folder, CancellationToken cancel)
    {
        var scripts = Path.Combine(Path.GetTempPath(), "agent-orange-test-runs");
        Directory.CreateDirectory(scripts);
        var name = Path.Combine(scripts, $"{run.Id}-{Guid.NewGuid():N}"[..48]);

        ProcessStartInfo info;
        switch (settings.Shell)
        {
            case TestMachineSettings.Cmd:
                name += ".cmd";
                await File.WriteAllTextAsync(name, command + "\r\n", new UTF8Encoding(false), CancellationToken.None);
                info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe");
                info.Arguments = $"/D /E:ON /V:OFF /S /C \"CALL \"{name}\"\"";
                break;
            case TestMachineSettings.Bash:
                if (GitBash() is not { } bash)
                {
                    Line("Git Bash wasn't found on this PC. Install Git for Windows, or choose another shell in Settings › Test machines.");
                    return null;
                }
                name += ".sh";
                await File.WriteAllTextAsync(name, command.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false), CancellationToken.None);
                info = new ProcessStartInfo(bash);
                foreach (var argument in new[] { "--noprofile", "--norc", "-eo", "pipefail", name.Replace('\\', '/') }) info.ArgumentList.Add(argument);
                break;
            default:
                name += ".ps1";
                await File.WriteAllTextAsync(name, $"$ErrorActionPreference = 'stop'\r\n{command}\r\nif ((Test-Path -LiteralPath variable:\\LASTEXITCODE)) {{ exit $LASTEXITCODE }}\r\n", new UTF8Encoding(true), CancellationToken.None);
                // -File rather than GitHub's -Command ". 'script'", as Windows PowerShell 5.1 turns
                // the script's exit code into 1 that way.
                info = new ProcessStartInfo("powershell.exe");
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", name }) info.ArgumentList.Add(argument);
                break;
        }
        info.WorkingDirectory = folder;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true; // Closed at once, so nothing waits on someone typing.
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        foreach (var (key, value) in settings.Variables()) info.Environment[key] = value;

        try
        {
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, line) => { if (line.Data is not null) Line(line.Data); };
            process.ErrorDataReceived += (_, line) => { if (line.Data is not null) Line(line.Data); };
            try
            {
                process.Start();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Line($"Couldn't start {settings.Shell}: {error.Message}");
                return null;
            }
            ChildProcesses.Track(process);
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It ended by itself meanwhile.
                }
                await process.WaitForExitAsync(CancellationToken.None);
                return process.ExitCode;
            }
            await process.WaitForExitAsync(CancellationToken.None); // The last of its output.
            return process.ExitCode;
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

    private void Line(string text)
    {
        lock (_pendingLock) _pending.Append(text).Append('\n');
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
            await FlushAsync();
            if (DateTimeOffset.UtcNow - lastBeat < HeartbeatEvery) continue;
            lastBeat = DateTimeOffset.UtcNow;
            var beat = await devices.HeartbeatAsync(run.Id);
            Hear(beat);
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
            _truncated |= keepGoing.Truncated;
            if (keepGoing.Cancel && !_cancelled)
            {
                _cancelled = true;
                _stop.Cancel();
            }
        }
    }

    // Sent in order, a part at a time, each numbered so one sent again is only kept once.
    private async Task FlushAsync()
    {
        while (true)
        {
            string chunk;
            lock (_pendingLock)
            {
                if (_pending.Length == 0) return;
                chunk = _pending.ToString(0, Math.Min(_pending.Length, MaxChunkChars));
            }
            if (_truncated)
            {
                lock (_pendingLock) _pending.Clear();
                return;
            }
            var sent = await devices.AppendLogAsync(run.Id, _nextSeq, chunk);
            Hear(sent);
            if (!sent.Worked) return; // Tried again next time, with the same number.
            _nextSeq++;
            lock (_pendingLock) _pending.Remove(0, chunk.Length);
        }
    }
}
