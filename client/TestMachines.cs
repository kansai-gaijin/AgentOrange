using System.Net;
using System.Text.Json;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// Test machines on this PC (#93): what it offers other PCs, the link code it's showing, the links
/// to and from it, and the background loop that answers link requests and runs what it's sent. The
/// loop only runs in the window that syncs, so two windows never take the same work, and only
/// takes runs while this PC accepts them.
/// </summary>
public sealed class TestMachines
{
    private const string SettingsKey = "test_machine";
    private const string RunsHereKey = "test_runs_here";
    private const int RunsHereKept = 30;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ClaimEvery = TimeSpan.FromSeconds(5);

    private readonly LocalDatabase _database;
    private readonly ServerConnection _server;
    private readonly DeviceSession _devices;
    private readonly Func<bool> _hasSyncTurn;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _ticking;
    private DateTimeOffset _refreshedAt;
    private DateTimeOffset _claimedAt;
    private DateTimeOffset _askedForRequestsAt;
    private (string Label, string Tags, bool Accepting)? _published;

    public TestMachines(LocalDatabase database, ServerConnection server, DeviceSession devices, Func<bool> hasSyncTurn)
    {
        _database = database;
        _server = server;
        _devices = devices;
        _hasSyncTurn = hasSyncTurn;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => await TickAsync();
        timer.Start();
    }

    /// <summary>Something shown about test machines changed.</summary>
    public event Action? Changed;

    /// <summary>The last list from the server, or null before there's been one.</summary>
    public MachinesInfo? Latest { get; private set; }

    /// <summary>Why the list can't be had, when it can't.</summary>
    public string? Problem { get; private set; }

    /// <summary>The link code this PC is showing, which lives only in memory.</summary>
    public LinkCode? Code { get; private set; }

    /// <summary>Said once a code was thrown away for taking too many wrong tries.</summary>
    public string? CodeProblem { get; private set; }

    public TestRunner? Running { get; private set; }

    /// <summary>A run waiting for a person on this PC to allow its commands, and where the answer goes.</summary>
    public (TestApproval Request, TaskCompletionSource<bool> Answer)? Approval { get; private set; }

    /// <summary>A run's commands need a person here to allow them, so something should show them.</summary>
    public event Action<TestApproval>? ApprovalNeeded;

    /// <summary>The run that was waiting got its answer, or stopped waiting.</summary>
    public event Action? ApprovalEnded;

    /// <summary>The person's answer to the commands waiting.</summary>
    public void AnswerApproval(string runId, bool allow)
    {
        if (Approval is { } waiting && waiting.Request.RunId == runId) waiting.Answer.TrySetResult(allow);
    }

    // Runs keep their awaits on the window's thread, but the UI only ever hears from it there.
    private void OnWindow(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Invoke(action);
    }

    private async Task<bool> AskToAllowAsync(TestApproval request, CancellationToken cancel)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnWindow(() =>
        {
            Approval = (request, answer);
            Changed?.Invoke();
            ApprovalNeeded?.Invoke(request);
        });
        try
        {
            using (cancel.Register(() => answer.TrySetCanceled()))
            {
                return await answer.Task;
            }
        }
        finally
        {
            OnWindow(() =>
            {
                Approval = null;
                ApprovalEnded?.Invoke();
                Changed?.Invoke();
            });
        }
    }

    // This PC's own settings

    public TestMachineSettings Settings
    {
        get
        {
            TestMachineSettings? stored = null;
            if (_database.GetSetting(SettingsKey) is { Length: > 0 } text)
            {
                try
                {
                    stored = JsonSerializer.Deserialize<TestMachineSettings>(text, TestJob.Json);
                }
                catch (JsonException)
                {
                }
            }
            stored ??= new TestMachineSettings();
            return stored with
            {
                Label = stored.Label.Trim().Length > 0 ? stored.Label : Environment.MachineName,
                WorkFolder = stored.WorkFolder.Trim().Length > 0 ? stored.WorkFolder : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ao-test-runs"),
                Tags = stored.Tags ?? [],
            };
        }
    }

    public void Save(TestMachineSettings settings)
    {
        _database.SetSetting(SettingsKey, JsonSerializer.Serialize(settings, TestJob.Json));
        if (!settings.Accepting) Code = null; // A code only means anything while this PC takes runs.
        _refreshedAt = default; // Published on the next tick.
        Changed?.Invoke();
    }

    /// <summary>Exactly what this PC ran for others, newest first.</summary>
    public List<RunHere> RunsHere()
    {
        if (_database.GetSetting(RunsHereKey) is not { Length: > 0 } text) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RunHere>>(text, TestJob.Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void RememberRun(RunHere run)
    {
        var runs = RunsHere();
        runs.RemoveAll(each => each.RunId == run.RunId);
        runs.Insert(0, run);
        _database.SetSetting(RunsHereKey, JsonSerializer.Serialize(runs.Take(RunsHereKept), TestJob.Json));
    }

    // Link codes

    /// <summary>A code for the one access level there is so far, which the person here chooses by making it.</summary>
    public LinkCode MakeCode()
    {
        Code = new LinkCode("tests");
        CodeProblem = null;
        _askedForRequestsAt = default;
        Changed?.Invoke();
        return Code;
    }

    public void RevokeCode()
    {
        Code = null;
        Changed?.Invoke();
    }

    public async Task RefreshAsync()
    {
        _refreshedAt = default;
        await TickAsync();
    }

    private bool CanTalkToServer => _server.State == ServerState.Connected && _devices.IsPaired;

    private async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            if (Code is { Expired: true })
            {
                Code = null;
                Changed?.Invoke();
            }
            if (!CanTalkToServer)
            {
                var problem = _devices.IsPaired ? "Can't reach the server." : "Connect this PC to your server first, on the Server page. Test machines go through it.";
                if (Problem != problem)
                {
                    Problem = problem;
                    Changed?.Invoke();
                }
                return;
            }

            if (DateTimeOffset.UtcNow - _refreshedAt >= RefreshEvery) await RefreshListAsync();

            // The window showing a code answers for it, syncing or not, as only it has the code.
            if (Code is not null) await AnswerLinkRequestsAsync();

            var settings = Settings;
            if (settings.Accepting && Running is null && _hasSyncTurn() && DateTimeOffset.UtcNow - _claimedAt >= ClaimEvery)
            {
                _claimedAt = DateTimeOffset.UtcNow;
                await ClaimAsync(settings);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            AppLog.Write($"Test machines couldn't read this PC's settings: {error.Message}");
        }
        finally
        {
            _ticking = false;
        }
    }

    private async Task RefreshListAsync()
    {
        _refreshedAt = DateTimeOffset.UtcNow;
        var settings = Settings;
        var tags = string.Join('\n', settings.Tags ?? []);

        // Said again whenever it changes, and once after starting, so the server has it right.
        if (_published != (settings.Label, tags, settings.Accepting) || Latest?.ThisMachine is null && settings.Accepting)
        {
            var published = await _devices.PublishMachineAsync(settings.Label, settings.Tags ?? [], settings.Accepting);
            if (published.Worked) _published = (settings.Label, tags, settings.Accepting);
        }

        var askedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var answer = await _devices.MachinesAsync();
        Problem = DescribeProblem(answer.Status);
        if (answer.Value is { } latest)
        {
            Latest = latest;

            // A link the server no longer has was revoked from the other end, so its key is no use.
            // Not one saved since the list was asked for, which the list can't have had yet.
            var current = latest.Links.Select(link => link.Id).ToHashSet();
            foreach (var known in MachineLinks.KnownLinks(_database).Where(id => !current.Contains(id)))
            {
                if (MachineLinks.ReadKey(_database, known) is { } key && key.SavedAt >= askedAt - 1) continue;
                MachineLinks.ForgetKey(_database, known);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>What to say when the server didn't give an answer about test machines.</summary>
    public static string? DescribeProblem(HttpStatusCode? status) => status switch
    {
        null => "Can't reach the server.",
        HttpStatusCode.UpgradeRequired => "Paused until this PC and the server are on the same version.",
        HttpStatusCode.PreconditionFailed => "This PC paired with the server before test machines, so the server can't tell it apart. Sign out on the Server page and pair it again.",
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "This server doesn't have test machines yet. It updates itself with the next release.",
        { } code when (int)code is >= 200 and < 300 => null,
        _ => "The server couldn't answer. The log on the server has details.",
    };

    // As the target: each request is checked against the code this PC is showing, which is used
    // once, and never gives more access than the code was made for.
    private async Task AnswerLinkRequestsAsync()
    {
        if (DateTimeOffset.UtcNow - _askedForRequestsAt < TimeSpan.FromSeconds(2)) return;
        _askedForRequestsAt = DateTimeOffset.UtcNow;
        var pending = await _devices.PendingLinkRequestsAsync();
        if (pending.Value is not { } requests || Latest?.Me is not { } me) return;

        foreach (var request in requests)
        {
            if (Code is not { Expired: false } code)
            {
                await _devices.RejectLinkAsync(request.Id);
                continue;
            }
            if (request.Access != code.Access || !MachineLinks.ProofMatches(code.Code, code.Access, request, me))
            {
                await _devices.RejectLinkAsync(request.Id);
                code.WrongProofs++;
                if (code.WrongProofs >= MachineLinks.MaxWrongProofs)
                {
                    Code = null;
                    CodeProblem = "That code was tried too many times with the wrong value, so it no longer works. Create a new link.";
                    AppLog.Write("A link code was thrown away after too many wrong tries.");
                }
                Changed?.Invoke();
                continue;
            }

            // Used now, whatever the server says, as it's been tried. The link's key comes from a
            // key pair made for this one link, and its expiry is this PC's own.
            Code = null;
            var linkId = MachineLinks.NewId();
            using var ephemeral = MachineLinks.NewEphemeral();
            var machineKey = MachineLinks.PublicKey(ephemeral);
            var expiresAt = (DateTimeOffset.UtcNow + MachineLinks.LinkLifetime).ToUnixTimeSeconds();
            var transcript = MachineLinks.Transcript(request.Linker, me, code.Access, request.Nonce, request.Key, machineKey, linkId, expiresAt);
            if (MachineLinks.LinkKey(ephemeral, request.Key, transcript) is not { } linkKey)
            {
                await _devices.RejectLinkAsync(request.Id);
                CodeProblem = "The other PC's answer couldn't be read. Create a new link and try again.";
                Changed?.Invoke();
                continue;
            }
            var accepted = await _devices.AcceptLinkAsync(request.Id, linkId, machineKey, expiresAt, MachineLinks.Confirm(code.Code, linkKey, transcript));
            if (accepted.Worked)
            {
                MachineLinks.SaveKey(_database, new MachineLinkKey(linkId, linkKey, code.Access, expiresAt, ThisPcIsMachine: true));
                AppLog.Write($"Linked {request.LinkerLabel} to run tests on this PC.");
            }
            else
            {
                CodeProblem = "The link couldn't be saved on the server. Create a new link and try again.";
            }
            await RefreshListAsync();
        }
    }

    // As the target: one run at a time, in the background.
    private async Task ClaimAsync(TestMachineSettings settings)
    {
        var claimed = await _devices.ClaimRunAsync();
        if (claimed.Value is not { } run) return;

        // Before it goes in any path or name. One that isn't can't be told it's refused either, so
        // it's left to be lost.
        if (!MachineLinks.IsId(run.Id))
        {
            AppLog.Write("The server sent a test run whose id isn't one, so it was left alone.");
            return;
        }

        var runner = new TestRunner(_database, _devices, settings, run, AskToAllowAsync);
        Running = runner;
        Changed?.Invoke();
        AppLog.Write($"Started test run {run.Id} for {run.LinkerLabel}.");
        _ = FinishRunAsync(runner, run); // The loop carries on meanwhile, answering link requests and refreshing.
    }

    private async Task FinishRunAsync(TestRunner runner, TestRunInfo run)
    {
        try
        {
            var done = await runner.RunAsync();
            RememberRun(done);
            AppLog.Write($"Test run {run.Id} ended: {done.Verdict}.");
        }
        catch (Exception error)
        {
            // The runner ends its run itself whatever happens; this is only in case even that failed.
            AppLog.Write($"Test run {run.Id} stopped: {error.GetType().Name}: {error.Message}");
            try
            {
                await _devices.EndRunAsync(run.Id, "couldnt_run", null);
            }
            catch (Exception again)
            {
                AppLog.Write($"Test run {run.Id} couldn't be ended: {again.Message}");
            }
        }
        finally
        {
            OnWindow(() =>
            {
                Running = null;
                Changed?.Invoke();
            });
        }
    }

    // As the PC linking

    /// <summary>
    /// Links this PC to another with the code shown there, which never goes to the server: only
    /// proof of it does, with a key pair made for this one link. The target's answer is kept only
    /// once its confirmation checks out. Returns null once linked, or what went wrong, in T3's
    /// words where it has them.
    /// </summary>
    public async Task<string?> LinkAsync(TestMachineInfo machine, string typedCode, string access, CancellationToken cancel)
    {
        if (MachineLinks.CleanCode(typedCode) is not { } code) return "That isn't a pairing code. It's 12 letters and numbers, from that machine's Test machines settings.";
        if (Latest?.Me is not { } me) return "This PC could not reach the server.";
        if (machine.Install == me) return "That machine is this PC.";

        var nonce = MachineLinks.NewId();
        using var ephemeral = MachineLinks.NewEphemeral();
        var linkerKey = MachineLinks.PublicKey(ephemeral);
        var asked = await _devices.RequestLinkAsync(machine.Install, access, nonce, MachineLinks.Proof(code, me, machine.Install, access, nonce, linkerKey), linkerKey);
        if (asked.Value is not { } request)
        {
            return asked.Status switch
            {
                HttpStatusCode.TooManyRequests => "Too many tries that weren't approved. Wait a minute and try again.",
                HttpStatusCode.Conflict => $"{machine.Label} doesn't let other PCs run tests on it. Turn it on in its Settings › Test machines.",
                HttpStatusCode.NotFound => $"{machine.Label} isn't a test machine any more.",
                HttpStatusCode.UnprocessableEntity => "That machine is this PC.",
                HttpStatusCode.UpgradeRequired => "This PC and the server are on different versions. Update both, then link again.",
                _ => DescribeProblem(asked.Status) ?? "The server couldn't take the request.",
            };
        }

        // The target answers within a few seconds while it's showing a code. Two minutes is plenty.
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < until)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
            }
            catch (OperationCanceledException)
            {
                return "Cancelled.";
            }
            var answer = await _devices.LinkAnswerAsync(request.Id);
            if (answer.Status == HttpStatusCode.NotFound) break;
            if (answer.Value is not { Answer: { } said } got) continue;
            if (said != "accepted" || !MachineLinks.IsId(got.LinkId) || got.ExpiresAt is not { } expires || got.Key is not { } machineKey || got.Confirm is not { } confirm)
            {
                return "The peer did not approve the link. Check the pairing code and try again.";
            }

            // Only the target, with the code, could have made this, with the same key as here.
            var linkId = got.LinkId!;
            var expiresAt = (long)expires;
            var transcript = MachineLinks.Transcript(me, machine.Install, access, nonce, linkerKey, machineKey, linkId, expiresAt);
            var linkKey = MachineLinks.LinkKey(ephemeral, machineKey, transcript);
            var sensible = expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && expiresAt <= (DateTimeOffset.UtcNow + MachineLinks.LinkLifetime + TimeSpan.FromDays(1)).ToUnixTimeSeconds();
            if (linkKey is null || !sensible || !MachineLinks.ConfirmMatches(code, linkKey, transcript, confirm))
            {
                AppLog.Write($"A link to {machine.Label} was refused: its answer's confirmation didn't check out.");
                await _devices.RemoveLinkAsync(linkId);
                return $"The answer didn't come from {machine.Label} with that code, so the link wasn't kept. Create a new link there and try again.";
            }
            MachineLinks.SaveKey(_database, new MachineLinkKey(linkId, linkKey, access, expiresAt, ThisPcIsMachine: false));
            await RefreshAsync();
            return null;
        }
        return $"{machine.Label} did not answer. Check that its pairing code is still showing, and try again.";
    }

    /// <summary>Ends a link from either end. The server's copy is the only one, so it's final.</summary>
    public async Task<string?> ForgetAsync(MachineLinkInfo link)
    {
        var removed = await _devices.RemoveLinkAsync(link.Id);
        if (!removed.Worked && removed.Status != HttpStatusCode.NotFound) return DescribeProblem(removed.Status) ?? "Could not forget the link.";
        MachineLinks.ForgetKey(_database, link.Id);
        await RefreshAsync();
        return null;
    }

    /// <summary>
    /// Sends a project's tests to a machine this PC is linked to, at a commit origin has, signed with
    /// the link's key, under a run id and time made here. The repo and commands come from synced
    /// data, so when they differ from what this PC last signed for that machine, a person here sees
    /// them first (confirm), and nothing is sent without a yes. Returns the run's id, or what went wrong.
    /// </summary>
    public async Task<(string? RunId, string? Problem)> RunTestsAsync(MachineLinkInfo link, Project project, TestSetup setup, string commit, Func<TestApproval, Task<bool>> confirm)
    {
        if (MachineLinks.ReadKey(_database, link.Id) is not { ThisPcIsMachine: false } key) return (null, $"This PC has no key for its link to {link.MachineLabel}. Link it again.");
        if (link.Status == "expired" || key.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return (null, $"The link to {link.MachineLabel} expired. Link it again with a new pairing code from {link.MachineLabel}.");
        if (!setup.CanRun) return (null, "This project has no test command yet. Set one in Project settings › Tests.");

        var (prepare, test) = (setup.Prepare.Trim(), setup.Test.Trim());
        var commands = MachineLinks.CommandsHash(project.RepoUrl, prepare, test);
        if (!MachineLinks.SignedBefore(_database, link.Machine, project.Id, commands))
        {
            if (!await confirm(new TestApproval("", link.MachineLabel, project.Name, project.RepoUrl, commit, prepare, test, "", ""))) return (null, null);
            MachineLinks.RememberSigned(_database, link.Machine, project.Id, commands);
        }

        var runId = MachineLinks.NewId();
        var job = JsonSerializer.Serialize(new TestJob(TestJob.TestsKind, runId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), project.Id, project.Name, project.RepoUrl, commit.ToLowerInvariant(), prepare, test, setup.CouldntRunCode, setup.Tags ?? [], setup.TimeoutMinutes), TestJob.Json);
        var mac = MachineLinks.JobMac(key.Key, runId, link.Id, project.Id, commit, job);
        var sent = await _devices.EnqueueRunAsync(runId, link.Id, project.Id, commit.ToLowerInvariant(), job, mac);
        if (sent.Value is { } run) return (run.Id, null);
        return (null, sent.Status == HttpStatusCode.Conflict
            ? $"{link.MachineLabel} couldn't take it: the link may have ended, or ten runs are already waiting there."
            : DescribeProblem(sent.Status) ?? "The server couldn't take the run.");
    }

    /// <summary>Whether a machine offers every tag a project needs. Tags are only words that match, whatever case.</summary>
    public static bool HasTags(IEnumerable<string> offered, IEnumerable<string>? needed) =>
        (needed ?? []).All(tag => offered.Any(each => string.Equals(each, tag, StringComparison.OrdinalIgnoreCase)));
}
