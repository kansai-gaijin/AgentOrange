using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// A link this PC is making to another, shown with what's happening until it's made, or with why
/// it wasn't until it's dismissed or tried again (#95).
/// </summary>
public sealed class Linking(TestMachineInfo machine, string access)
{
    public TestMachineInfo Machine { get; } = machine;

    public string Access { get; } = access;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>What's happening now, in words.</summary>
    public string Step { get; internal set; } = "Sending the request…";

    /// <summary>Why it didn't link, once it didn't.</summary>
    public string? Problem { get; internal set; }

    public bool Failed => Problem is not null;

    internal CancellationTokenSource Cancel { get; } = new();
}

/// <summary>
/// Test machines on this PC (#93): what it offers other PCs, the links to and from it and those
/// being made, and the background loop that answers link requests and runs what it's sent. The
/// loop only answers and runs in the window that syncs, so two windows never take the same work,
/// and only while this PC accepts runs, which is its consent to being linked: it answers by
/// itself, with nobody needing to be here (#95).
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

    private readonly List<Linking> _linkings = [];

    /// <summary>Links this PC is making, or failed to make and hasn't been told to forget.</summary>
    public IReadOnlyList<Linking> Linkings => _linkings;

    /// <summary>The last link made from here, said once in Linked machines.</summary>
    public string? JustLinked { get; set; }

    public TestRunner? Running { get; private set; }

    /// <summary>The test clone being removed, if one is. No run is taken meanwhile.</summary>
    public string? RemovingClone { get; private set; }

    private bool _claiming;

    /// <summary>
    /// Removes one of this PC's test clones (#93), never while a run is going on here or being
    /// taken, and takes no run until it's done. Null once it's gone, or why it isn't.
    /// </summary>
    public async Task<string?> RemoveCloneAsync(string folder)
    {
        if (Running is not null || _claiming) return "A test run is going on here. Remove it once that's done.";
        if (RemovingClone is not null) return "Another test clone is being removed. Remove this one once that's done.";
        RemovingClone = folder;
        Changed?.Invoke();
        try
        {
            var workFolder = Settings.WorkFolder;
            var problem = await Task.Run(() => TestClones.Remove(folder, workFolder));
            AppLog.Write(problem is null ? $"Removed the test clone {folder}." : $"The test clone {folder} wasn't all removed: {problem}");
            return problem;
        }
        finally
        {
            RemovingClone = null;
            Changed?.Invoke();
        }
    }

    // Runs keep their awaits on the window's thread, but the UI only ever hears from it there.
    private void OnWindow(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Invoke(action);
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

    public async Task RefreshAsync()
    {
        _refreshedAt = default;
        await TickAsync();
    }

    private bool CanTalkToServer => _server.State == ServerState.Connected && _devices.IsPaired;

    private async Task TickAsync()
    {
#if DEBUG
        if (_madeUp) return;
#endif
        if (_ticking) return;
        _ticking = true;
        try
        {
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

            // Quicker while this PC is making a link, so it shows as soon as it's made.
            if (DateTimeOffset.UtcNow - _refreshedAt >= (_linkings.Any(each => !each.Failed) ? ClaimEvery : RefreshEvery)) await RefreshListAsync();

            var settings = Settings;

            // Answered by itself while this PC takes runs, in the window that does background work.
            if (settings.Accepting && _hasSyncTurn()) await AnswerLinkRequestsAsync();

            // What a run kept back while the server was on another release, once this PC has updated (#124).
            if (_hasSyncTurn() && Running is null && TestRunner.HeldReports(_database).Count > 0)
            {
                await TestRunner.SendHeldReportsAsync(_database, _devices);
                Changed?.Invoke();
            }

            // Not while a clone is being removed, which a run would then be checking out into.
            if (settings.Accepting && Running is null && RemovingClone is null && _hasSyncTurn() && DateTimeOffset.UtcNow - _claimedAt >= ClaimEvery)
            {
                _claimedAt = DateTimeOffset.UtcNow;
                _claiming = true;
                try
                {
                    await ClaimAsync(settings);
                }
                finally
                {
                    _claiming = false;
                }
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

            // Renewals, unattended, in the window that does background work: as the target, each one
            // asked for is answered; as the PC linking, an answer is checked, and a link with under a
            // week left is renewed while the target is online.
            if (_hasSyncTurn())
            {
                foreach (var link in latest.Links.Where(link => link.Machine == latest.Me)) await AnswerRenewalAsync(link);
                foreach (var link in latest.Links.Where(link => link.Linker == latest.Me))
                {
                    TakeRenewal(link);
                    var left = DateTimeOffset.FromUnixTimeSeconds((long)link.ExpiresAt) - DateTimeOffset.UtcNow;
                    if (link.Status is "reachable" or "busy" && left > TimeSpan.Zero && left < MachineLinks.RenewWithin && !RenewalPending(link)) await AskRenewalAsync(link);
                }
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

    // As the target, while it takes runs: each of the owner's PCs that asks is linked, for the one
    // access level offered so far, with a key pair made for this one link and an expiry of this
    // PC's own. Nobody needs to be here.
    private async Task AnswerLinkRequestsAsync()
    {
        if (DateTimeOffset.UtcNow - _askedForRequestsAt < TimeSpan.FromSeconds(2)) return;
        _askedForRequestsAt = DateTimeOffset.UtcNow;
        var pending = await _devices.PendingLinkRequestsAsync();
        if (pending.Value is not { Count: > 0 } requests || Latest?.Me is not { } me) return;

        foreach (var request in requests)
        {
            if (request.Access != "tests")
            {
                await _devices.RejectLinkAsync(request.Id, "access");
                continue;
            }
            if (MachineLinks.Accept(request, me) is not { } accepting)
            {
                await _devices.RejectLinkAsync(request.Id, "unreadable");
                continue;
            }
            var accepted = await _devices.AcceptLinkAsync(request.Id, accepting.LinkId, accepting.MachineKey, accepting.ExpiresAt, accepting.Confirm);
            if (accepted.Worked)
            {
                MachineLinks.SaveKey(_database, new MachineLinkKey(accepting.LinkId, accepting.LinkKey, request.Access, accepting.ExpiresAt, ThisPcIsMachine: true));
                AppLog.Write($"Linked {request.LinkerLabel} to run tests on this PC.");
            }
            else
            {
                AppLog.Write($"A link from {request.LinkerLabel} couldn't be saved on the server, so it wasn't made.");
            }
            CryptographicOperations.ZeroMemory(accepting.LinkKey);
        }
        await RefreshListAsync();
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

        var runner = new TestRunner(_database, _devices, settings, run);
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
    /// Starts linking this PC to another of the owner's PCs, in the background, so it carries on
    /// with the dialog closed and shows in Linked machines meanwhile. Linking to one already being
    /// linked gives that one; one that failed starts again.
    /// </summary>
    public Linking StartLink(TestMachineInfo machine, string access = "tests")
    {
        if (_linkings.FirstOrDefault(each => each.Machine.Install == machine.Install) is { } already)
        {
            if (!already.Failed) return already;
            _linkings.Remove(already);
        }
        var linking = new Linking(machine, access);
        _linkings.Add(linking);
        JustLinked = null;
        Changed?.Invoke();
        _ = RunLinkingAsync(linking);
        return linking;
    }

    /// <summary>Stops a link being made, or forgets one that failed.</summary>
    public void StopLinking(Linking linking)
    {
        linking.Cancel.Cancel();
        _linkings.Remove(linking);
        Changed?.Invoke();
    }

    private async Task RunLinkingAsync(Linking linking)
    {
        string? problem;
        try
        {
            problem = await LinkAsync(linking);
        }
        catch (Exception error)
        {
            AppLog.Write($"Linking to {linking.Machine.Label} stopped: {error.GetType().Name}: {error.Message}");
            problem = $"Something went wrong on this PC: {error.Message}";
        }
        OnWindow(() =>
        {
            if (!_linkings.Contains(linking)) return; // Stopped meanwhile.
            if (problem is null)
            {
                _linkings.Remove(linking);
                JustLinked = $"Linked {linking.Machine.Label}";
            }
            else
            {
                linking.Problem = problem;
            }
            Changed?.Invoke();
        });
    }

    private void Say(Linking linking, string step)
    {
        if (linking.Step == step) return;
        linking.Step = step;
        Changed?.Invoke();
    }

    // What the PC linking waits on, said as the target's state changes.
    private string Waiting(TestMachineInfo machine)
    {
        var now = Latest?.Machines.FirstOrDefault(each => each.Install == machine.Install) ?? machine;
        return now.Status switch
        {
            "other_release" => $"Waiting for {now.Label}. It's on another release of Agent Orange, so it can't answer until it updates.",
            "unreachable" => $"Waiting for {now.Label}. It isn't connected right now, and answers by itself once Agent Orange runs there and it's online.",
            _ => $"Waiting for {now.Label} to answer. It answers by itself in a few seconds.",
        };
    }

    /// <summary>
    /// Links this PC to another through the server, with a key pair made for this one link, and
    /// keeps the target's answer only once its confirmation checks out. Null once linked, or what
    /// went wrong, in words a person can act on.
    /// </summary>
    private async Task<string?> LinkAsync(Linking linking)
    {
        var machine = linking.Machine;
        var access = linking.Access;
        var cancel = linking.Cancel.Token;
        if (Latest?.Me is not { } me) return Problem ?? "This PC could not reach the server.";
        if (machine.Install == me) return "That machine is this PC.";

        var nonce = MachineLinks.NewId();
        using var ephemeral = MachineLinks.NewEphemeral();
        var asked = await _devices.RequestLinkAsync(machine.Install, access, nonce, MachineLinks.PublicKey(ephemeral));
        if (asked.Value is not { } request)
        {
            return asked.Status switch
            {
                HttpStatusCode.TooManyRequests => "Too many link requests at once, or turned down in the last minute. Wait a minute and try again.",
                HttpStatusCode.Conflict => MachineLinks.RefusedText("not_accepting", machine.Label),
                HttpStatusCode.NotFound => $"{machine.Label} isn't a test machine any more.",
                HttpStatusCode.UnprocessableEntity => "That machine is this PC.",
                HttpStatusCode.UpgradeRequired => "This PC and the server are on different versions. Update both, then link again.",
                _ => DescribeProblem(asked.Status) ?? "The server couldn't take the request.",
            };
        }

        // The target answers within a few seconds while Agent Orange runs there. One that's away
        // gets as long as the server keeps the request.
        var until = DateTimeOffset.UtcNow + MachineLinks.AnswerWait;
        while (DateTimeOffset.UtcNow < until)
        {
            Say(linking, Waiting(machine));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
            }
            catch (OperationCanceledException)
            {
                await WithdrawAsync(request.Id);
                return "Cancelled.";
            }
            var answer = await _devices.LinkAnswerAsync(request.Id);
            if (answer.Status == HttpStatusCode.NotFound) break;
            if (answer.Value is not { Answer: { } said } got) continue;
            if (said != "accepted") return MachineLinks.RefusedText(got.Reason, machine.Label);

            Say(linking, $"Checking {machine.Label}'s answer…");
            if (MachineLinks.TakeAnswer(ephemeral, me, machine.Install, access, nonce, got) is not { } linkKey)
            {
                AppLog.Write($"A link to {machine.Label} was refused: its answer's confirmation didn't check out.");
                if (MachineLinks.IsId(got.LinkId)) await _devices.RemoveLinkAsync(got.LinkId!);
                return $"The answer from {machine.Label} didn't check out, so the link wasn't kept. Link again.";
            }
            MachineLinks.SaveKey(_database, new MachineLinkKey(got.LinkId!, linkKey, access, (long)got.ExpiresAt!, ThisPcIsMachine: false));
            CryptographicOperations.ZeroMemory(linkKey);
            await RefreshAsync();
            return null;
        }
        await WithdrawAsync(request.Id);
        return $"{machine.Label} didn't answer in {MachineLinks.AnswerWait.TotalMinutes:0} minutes. It answers by itself while Agent Orange runs there and it's online, with Let other PCs run tests here on. Link again once it is.";
    }

    // Given up on: the target mustn't link later to a key this PC no longer has. When it already
    // has, that link is ended, as this PC won't keep it.
    private async Task WithdrawAsync(string request)
    {
        var withdrawn = await _devices.WithdrawLinkAsync(request);
        if (withdrawn.Status != HttpStatusCode.NotFound) return;
        if ((await _devices.LinkAnswerAsync(request)).Value is { Answer: "accepted", LinkId: { } linkId } && MachineLinks.IsId(linkId)) await _devices.RemoveLinkAsync(linkId);
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
    /// What a commit in a project's folder would send, the one checked out unless another is named:
    /// its repo, from the folder's own origin, and the test setup it has committed. A problem, in
    /// words a person can act on, when it can't be sent: no origin, not pushed, or no valid
    /// .agent-orange/tests.json at that commit.
    /// </summary>
    public static async Task<(string? RepoUrl, string? Commit, TestSetup? Setup, string? Problem)> WhatToSendAsync(string folder, string? named = null)
    {
        var (commit, _, pushed, problem) = await GitTools.PushedHeadAsync(folder, named);
        if (problem is not null) return (null, null, null, problem);
        var (origin, originProblem) = await GitTools.OriginForTestsAsync(folder);
        if (origin is null) return (null, null, null, originProblem);
        var which = named is null ? "the commit checked out here" : $"commit {commit[..Math.Min(12, commit.Length)]}";
        if (!pushed) return (null, null, null, $"Push {which} first. A test machine fetches it from origin, and origin doesn't have {commit[..Math.Min(12, commit.Length)]} yet.");
        if (await GitTools.FileAtCommitAsync(folder, commit, TestsFile.RelativePath) is not { } text)
        {
            return (null, null, null, $"{char.ToUpperInvariant(which[0])}{which[1..]} has no {TestsFile.RelativePath}, which says how the tests run. Set it up in Project settings › Tests, then commit and push it.");
        }
        var (setup, invalid) = TestsFile.Parse(text);
        return setup is null ? (null, null, null, $"{invalid} Fix it, then commit and push it.") : (origin, commit, setup, null);
    }

    /// <summary>
    /// A run this PC sent that's waiting or running on that link, for that project and commit, and
    /// the same filter or none, which sending again would only repeat (#118).
    /// </summary>
    public TestRunInfo? SameRun(IEnumerable<TestRunInfo> runs, string linkId, string projectId, string? commit, string? filter = null) =>
        commit is null ? null : runs.FirstOrDefault(run => !run.Finished && run.Linker == Latest?.Me && run.LinkId == linkId && run.ProjectId == projectId && string.Equals(run.Commit, commit, StringComparison.OrdinalIgnoreCase) && run.Filter == filter);

    /// <summary>
    /// Why a run of this setup can't be asked to test only what the filter names, or null when it can:
    /// the filter has to be one plain word, and the repo has to say how to pass one.
    /// </summary>
    public static string? FilterProblem(TestSetup setup, string? filter) =>
        filter is null ? null
        : TestsFile.FilterProblem(filter) is { } unsafeFilter ? unsafeFilter
        : setup.TestWithFilter is null ? $"{TestsFile.RelativePath} at that commit has no \"testWithFilter\", the test command with {TestsFile.FilterPlaceholder} where a filter goes, so its tests can't be asked to run only some. Add it in Project settings › Tests, then commit and push it."
        : null;

    // Links a run is being sent over right now, so a second click doesn't send it twice.
    private readonly HashSet<string> _sending = [];

    /// <summary>
    /// Sends a project's tests to a machine this PC is linked to: which commit of which repo, signed
    /// with the link's key under a run id and time made here. Never the commands, which the target
    /// reads from the repo at that commit. Returns the run's id, or what went wrong. When that
    /// machine is already testing that commit, or has it waiting, nothing is sent and Existing is
    /// that run. An agent's run (#117) goes the same way, with the same checks here and on the
    /// target, and only says in the signed job that an agent started it.
    /// </summary>
    public async Task<(string? RunId, string? Problem, TestRunInfo? Existing)> RunTestsAsync(MachineLinkInfo link, Project project, string folder, string? commit = null, string? startedBy = null, string? filter = null)
    {
        var sending = $"{link.Id}|{project.Id}";
        if (!_sending.Add(sending)) return (null, $"The tests are already being sent to {link.MachineLabel}.", null);
        try
        {
            return await SendRunAsync(link, project, folder, commit, startedBy, filter);
        }
        finally
        {
            _sending.Remove(sending);
        }
    }

    private async Task<(string? RunId, string? Problem, TestRunInfo? Existing)> SendRunAsync(MachineLinkInfo link, Project project, string folder, string? named, string? startedBy, string? filter)
    {
        if (MachineLinks.ReadKey(_database, link.Id) is not { ThisPcIsMachine: false } key) return (null, $"This PC has no key for its link to {link.MachineLabel}. Link it again.", null);
        if (link.Status == "expired" || key.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return (null, $"The link to {link.MachineLabel} expired. Link it again in Settings › Test machines.", null);
        var (repoUrl, commit, setup, problem) = await WhatToSendAsync(folder, named);
        if (problem is not null || repoUrl is null || commit is null || setup is null) return (null, problem, null);
        var missing = setup.Tags.Where(tag => !HasTags(link.MachineTags, [tag])).ToList();
        if (missing.Count > 0) return (null, $"{link.MachineLabel} doesn't offer {string.Join(", ", missing)}, which {TestsFile.RelativePath} asks for.", null);
        if (FilterProblem(setup, filter) is { } badFilter) return (null, badFilter, null);

        // Asked again now, as the list may be from before another click sent it.
        var runs = await _devices.RunsAsync(project.Id);
        if (SameRun(runs.Value ?? [], link.Id, project.Id, commit, filter) is { } same) return (null, null, same);

        // The filter is signed with the rest, and the target checks it again before putting it in the repo's command.
        var runId = MachineLinks.NewId();
        var job = JsonSerializer.Serialize(new TestJob(TestJob.TestsKind, runId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), project.Id, project.Name, repoUrl, commit.ToLowerInvariant(), "tests", setup.Tags, startedBy, filter), TestJob.Json);
        var mac = MachineLinks.JobMac(key.Key, runId, link.Id, project.Id, commit, job);
        var sent = await _devices.EnqueueRunAsync(runId, link.Id, project.Id, commit.ToLowerInvariant(), job, mac);
        if (sent.Value is { } run) return (run.Id, null, null);
        return (null, sent.Status == HttpStatusCode.Conflict
            ? $"{link.MachineLabel} couldn't take it: the link may have ended, or ten runs are already waiting there."
            : DescribeProblem(sent.Status) ?? "The server couldn't take the run.", null);
    }

    // Renewing a link, by itself. Only a link that's still good can be renewed; an expired or
    // revoked one is linked again, which makes a new key.

    // The renewals this PC asked for and hasn't had an answer to, by link: the nonce and when.
    private readonly Dictionary<string, (string Nonce, DateTimeOffset At)> _renewing = [];

    // Renewal requests this PC turned down, so a bad one left on the server is looked at, and logged, once.
    private readonly HashSet<string> _turnedDown = [];

    private bool RenewalPending(MachineLinkInfo link) =>
        _renewing.TryGetValue(link.Id, out var asked) && DateTimeOffset.UtcNow - asked.At < MachineLinks.RenewMaxAge;

    /// <summary>As the PC linking: asks the target to renew. The answer comes with the list.</summary>
    private async Task<string?> AskRenewalAsync(MachineLinkInfo link)
    {
        if (MachineLinks.ReadKey(_database, link.Id) is not { ThisPcIsMachine: false } key) return $"This PC has no key for its link to {link.MachineLabel}. Link it again.";
        var nonce = MachineLinks.NewId();
        var sentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var wanted = (DateTimeOffset.UtcNow + MachineLinks.LinkLifetime).ToUnixTimeSeconds();
        var asked = await _devices.RenewLinkAsync(link.Id, nonce, sentAt, wanted, MachineLinks.RenewMac(key.Key, link.Id, nonce, sentAt, wanted));
        if (!asked.Worked)
        {
            return asked.Status == HttpStatusCode.NotFound
                ? $"The link to {link.MachineLabel} has expired or was revoked. Link it again in Settings › Test machines."
                : DescribeProblem(asked.Status) ?? "The server couldn't take the renewal.";
        }
        _renewing[link.Id] = (nonce, DateTimeOffset.UtcNow);
        AppLog.Write($"Asked {link.MachineLabel} to renew its link.");
        return null;
    }

    /// <summary>As the PC linking: keeps the target's answer to this PC's renewal, once its MAC checks out.</summary>
    private bool TakeRenewal(MachineLinkInfo link)
    {
        if (link.Renewed is not { } answer || !_renewing.TryGetValue(link.Id, out var asked) || answer.Nonce != asked.Nonce) return false;
        if (MachineLinks.ReadKey(_database, link.Id) is not { ThisPcIsMachine: false } key) return false;
        _renewing.Remove(link.Id);
        var sensible = answer.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && answer.ExpiresAt <= (DateTimeOffset.UtcNow + MachineLinks.LinkLifetime + TimeSpan.FromDays(1)).ToUnixTimeSeconds();
        if (!sensible || !MachineLinks.MacMatches(MachineLinks.RenewedMac(key.Key, link.Id, answer.Nonce, answer.ExpiresAt), answer.Mac))
        {
            AppLog.Write($"An answer to renewing the link to {link.MachineLabel} didn't check out, so it was ignored.");
            return false;
        }
        MachineLinks.SaveKey(_database, key with { ExpiresAt = answer.ExpiresAt });
        AppLog.Write($"The link to {link.MachineLabel} was renewed.");
        return true;
    }

    /// <summary>As the target: answers a renewal the PC linking asked for, once its MAC checks out, with this PC's own new expiry.</summary>
    private async Task AnswerRenewalAsync(MachineLinkInfo link)
    {
        if (link.Renewal is not { } asked || link.Renewed?.Nonce == asked.Nonce || _turnedDown.Contains(asked.Nonce)) return;
        if (MachineLinks.ReadKey(_database, link.Id) is not { ThisPcIsMachine: true } key) return;
        var now = DateTimeOffset.UtcNow;
        if (key.ExpiresAt <= now.ToUnixTimeSeconds()) return; // Expired here: only linking again links them.
        var age = now - DateTimeOffset.FromUnixTimeSeconds(asked.SentAt);
        if (age > MachineLinks.RenewMaxAge || age < -MachineLinks.RenewMaxAge || !MachineLinks.IsId(asked.Nonce)) return;
        if (!MachineLinks.MacMatches(MachineLinks.RenewMac(key.Key, link.Id, asked.Nonce, asked.SentAt, asked.ExpiresAt), asked.Mac))
        {
            _turnedDown.Add(asked.Nonce);
            AppLog.Write($"A request to renew the link from {link.LinkerLabel} didn't check out, so it was ignored.");
            return;
        }

        // Never longer than this PC's own 30 days from now, whatever was asked.
        var expiresAt = Math.Min(asked.ExpiresAt, (now + MachineLinks.LinkLifetime).ToUnixTimeSeconds());
        var answered = await _devices.AnswerRenewalAsync(link.Id, asked.Nonce, expiresAt, MachineLinks.RenewedMac(key.Key, link.Id, asked.Nonce, expiresAt));
        if (!answered.Worked) return;
        MachineLinks.SaveKey(_database, key with { ExpiresAt = expiresAt });
        AppLog.Write($"Renewed the link from {link.LinkerLabel}.");
    }

#if DEBUG
    // Made up for a check, which the server (there being none) mustn't then say otherwise about.
    private bool _madeUp;

    /// <summary>Headless checks only: a made-up list of machines and links, as if the server had sent it.</summary>
    internal void ShowForChecks(MachinesInfo latest)
    {
        _madeUp = true;
        Latest = latest;
        Problem = null;
        Changed?.Invoke();
    }

    /// <summary>Headless checks only: a link being made, at that step or failed, as if the server were slow to answer.</summary>
    internal void LinkingForChecks(TestMachineInfo machine, string? step, string? problem)
    {
        _linkings.RemoveAll(each => each.Machine.Install == machine.Install);
        var linking = new Linking(machine, "tests") { Problem = problem };
        if (step is not null) linking.Step = step;
        _linkings.Add(linking);
        Changed?.Invoke();
    }
#endif

    /// <summary>Whether a machine offers every tag a project needs. Tags are only words that match, whatever case.</summary>
    public static bool HasTags(IEnumerable<string> offered, IEnumerable<string>? needed) =>
        (needed ?? []).All(tag => offered.Any(each => string.Equals(each, tag, StringComparison.OrdinalIgnoreCase)));
}
