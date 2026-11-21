using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

/// <summary>What a test PC tells the server about a run it's running: the lease, the log and the end.</summary>
public interface IRunServer
{
    Task<ServerAnswer<RunKeepGoing>> HeartbeatAsync(string run);

    Task<ServerAnswer<RunKeepGoing>> AppendLogAsync(string run, int seq, string text, bool heldBack = false);

    Task<ServerAnswer<bool>> EndRunAsync(string run, string status, int? exitCode, IReadOnlyList<CheckResult>? checks = null, bool heldBack = false);
}

public sealed partial class DeviceSession : IRunServer
{
}

/// <summary>
/// The rest of a run's report, kept on the test PC while the server turned it away for coming from
/// another release (#124): the log from the part numbered NextSeq on, and the verdict. Kept in the
/// local database, so it outlives the update that restarts the app, and sent once this PC is on the
/// server's release.
/// </summary>
public sealed record HeldReport(string RunId, int NextSeq, string Log, string Verdict, int? ExitCode, List<CheckResult> Checks, DateTimeOffset HeldAt, bool LogFull = false);

public sealed partial class TestRunner
{
    private const string HeldKey = "test_runs_held_back";

    /// <summary>A day after it was held back, the server no longer takes it, so neither is it kept.</summary>
    private static readonly TimeSpan HeldKept = TimeSpan.FromHours(24);

    public const string HeldBackNote = "The server was on another release and turned its report away, so this PC kept the rest of the log and the verdict, to send once it has updated.";

    private volatile bool _heldBack;

    // Said once in the log, which goes with the rest once this PC has updated.
    private void HeldBackBy(string what)
    {
        if (_heldBack) return;
        _heldBack = true;
        AppLog.Write($"Test run {run.Id}'s {what.Replace("its ", "")} was turned away by the server, which is on another release, so this PC keeps the rest of its report until it has updated.");
        Line($"The server is on another release and turned {what} away, so this PC keeps the rest of the log and the verdict, and sends them once it has updated.");
    }

    // The part that got no answer goes first, under its own number, then everything after it.
    private void HoldBack(string verdict, int? exitCode)
    {
        string log;
        lock (_pendingLock)
        {
            log = (_inFlight?.Text ?? "") + _pending;
            _pending.Clear();
        }
        var report = new HeldReport(run.Id, _inFlight?.Seq ?? _nextSeq, log, verdict, exitCode, _checks, DateTimeOffset.UtcNow, _truncated);
        _inFlight = null;
        KeepHeld(database, report);
        AppLog.Write($"Test run {run.Id} ended as {verdict}, and its report is kept until this PC has updated.");
    }

    /// <summary>Reports kept while the server was on another release, oldest first.</summary>
    public static List<HeldReport> HeldReports(LocalDatabase database)
    {
        if (database.GetSetting(HeldKey) is not { Length: > 0 } text) return [];
        try
        {
            return JsonSerializer.Deserialize<List<HeldReport>>(text, TestJob.Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void KeepHeld(LocalDatabase database, HeldReport report)
    {
        var reports = HeldReports(database);
        reports.RemoveAll(each => each.RunId == report.RunId);
        reports.Add(report);
        database.SetSetting(HeldKey, JsonSerializer.Serialize(reports, TestJob.Json));
    }

    private static void ForgetHeld(LocalDatabase database, string runId)
    {
        var reports = HeldReports(database);
        if (reports.RemoveAll(each => each.RunId == runId) == 0) return;
        database.SetSetting(HeldKey, reports.Count == 0 ? "" : JsonSerializer.Serialize(reports, TestJob.Json));
    }

    /// <summary>
    /// Sends the reports this PC kept while the server was on another release: each one's log from
    /// where the server left off, then its end, both marked held back, so the server still takes
    /// them for a run it called lost meanwhile. Stops at the first the server still turns away for
    /// its release, or can't be reached for, and goes on from there next time. What the server no
    /// longer takes (a day on, or a run it never lost) is let go, saying so in the log.
    /// </summary>
    public static async Task SendHeldReportsAsync(LocalDatabase database, IRunServer server)
    {
        foreach (var held in HeldReports(database))
        {
            if (DateTimeOffset.UtcNow - held.HeldAt > HeldKept)
            {
                AppLog.Write($"Test run {held.RunId}'s held-back report was over a day old, so it was let go.");
                ForgetHeld(database, held.RunId);
                continue;
            }
            if (!await SendHeldAsync(database, server, held)) return;
        }
    }

    // Whether it's done with, sent or let go; false while the server can't take it yet.
    private static async Task<bool> SendHeldAsync(LocalDatabase database, IRunServer server, HeldReport held)
    {
        var rest = new StringBuilder(held.Log);
        var seq = held.NextSeq;
        var logFull = held.LogFull;
        while (rest.Length > 0 && !logFull)
        {
            var text = TakeChunk(rest);
            var sent = await server.AppendLogAsync(held.RunId, seq, text, heldBack: true);
            if (sent.Worked)
            {
                seq++;
                logFull = sent.Value?.Truncated == true;
                KeepHeld(database, held with { NextSeq = seq, Log = rest.ToString(), LogFull = logFull });
                continue;
            }
            if (sent.Status is null || sent.VersionsDiffer || sent.Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)sent.Status >= 500) return false;
            if (sent.Status == HttpStatusCode.Gone)
            {
                AppLog.Write($"The server no longer takes test run {held.RunId}'s held-back report, so it was let go.");
                ForgetHeld(database, held.RunId);
                return true;
            }
            AppLog.Write($"Test run {held.RunId}'s held-back log was refused by the server ({(int)sent.Status}), so only its end is sent.");
            logFull = true;
        }

        var checks = held.Checks;
        for (var tries = 0; tries < 2; tries++)
        {
            var ended = await server.EndRunAsync(held.RunId, held.Verdict, held.ExitCode, checks, heldBack: true);
            if (ended.Worked)
            {
                AppLog.Write($"Sent test run {held.RunId}'s held-back report: {held.Verdict}.");
                break;
            }
            if (ended.Status is null || ended.VersionsDiffer || ended.Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)ended.Status >= 500)
            {
                KeepHeld(database, held with { NextSeq = seq, Log = "", LogFull = true });
                return false;
            }
            // As at a run's end, checks the server won't take never keep it from ending.
            if (ended.Status != HttpStatusCode.Gone && checks.Count > 0)
            {
                checks = [];
                continue;
            }
            AppLog.Write($"The server didn't take test run {held.RunId}'s held-back end ({(int)ended.Status}), so it was let go.");
            break;
        }
        ForgetHeld(database, held.RunId);
        return true;
    }
}

#if DEBUG
/// <summary>
/// Headless checks only: a server that updates to a new release partway through a run. It takes
/// the log's first parts, then turns everything about the run away for coming from the release
/// before, as a server would that didn't let a run finish on the release it started on, until the
/// test PC has updated. What it took, a line each.
/// </summary>
internal sealed class MadeUpRunServer(int releasedAfterParts) : IRunServer
{
    private bool _released;

    /// <summary>The test PC is on this server's release now.</summary>
    public bool Updated { get; set; }

    public List<string> Took { get; } = [];

    public string Log { get; private set; } = "";

    private static ServerAnswer<T> Answer<T>(T value, HttpStatusCode status) => new(status == HttpStatusCode.OK ? value : default, status);

    public Task<ServerAnswer<RunKeepGoing>> HeartbeatAsync(string run) =>
        Task.FromResult(Answer(new RunKeepGoing(false, false), _released && !Updated ? HttpStatusCode.UpgradeRequired : HttpStatusCode.OK));

    public Task<ServerAnswer<RunKeepGoing>> AppendLogAsync(string run, int seq, string text, bool heldBack = false)
    {
        if (seq >= releasedAfterParts) _released = true;
        if (_released && !Updated) return Task.FromResult(Answer(new RunKeepGoing(false, false), HttpStatusCode.UpgradeRequired));
        Took.Add($"Part {seq}{(heldBack ? ", held back" : "")}");
        Log += text;
        return Task.FromResult(Answer(new RunKeepGoing(false, false), HttpStatusCode.OK));
    }

    public Task<ServerAnswer<bool>> EndRunAsync(string run, string status, int? exitCode, IReadOnlyList<CheckResult>? checks = null, bool heldBack = false)
    {
        _released = true; // Before the run ended, whatever came first.
        if (!Updated) return Task.FromResult(Answer(false, HttpStatusCode.UpgradeRequired));
        Took.Add($"Ended {status}{(exitCode is { } code ? $" (exit code {code})" : "")}{(heldBack ? ", held back" : "")}");
        return Task.FromResult(Answer(true, HttpStatusCode.OK));
    }
}
#endif
