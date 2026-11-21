#if DEBUG
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentOrange;

/// <summary>
/// Headless checks only, for a link's access and agent tasks from other PCs (#93). Checks have no
/// server, so links are made up as the server would list them, with keys kept here as linking
/// keeps them, and a whole agent task runs here against a made-up server, as the target would run
/// one: every check on the job, the clone, the conversation with a check login, and the push.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// As if the server said: this PC links to Office PC with Tests, which now grants it Agents, and to
    /// Studio PC with Agents; Laptop links here with Tests and this PC grants it Agents, and Tablet
    /// with Agents. This PC keeps keys for the Studio PC and Tablet links.
    /// </summary>
    internal void ShowAccessForChecks()
    {
        var inMonth = DateTimeOffset.UtcNow.AddDays(28).ToUnixTimeSeconds();
        _testMachines.ShowForChecks(AccessLinksForChecks("agents"));
        MachineLinks.SaveKey(_database, new MachineLinkKey("link-studio", RandomNumberGenerator.GetBytes(32), "agents", inMonth, ThisPcIsMachine: false));
        MachineLinks.SaveKey(_database, new MachineLinkKey("link-tablet", RandomNumberGenerator.GetBytes(32), "agents", inMonth, ThisPcIsMachine: true));
        var settings = _testMachines.Settings;
        _testMachines.Save(settings with { Grants = new Dictionary<string, string>(settings.Grants ?? []) { ["laptop"] = "agents", ["tablet"] = "agents" } });
    }

    private static MachinesInfo AccessLinksForChecks(string studioAccess)
    {
        var inMonth = DateTimeOffset.UtcNow.AddDays(28).ToUnixTimeSeconds();
        return new MachinesInfo(
            "me",
            null,
            [OfficeForChecks, StudioForChecks],
            [
                new MachineLinkInfo("link-office", "office", "Office PC", ["windows", "gpu"], "me", "This PC", "tests", 0, inMonth, "reachable", 0, Offered: "agents"),
                new MachineLinkInfo("link-studio", "studio", "Studio PC", ["windows"], "me", "This PC", studioAccess, 0, inMonth, "reachable", 0, Offered: "agents"),
                new MachineLinkInfo("link-laptop", "me", "This PC", [], "laptop", "Laptop", "tests", 0, inMonth, "reachable", 0, Offered: "agents"),
                new MachineLinkInfo("link-tablet", "me", "This PC", [], "tablet", "Tablet", "agents", 0, inMonth, "reachable", 0, Offered: "agents"),
            ]);
    }

    /// <summary>The access this PC keeps for a link, as a job here is checked against it.</summary>
    internal string LinkKeyForChecks(string linkId) =>
        MachineLinks.ReadKey(_database, linkId) is { } key ? $"{linkId}: {MachineLinks.AccessLabel(key.Access)}" : $"{linkId}: no key";

    /// <summary>As if the server's list now had the Studio PC link lowered to that access, which this PC takes.</summary>
    internal void ServerLowersForChecks(string access)
    {
        var latest = AccessLinksForChecks(access);
        _testMachines.TakeLowered(latest);
        _testMachines.ShowForChecks(latest);
    }

    /// <summary>
    /// A whole agent task here, from another PC (Laptop) over a link of that access that this PC
    /// keeps a key for, as the target runs one, telling a made-up server. The project's folder's
    /// origin is made the upstream folder, which stands in for GitHub, and the task starts from its
    /// commit. As asked: "lowered" has the server's link at Tests, "saystests" signs an agent task
    /// that says it needs Tests, and "testjob" sends a test run instead. The log, the verdict, and
    /// the branches origin has after.
    /// </summary>
    internal async Task<string> RemoteAgentRunForChecksAsync(string projectName, string upstream, string linkAccess, string variant)
    {
        var project = _database.ListProjects().FirstOrDefault(each => each.Name == projectName) ?? throw new InvalidOperationException($"There's no project called {projectName}.");
        var folder = ProjectFolder(project.Id) ?? throw new InvalidOperationException($"{projectName} has no folder.");
        var origin = Path.Combine(Headless.DataFolder, upstream);
        var commit = await GitTools.CommitAllForChecksAsync(origin) ?? throw new InvalidOperationException($"{upstream} couldn't be committed.");
        if (await GitTools.CommitAllForChecksAsync(folder, origin) is null) throw new InvalidOperationException($"{projectName}'s folder couldn't be made a clone of {upstream}.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expires = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds();
        var linkId = MachineLinks.NewId();
        var key = RandomNumberGenerator.GetBytes(32);
        MachineLinks.SaveKey(_database, new MachineLinkKey(linkId, key, linkAccess, expires, ThisPcIsMachine: true));

        var runId = MachineLinks.NewId();
        var agent = variant != "testjob";
        var job = JsonSerializer.Serialize(new TestJob(agent ? TestJob.AgentKind : TestJob.TestsKind, runId, now, project.Id, project.Name, origin, commit, agent && variant != "saystests" ? "agents" : "tests", [],
            Task: agent ? "Write your part of the check and commit it." : null, Title: agent ? "Check task" : null), TestJob.Json);
        var mac = MachineLinks.JobMac(key, runId, linkId, project.Id, commit, job);
        var run = new TestRunInfo(runId, linkId, "me", "This PC", "laptop", "Laptop", project.Id, commit, job, mac, "running", null, false, now, now, null, variant == "lowered" ? "tests" : linkAccess, expires);

        var server = new MadeUpRunServer(int.MaxValue) { Updated = true };
        var settings = _testMachines.Settings with { Accepting = true, WorkFolder = Path.Combine(Headless.DataFolder, "test-runs") };
        var done = await new TestRunner(_database, server, settings, run) { Agents = this }.RunAsync();
        return $"{server.Log.TrimEnd()}\nVerdict: {TestRunner.VerdictLabel(done.Verdict)}.{(done.Note is { } note ? $" {note}" : "")}\nThe server took: {string.Join(", ", server.Took.Where(each => each.StartsWith("Ended", StringComparison.Ordinal)))}\nOrigin's branches: {await GitTools.BranchesForChecksAsync(origin)}\nWhat this PC keeps of it: {done.Test}; {done.Task}; {done.Branch ?? "nothing pushed"}";
    }
}
#endif
