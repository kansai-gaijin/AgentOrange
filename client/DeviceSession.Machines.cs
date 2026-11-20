using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentOrange;

/// <summary>A PC that lets others run tests on it, as the server knows it. Install is its hash, which links and proofs name.</summary>
public sealed record TestMachineInfo(string Install, string Label, List<string> Tags, bool Accepting, string Status, double LastSeenAt);

/// <summary>
/// A link from one PC (the linker) to another (the machine), seen from either end, with the
/// machine's status, and the last renewal asked for and answered, each MACed with the link's key.
/// </summary>
public sealed record MachineLinkInfo(string Id, string Machine, string MachineLabel, List<string> MachineTags, string Linker, string LinkerLabel, string Access, double LinkedAt, double ExpiresAt, string Status, double LastSeenAt, LinkRenewal? Renewal = null, LinkRenewed? Renewed = null);

public sealed record LinkRenewal(string Nonce, long SentAt, long ExpiresAt, string Mac);

public sealed record LinkRenewed(string Nonce, long ExpiresAt, string Mac);

/// <summary>Me is this PC's install hash, as other PCs know it.</summary>
public sealed record MachinesInfo(string Me, TestMachineInfo? ThisMachine, List<TestMachineInfo> Machines, List<MachineLinkInfo> Links);

/// <summary>Key is the PC linking's ephemeral public key, which the proof covers.</summary>
public sealed record PendingLinkRequest(string Id, string Linker, string LinkerLabel, string Access, string Nonce, string Proof, string Key);

/// <summary>Key, ExpiresAt and Confirm are the target's, which the PC linking checks before keeping the link.</summary>
public sealed record LinkAnswer(string? Answer, string? LinkId, double? ExpiresAt, string? Key, string? Confirm);

/// <summary>
/// A test run as the server keeps it. Job is the text the linker signed, which only the PCs read.
/// The checks are counted from the results file the repo names, when it names one (#105).
/// </summary>
public sealed record TestRunInfo(
    string Id, string LinkId, string Machine, string MachineLabel, string Linker, string LinkerLabel,
    string ProjectId, string Commit, string Job, string Mac, string Status, int? ExitCode, bool CancelRequested,
    double CreatedAt, double? StartedAt, double? EndedAt, string? LinkAccess, double? LinkExpiresAt,
    int ChecksPassed = 0, int ChecksFailed = 0, int ChecksSkipped = 0)
{
    public bool Finished => Status is not ("queued" or "running");

    /// <summary>Started by an agent with run_tests, as the signed job says (#117), rather than by a person.</summary>
    public bool ByAgent => TestJob.StartedByIn(Job) == TestJob.ByAgent;

    public int Checks => ChecksPassed + ChecksFailed + ChecksSkipped;
}

public sealed record RunLogLine(int Seq, string Text);

public sealed record TestRunWithLog(
    string Id, string LinkId, string Machine, string MachineLabel, string Linker, string LinkerLabel,
    string ProjectId, string Commit, string Job, string Mac, string Status, int? ExitCode, bool CancelRequested,
    double CreatedAt, double? StartedAt, double? EndedAt, string? LinkAccess, double? LinkExpiresAt, List<RunLogLine> Log,
    int ChecksPassed = 0, int ChecksFailed = 0, int ChecksSkipped = 0)
{
    public TestRunInfo Run => new(Id, LinkId, Machine, MachineLabel, Linker, LinkerLabel, ProjectId, Commit, Job, Mac, Status, ExitCode, CancelRequested, CreatedAt, StartedAt, EndedAt, LinkAccess, LinkExpiresAt, ChecksPassed, ChecksFailed, ChecksSkipped);
}

/// <summary>Whether to keep going with a run, as the server says.</summary>
public sealed record RunKeepGoing(bool Cancel, bool Truncated);

/// <summary>
/// An answer from the server about test machines. Status is null when there was no answer, such
/// as with no connection. Value is set only for a success that carried one.
/// </summary>
public readonly record struct ServerAnswer<T>(T? Value, HttpStatusCode? Status)
{
    public bool Worked => Status is { } status && (int)status is >= 200 and < 300;

    /// <summary>This PC and the server are on different releases, which test machines need to match.</summary>
    public bool VersionsDiffer => Status == HttpStatusCode.UpgradeRequired;

    /// <summary>This PC's login is from before pairing said which PC it was.</summary>
    public bool NeedsPairingAgain => Status == HttpStatusCode.PreconditionFailed;
}

/// <summary>Test machines: links between this PC and others, and the test runs sent over them (#93).</summary>
public sealed partial class DeviceSession
{
    public Task<ServerAnswer<MachinesInfo>> MachinesAsync() => AskAsync<MachinesInfo>(HttpMethod.Get, "/v1/machines");

    public Task<ServerAnswer<bool>> PublishMachineAsync(string label, IReadOnlyList<string> tags, bool accepting) =>
        AskAsync<bool>(HttpMethod.Put, "/v1/machines/me", new { label, tags, accepting });

    public Task<ServerAnswer<IdAnswer>> RequestLinkAsync(string machine, string access, string nonce, string proof, string key) =>
        AskAsync<IdAnswer>(HttpMethod.Post, $"/v1/machines/{Uri.EscapeDataString(machine)}/link-requests", new { access, nonce, proof, key });

    public Task<ServerAnswer<List<PendingLinkRequest>>> PendingLinkRequestsAsync() =>
        AskAsync<List<PendingLinkRequest>>(HttpMethod.Get, "/v1/machines/me/link-requests");

    public Task<ServerAnswer<LinkAnswer>> LinkAnswerAsync(string request) =>
        AskAsync<LinkAnswer>(HttpMethod.Get, $"/v1/link-requests/{Uri.EscapeDataString(request)}");

    public Task<ServerAnswer<ExpiresAnswer>> AcceptLinkAsync(string request, string linkId, string key, long expiresAt, string confirm) =>
        AskAsync<ExpiresAnswer>(HttpMethod.Post, $"/v1/link-requests/{Uri.EscapeDataString(request)}/accept", new { id = linkId, key, expiresAt, confirm });

    public Task<ServerAnswer<bool>> RejectLinkAsync(string request) =>
        AskAsync<bool>(HttpMethod.Post, $"/v1/link-requests/{Uri.EscapeDataString(request)}/reject");

    public Task<ServerAnswer<bool>> RemoveLinkAsync(string link) =>
        AskAsync<bool>(HttpMethod.Delete, $"/v1/links/{Uri.EscapeDataString(link)}");

    public Task<ServerAnswer<bool>> RenewLinkAsync(string link, string nonce, long sentAt, long expiresAt, string mac) =>
        AskAsync<bool>(HttpMethod.Post, $"/v1/links/{Uri.EscapeDataString(link)}/renew", new { nonce, sentAt, expiresAt, mac });

    public Task<ServerAnswer<bool>> AnswerRenewalAsync(string link, string nonce, long expiresAt, string mac) =>
        AskAsync<bool>(HttpMethod.Post, $"/v1/links/{Uri.EscapeDataString(link)}/renewed", new { nonce, expiresAt, mac });

    public Task<ServerAnswer<IdAnswer>> EnqueueRunAsync(string id, string linkId, string projectId, string commit, string job, string mac) =>
        AskAsync<IdAnswer>(HttpMethod.Post, "/v1/runs", new { id, linkId, projectId, commit, job, mac });

    public Task<ServerAnswer<List<TestRunInfo>>> RunsAsync(string? projectId = null) =>
        AskAsync<List<TestRunInfo>>(HttpMethod.Get, projectId is null ? "/v1/runs" : $"/v1/runs?project={Uri.EscapeDataString(projectId)}");

    public Task<ServerAnswer<TestRunWithLog>> ReadRunAsync(string run, int after) =>
        AskAsync<TestRunWithLog>(HttpMethod.Get, $"/v1/runs/{Uri.EscapeDataString(run)}?after={after}");

    public Task<ServerAnswer<bool>> CancelRunAsync(string run) =>
        AskAsync<bool>(HttpMethod.Post, $"/v1/runs/{Uri.EscapeDataString(run)}/cancel");

    /// <summary>No content means there's nothing waiting.</summary>
    public Task<ServerAnswer<TestRunInfo>> ClaimRunAsync() =>
        AskAsync<TestRunInfo>(HttpMethod.Post, "/v1/machines/me/runs/next");

    public Task<ServerAnswer<RunKeepGoing>> HeartbeatAsync(string run) =>
        AskAsync<RunKeepGoing>(HttpMethod.Post, $"/v1/runs/{Uri.EscapeDataString(run)}/heartbeat");

    public Task<ServerAnswer<RunKeepGoing>> AppendLogAsync(string run, int seq, string text) =>
        AskAsync<RunKeepGoing>(HttpMethod.Post, $"/v1/runs/{Uri.EscapeDataString(run)}/log", new { seq, text });

    public Task<ServerAnswer<bool>> EndRunAsync(string run, string status, int? exitCode, IReadOnlyList<CheckResult>? checks = null) =>
        AskAsync<bool>(HttpMethod.Post, $"/v1/runs/{Uri.EscapeDataString(run)}/end", new { status, exitCode, checks = checks ?? [] });

    /// <summary>How each check went in a run, by name (#105).</summary>
    public Task<ServerAnswer<List<CheckResult>>> RunChecksAsync(string run) =>
        AskAsync<List<CheckResult>>(HttpMethod.Get, $"/v1/runs/{Uri.EscapeDataString(run)}/checks");

    /// <summary>
    /// A project's checks across its runs, newest first: one check's whole history, or with no name,
    /// every check that failed at least once, its passes too.
    /// </summary>
    public Task<ServerAnswer<List<CheckHistoryRow>>> CheckHistoryAsync(string projectId, string? name = null) =>
        AskAsync<List<CheckHistoryRow>>(HttpMethod.Get, $"/v1/check-history?project={Uri.EscapeDataString(projectId)}{(name is null ? "" : $"&name={Uri.EscapeDataString(name)}")}");

    public sealed record IdAnswer(string Id);

    public sealed record ExpiresAnswer(double ExpiresAt);

    // Only the server's own answers count, not a proxy's on the way to it. A body is read only for a
    // success that has one; bool answers have none.
    private async Task<ServerAnswer<T>> AskAsync<T>(HttpMethod method, string path, object? body = null)
    {
        try
        {
            using var response = await SendAsync(method, path, body);
            if (response is null || !response.Headers.Contains("X-Agent-Orange")) return new(default, null);
            if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(bool))
            {
                return new(typeof(T) == typeof(bool) && response.IsSuccessStatusCode ? (T)(object)true : default, response.StatusCode);
            }
            return new(await response.Content.ReadFromJsonAsync<T>(), response.StatusCode);
        }
        catch (Exception error) when (IsNetworkFailure(error))
        {
            return new(default, null);
        }
    }
}
