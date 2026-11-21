#if DEBUG
using System.Security.Cryptography;
using System.Text;

namespace AgentOrange;

/// <summary>
/// Headless checks only, for linking test machines without a typed code (#95). Checks have no
/// server, so the page is shown made-up machines, and the two PCs' halves of linking are run
/// against each other here, as the server would carry them.
/// </summary>
public partial class MainWindow
{
    private static readonly TestMachineInfo OfficeForChecks = new("office", "Office PC", ["windows", "gpu"], true, "reachable", 0);
    private static readonly TestMachineInfo StudioForChecks = new("studio", "Studio PC", ["windows"], true, "reachable", 0);
    private static readonly TestMachineInfo AwayForChecks = new("away", "Away PC", [], true, "unreachable", 0);
    private static readonly TestMachineInfo SpareForChecks = new("spare", "Spare PC", [], false, "unreachable", 0);
    private static readonly TestMachineInfo OlderForChecks = new("older", "Older PC", [], true, "other_release", 0);

    /// <summary>
    /// As if the server said: this PC links to Office PC; Studio PC and Away PC take runs, Away PC
    /// not connected; Spare PC doesn't take runs; Older PC is on another release; Old Laptop never
    /// said, and Ancient PC paired before PCs said which they were. Laptop is linked to this PC.
    /// </summary>
    internal void ShowLinksForChecks()
    {
        var inMonth = DateTimeOffset.UtcNow.AddDays(28).ToUnixTimeSeconds();
        var toOffice = new MachineLinkInfo("link-office", "office", "Office PC", ["windows", "gpu"], "me", "This PC", "tests", 0, inMonth, "reachable", 0);
        var toOlder = new MachineLinkInfo("link-older", "older", "Older PC", [], "me", "This PC", "tests", 0, inMonth, "other_release", 0);
        var fromLaptop = new MachineLinkInfo("link-laptop", "me", "This PC", [], "laptop", "Laptop", "tests", 0, inMonth, "reachable", 0);
        _testMachines.ShowForChecks(new MachinesInfo(
            "me",
            null,
            [OfficeForChecks, StudioForChecks, AwayForChecks, SpareForChecks, OlderForChecks],
            [toOffice, toOlder, fromLaptop],
            [new OtherPc("Old Laptop", "other_release", 0), new OtherPc("Ancient PC", "pair_again", 0)]));
    }

    /// <summary>A link being made to one of those machines, at that step, or failed with that problem.</summary>
    internal void ShowLinkingForChecks(string label, string? step, string? problem)
    {
        var machine = new[] { OfficeForChecks, StudioForChecks, AwayForChecks, SpareForChecks, OlderForChecks }.FirstOrDefault(each => each.Label == label)
            ?? throw new InvalidOperationException($"There's no made-up machine called {label}.");
        _testMachines.LinkingForChecks(machine, step, problem);
    }

    /// <summary>
    /// Both PCs' halves of linking, against each other, as the server would carry them: the key
    /// both end up with, a job and a renewal signed with it, and answers the PC linking must not
    /// keep. Says what it found; throws at the first thing that's wrong.
    /// </summary>
    internal static string LinkProtocolForChecks()
    {
        var said = new StringBuilder();
        void Expect(bool holds, string what)
        {
            if (!holds) throw new InvalidOperationException($"Linking: {what} didn't hold.");
            said.AppendLine($"ok: {what}");
        }

        using var linker = MachineLinks.NewEphemeral();
        var nonce = MachineLinks.NewId();
        var request = new PendingLinkRequest(MachineLinks.NewId(), "laptop", "Laptop", "tests", nonce, MachineLinks.PublicKey(linker));
        var accepted = MachineLinks.Accept(request, "office", "tests") ?? throw new InvalidOperationException("Linking: the target couldn't answer a good request.");
        var answer = new LinkAnswer("accepted", accepted.LinkId, accepted.ExpiresAt, accepted.MachineKey, accepted.Confirm, Access: accepted.Access);
        var taken = MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, answer);
        Expect(taken is not null, "the PC linking keeps the target's answer");
        var key = taken?.Key;
        Expect(CryptographicOperations.FixedTimeEquals(key!, accepted.LinkKey), "both PCs have the same link key");
        Expect(accepted.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() is > 29 * 86400 and <= 30 * 86400, "the target sets 30 days");

        // A job signed on the PC linking is one the target takes, and one signed with any other key isn't.
        var runId = MachineLinks.NewId();
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var job = $"{{\"runId\":\"{runId}\"}}";
        var mac = MachineLinks.JobMac(key!, runId, accepted.LinkId, "project", commit, job);
        Expect(MachineLinks.MacMatches(MachineLinks.JobMac(accepted.LinkKey, runId, accepted.LinkId, "project", commit, job), mac), "the target takes a job the PC linking signed");
        Expect(!MachineLinks.MacMatches(MachineLinks.JobMac(RandomNumberGenerator.GetBytes(32), runId, accepted.LinkId, "project", commit, job), mac), "a job signed with another key is refused");
        Expect(!MachineLinks.MacMatches(MachineLinks.JobMac(accepted.LinkKey, runId, accepted.LinkId, "project", commit, job.Replace("runId", "runid")), mac), "a changed job is refused");

        // Renewing, both ways.
        var sentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var renew = MachineLinks.RenewMac(key!, accepted.LinkId, nonce, sentAt, accepted.ExpiresAt);
        Expect(MachineLinks.MacMatches(MachineLinks.RenewMac(accepted.LinkKey, accepted.LinkId, nonce, sentAt, accepted.ExpiresAt), renew), "the target takes a renewal the PC linking asked for");
        var renewed = MachineLinks.RenewedMac(accepted.LinkKey, accepted.LinkId, nonce, accepted.ExpiresAt);
        Expect(MachineLinks.MacMatches(MachineLinks.RenewedMac(key!, accepted.LinkId, nonce, accepted.ExpiresAt), renewed), "the PC linking takes the target's renewal");

        // Answers the PC linking mustn't keep.
        using var other = MachineLinks.NewEphemeral();
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, answer with { Key = MachineLinks.PublicKey(other) }) is null, "a key swapped in without its confirmation is refused");
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, answer with { Confirm = new string('0', 64) }) is null, "a wrong confirmation is refused");
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, answer with { ExpiresAt = accepted.ExpiresAt + 86400 }) is null, "a changed expiry is refused");
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, answer with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds() }) is null, "an expiry past 30 days is refused");
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "spare", "tests", nonce, answer) is null, "an answer from another PC than the one asked is refused");
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, answer with { Access = "agents" }) is null, "an answer whose access was changed on the way is refused");
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, answer with { Access = null }) is null, "an answer that doesn't say its access is refused");
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", MachineLinks.NewId(), answer) is null, "an answer to another request is refused");
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, answer with { Answer = "rejected" }) is null, "a refusal isn't kept as a link");
        Expect(MachineLinks.Accept(request with { Key = "bm90IGEga2V5IGF0IGFsbCwganVzdCB3b3JkcyB0aGF0IGxvb2sgbGlrZSBiYXNlNjQ=" }, "office", "tests") is null, "a request whose key isn't one is turned down");

        // Access: what the target grants is the lower of what was asked and what the person there
        // chose, never Full access, and the PC linking keeps it only when it's what was asked or less.
        Expect(MachineLinks.Grant("agents", "tests") == "tests", "a PC asking for Agents is granted Tests where the target grants Tests");
        Expect(MachineLinks.Grant("agents", "agents") == "agents", "a PC asking for Agents is granted Agents where the target grants it");
        Expect(MachineLinks.Grant("tests", "agents") == "tests", "a PC asking for Tests is granted Tests where the target grants Agents");
        Expect(MachineLinks.Grant("full", "agents") == "agents", "a PC asking for Full access is granted Agents at most");
        Expect(MachineLinks.Grant("root", "agents") is null, "a request for access that isn't one is turned down");
        Expect(new TestMachineSettings().GrantFor("laptop") == "tests", "a target grants Tests until the person there chooses more");
        Expect(new TestMachineSettings(Grants: new() { ["laptop"] = "full" }).GrantFor("laptop") == "tests", "a grant of Full access, which isn't offered, counts as Tests");

        using var asking = MachineLinks.NewEphemeral();
        var wanting = new PendingLinkRequest(MachineLinks.NewId(), "laptop", "Laptop", "agents", nonce, MachineLinks.PublicKey(asking));
        var lower = MachineLinks.Accept(wanting, "office", "tests")!;
        var lowerAnswer = new LinkAnswer("accepted", lower.LinkId, lower.ExpiresAt, lower.MachineKey, lower.Confirm, Access: lower.Access);
        Expect(MachineLinks.TakeAnswer(asking, "laptop", "office", "agents", nonce, lowerAnswer) is { Access: "tests" } got && CryptographicOperations.FixedTimeEquals(got.Key, lower.LinkKey), "a PC asking for Agents and granted Tests keeps a Tests link");
        Expect(MachineLinks.TakeAnswer(asking, "laptop", "office", "agents", nonce, lowerAnswer with { Access = "agents" }) is null, "a Tests link passed off as Agents on the way is refused");
        var more = MachineLinks.Accept(request, "office", "agents")!;
        Expect(MachineLinks.TakeAnswer(linker, "laptop", "office", "tests", nonce, new LinkAnswer("accepted", more.LinkId, more.ExpiresAt, more.MachineKey, more.Confirm, Access: more.Access)) is null, "an answer granting more than was asked is refused");

        // What each access allows, lowest first.
        Expect(MachineLinks.Allows("agents", "tests") && MachineLinks.Allows("agents", "agents") && !MachineLinks.Allows("tests", "agents") && !MachineLinks.Allows("agents", "full"), "Agents allows tests and agents, and Tests allows only tests");
        Expect(MachineLinks.Lower("agents", "tests") == "tests" && MachineLinks.Lower("tests", "agents") == "tests", "lowering keeps the lower");
        return said.ToString();
    }
}
#endif
