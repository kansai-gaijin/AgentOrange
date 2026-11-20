using System.IO;
using System.Text;
using System.Xml;

namespace AgentOrange;

/// <summary>How one check went in a run: passed, failed or skipped.</summary>
public sealed record CheckResult(string Name, string Outcome);

/// <summary>One check in one run, for its history: which run, on which machine, at which commit.</summary>
public sealed record CheckHistoryRow(string Name, string Outcome, string RunId, string Machine, string MachineLabel, string Commit, double CreatedAt);

/// <summary>Why a check is flagged: it flipped between passing and failing where nothing else changed.</summary>
public sealed record CheckFlag(string Name, string Why, bool SameMachine, bool BetweenMachines);

/// <summary>
/// Checks within a run (#105). A run's verdict is the test command's exit code, which says nothing
/// about which checks failed, so a repo can name a results file its test command writes, as
/// "results" in .agent-orange/tests.json: JUnit XML, which most test tools can write, or plain
/// lines of "passed name", "failed name" or "skipped name". From the runs recorded, a check that
/// both passed and failed at the same commit is flagged, as nothing in the code changed: on one
/// machine, it flips, which points at a clock, weather or timing dependency; between machines, it
/// depends on something about the machine. Nothing here knows about any test tool or engine.
/// </summary>
public static class TestChecks
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";

    public const int MaxChecks = 5000;
    private const int MaxNameLength = 300;
    private const int MaxFiles = 500;
    private const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>
    /// The checks the test command wrote to the results file or folder, inside the run's folder.
    /// Only files written once the test command started count, so results an earlier run left, or
    /// ones committed, aren't taken for this run's. Said is a line or two for the run's log.
    /// </summary>
    public static (List<CheckResult> Checks, string Said) Read(string folder, string results, DateTime testStartedUtc)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(folder, results));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return ([], $"Results: {results} isn't inside the repo, so only the verdict is kept.");

        List<string> files;
        try
        {
            if (File.Exists(path)) files = [path];
            else if (Directory.Exists(path))
            {
                files = [.. Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System })
                    .Where(file => file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    .Take(MaxFiles)];
            }
            else return ([], $"Results: the test command didn't write {results}, so only the verdict is kept.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ([], $"Results: {results} couldn't be read: {error.Message}");
        }

        var checks = new Dictionary<string, string>(StringComparer.Ordinal);
        var old = 0;
        long bytes = 0;
        var problems = new List<string>();
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (info.LastWriteTimeUtc < testStartedUtc)
            {
                old++;
                continue;
            }
            bytes += info.Length;
            if (bytes > MaxBytes)
            {
                problems.Add("the results are over 32 MB, so the rest were left out");
                break;
            }
            try
            {
                var text = File.ReadAllText(file);
                foreach (var check in text.TrimStart().StartsWith('<') ? ParseJUnit(text) : ParseLines(text)) Add(checks, check);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
            {
                problems.Add($"{Path.GetRelativePath(folder, file)} couldn't be read: {error.Message.TrimEnd('.')}");
            }
            if (checks.Count >= MaxChecks)
            {
                problems.Add($"only the first {MaxChecks} checks are kept");
                break;
            }
        }

        var list = checks.Select(each => new CheckResult(each.Key, each.Value)).OrderBy(each => each.Name, StringComparer.Ordinal).ToList();
        var said = new StringBuilder();
        if (list.Count == 0)
        {
            said.Append(old > 0
                ? $"Results: {results} wasn't written by this run's test command, so it was left out and only the verdict is kept."
                : $"Results: {results} has no checks in it, so only the verdict is kept.");
        }
        else
        {
            said.Append($"Results: {Count(list.Count)} from {results}: {Summary(list.Count(each => each.Outcome == Passed), list.Count(each => each.Outcome == Failed), list.Count(each => each.Outcome == Skipped))}.");
            if (old > 0) said.Append($" {old} {(old == 1 ? "file there wasn't" : "files there weren't")} written by this run, so {(old == 1 ? "it was" : "they were")} left out.");
            var failed = list.Where(each => each.Outcome == Failed).ToList();
            foreach (var each in failed.Take(20)) said.Append($"\nFailed check: {each.Name}");
            if (failed.Count > 20) said.Append($"\nAnd {failed.Count - 20} more failed.");
        }
        foreach (var problem in problems) said.Append($"\nResults: {problem}.");
        return (list, said.ToString());
    }

    public static string Count(int checks) => checks == 1 ? "1 check" : $"{checks} checks";

    /// <summary>How a run's checks went, in a few words: 40 passed, 2 failed, 1 skipped.</summary>
    public static string Summary(int passed, int failed, int skipped)
    {
        var parts = new List<string> { $"{passed} passed", $"{failed} failed" };
        if (skipped > 0) parts.Add($"{skipped} skipped");
        return string.Join(", ", parts);
    }

    // A check named more than once, as a test run twice, counts as failed if it ever failed.
    private static void Add(Dictionary<string, string> checks, CheckResult check)
    {
        var name = check.Name.Replace('\0', ' ').Trim();
        if (name.Length == 0) return;
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (checks.Count >= MaxChecks && !checks.ContainsKey(name)) return;
        checks[name] = checks.TryGetValue(name, out var before) ? Worse(before, check.Outcome) : check.Outcome;
    }

    private static string Worse(string a, string b) => a == Failed || b == Failed ? Failed : a == Passed || b == Passed ? Passed : Skipped;

    /// <summary>
    /// JUnit XML, as most test tools write it: each testcase is a check, named classname.name, failed
    /// with a failure or error inside it, skipped with skipped. No DTDs or outside files are read.
    /// </summary>
    public static IEnumerable<CheckResult> ParseJUnit(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = MaxBytes };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var found = new List<CheckResult>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "testcase") continue;
            var name = reader.GetAttribute("name") ?? "";
            var group = reader.GetAttribute("classname") ?? "";
            var full = group.Length > 0 && name.Length > 0 && !name.StartsWith(group, StringComparison.Ordinal) ? $"{group}.{name}" : name.Length > 0 ? name : group;
            var outcome = Passed;
            if (!reader.IsEmptyElement)
            {
                var depth = reader.Depth;
                while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1) continue;
                    if (reader.LocalName is "failure" or "error") outcome = Failed;
                    else if (reader.LocalName == "skipped" && outcome != Failed) outcome = Skipped;
                }
            }
            found.Add(new CheckResult(full, outcome));
        }
        return found;
    }

    /// <summary>A results file of plain lines: passed, failed or skipped, a space, then the check's name. Other lines are left out.</summary>
    public static IEnumerable<CheckResult> ParseLines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOfAny([' ', '\t']);
            if (space <= 0) continue;
            var outcome = line[..space].ToLowerInvariant() switch
            {
                "passed" or "pass" => Passed,
                "failed" or "fail" => Failed,
                "skipped" or "skip" => Skipped,
                _ => null,
            };
            if (outcome is not null) yield return new CheckResult(line[(space + 1)..].Trim(), outcome);
        }
    }

    /// <summary>
    /// The checks that passed and failed at the same commit, from their history: on one machine,
    /// they flip; on different machines, each always the same way, they differ between machines.
    /// Skipped doesn't count either way.
    /// </summary>
    public static Dictionary<string, CheckFlag> Flags(IEnumerable<CheckHistoryRow> history)
    {
        var flags = new Dictionary<string, CheckFlag>(StringComparer.Ordinal);
        foreach (var check in history.Where(row => row.Outcome is Passed or Failed).GroupBy(row => row.Name, StringComparer.Ordinal))
        {
            var flips = new List<string>();
            var differs = new List<string>();
            foreach (var commit in check.GroupBy(row => row.Commit.ToLowerInvariant()))
            {
                if (commit.Select(row => row.Outcome).Distinct().Count() < 2) continue;
                var machines = commit.GroupBy(row => row.Machine).ToList();
                var at = commit.Key.Length > 12 ? commit.Key[..12] : commit.Key;
                var flipping = machines.Where(machine => machine.Select(row => row.Outcome).Distinct().Count() > 1).ToList();
                if (flipping.Count > 0)
                {
                    flips.Add($"{string.Join(" and ", flipping.Select(machine => machine.First().MachineLabel))} at {at}");
                    continue;
                }
                var passing = machines.Where(machine => machine.First().Outcome == Passed).Select(machine => machine.First().MachineLabel);
                var failing = machines.Where(machine => machine.First().Outcome == Failed).Select(machine => machine.First().MachineLabel);
                differs.Add($"passes on {string.Join(" and ", passing)} and fails on {string.Join(" and ", failing)} at {at}");
            }
            if (flips.Count == 0 && differs.Count == 0) continue;
            var why = new List<string>();
            if (flips.Count > 0) why.Add($"flips between pass and fail at the same commit, on {string.Join("; ", flips)}");
            if (differs.Count > 0) why.Add($"differs between machines: {string.Join("; ", differs)}");
            flags[check.Key] = new CheckFlag(check.Key, string.Join(". It also ", why) + ".", flips.Count > 0, differs.Count > 0);
        }
        return flags;
    }

    /// <summary>What a flag points at, for people and agents, before anyone adds a retry.</summary>
    public const string FlagAdvice = "A check that flips at the same commit usually depends on the real clock, real weather, timing or frame rate; one that differs between machines depends on something about the machine. Fix that cause rather than adding a retry, which hides real races too.";

    /// <summary>A check's history, newest first, a line a run.</summary>
    public static IEnumerable<string> HistoryLines(IEnumerable<CheckHistoryRow> rows) =>
        rows.OrderByDescending(row => row.CreatedAt).Select(row =>
            $"{DateTimeOffset.FromUnixTimeSeconds((long)row.CreatedAt).ToLocalTime():d MMM HH:mm} · {Label(row.Outcome)} on {row.MachineLabel} at {(row.Commit.Length > 12 ? row.Commit[..12] : row.Commit)} (run {row.RunId})");

    public static string Label(string outcome) => outcome switch
    {
        Passed => "Passed",
        Failed => "Failed",
        Skipped => "Skipped",
        _ => outcome,
    };
}
