using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>How a project's tests run on a test machine, as its repo says at a commit.</summary>
public sealed record TestSetup(string Prepare, string Test, int? CouldNotRunExitCode, string[] Tags, int TimeoutMinutes, int? PrepareTimeoutMinutes = null)
{
    /// <summary>What the time limits cover, in a few words, as the run's log and the Tests editor say it.</summary>
    public string LimitsText => PrepareTimeoutMinutes is { } prepare
        ? $"{TestsFile.Minutes(prepare)} for checking out and prepare, then {TestsFile.Minutes(TimeoutMinutes)} for the test command"
        : $"{TestsFile.Minutes(TimeoutMinutes)}, from checking out to the last test";
}

/// <summary>
/// A project's test setup lives in its repo, so what a test machine runs is whatever's committed
/// at the commit it was sent, and nothing the server or another PC can change decides it. The
/// file is <c>.agent-orange/tests.json</c> at the repo's root:
///
/// <code>
/// {
///   "prepare": "npm ci",               // optional: run first, such as installing or building; if it fails, the tests couldn't run
///   "test": "npm test",                // required: its exit code is the verdict, 0 passed, anything else failed
///   "couldNotRunExitCode": 3,          // optional: an exit code of test meaning the tests couldn't run, rather than failed (not 0)
///   "tags": ["windows", "gpu"],        // optional: words a test machine must offer to be picked
///   "timeoutMinutes": 30,              // optional: from checking out to the last test, 1 to 1440, 30 if left out
///   "prepareTimeoutMinutes": 90        // optional: a limit of its own for checking out and prepare, 1 to 1440;
///                                      //   with it, timeoutMinutes covers only the test command, from when it starts
/// }
/// </code>
///
/// Commands are run in the repo's folder, by the shell the test machine chose (PowerShell, cmd or
/// Git Bash). Other properties are refused, so a typo doesn't pass silently.
/// </summary>
public static class TestsFile
{
    public const string RelativePath = ".agent-orange/tests.json";
    public const int DefaultTimeoutMinutes = 30;
    private const int MaxBytes = 64 * 1024;
    private static readonly string[] Known = ["prepare", "test", "couldNotRunExitCode", "tags", "timeoutMinutes", "prepareTimeoutMinutes"];

    public static string Minutes(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

    public static string PathIn(string folder) => Path.Combine(folder, ".agent-orange", "tests.json");

    /// <summary>The setup, or why the text isn't one, in words a person can act on.</summary>
    public static (TestSetup? Setup, string? Problem) Parse(string text)
    {
        if (text.Length > MaxBytes) return (null, $"{RelativePath} is over 64 KB, which is more than a test setup needs.");
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException error)
        {
            return (null, $"{RelativePath} isn't valid JSON: {error.Message}");
        }
        if (json is null) return (null, $"{RelativePath} must be a JSON object, like {{ \"test\": \"npm test\" }}.");

        if (json.Select(pair => pair.Key).FirstOrDefault(key => !Known.Contains(key)) is { } unknown)
        {
            return (null, $"{RelativePath} has \"{unknown}\", which isn't one of {string.Join(", ", Known)}.");
        }

        string? Text(string name, out string? problem)
        {
            problem = null;
            if (json[name] is null) return null;
            if (json[name] is JsonValue value && value.TryGetValue<string>(out var text)) return text.Trim();
            problem = $"In {RelativePath}, \"{name}\" must be text.";
            return null;
        }
        int? Number(string name, out string? problem)
        {
            problem = null;
            if (json[name] is null) return null;
            if (json[name] is JsonValue value && value.TryGetValue<int>(out var number)) return number;
            problem = $"In {RelativePath}, \"{name}\" must be a whole number.";
            return null;
        }

        var prepare = Text("prepare", out var problem) ?? "";
        if (problem is not null) return (null, problem);
        var test = Text("test", out problem) ?? "";
        if (problem is not null) return (null, problem);
        if (test.Length == 0) return (null, $"{RelativePath} has no \"test\" command, which is the one thing it needs.");
        var couldNotRun = Number("couldNotRunExitCode", out problem);
        if (problem is not null) return (null, problem);
        if (couldNotRun == 0) return (null, $"In {RelativePath}, \"couldNotRunExitCode\" can't be 0, which means passed.");
        var timeout = Number("timeoutMinutes", out problem) ?? DefaultTimeoutMinutes;
        if (problem is not null) return (null, problem);
        if (timeout is < 1 or > 24 * 60) return (null, $"In {RelativePath}, \"timeoutMinutes\" must be from 1 to 1440.");
        var prepareTimeout = Number("prepareTimeoutMinutes", out problem);
        if (problem is not null) return (null, problem);
        if (prepareTimeout is < 1 or > 24 * 60) return (null, $"In {RelativePath}, \"prepareTimeoutMinutes\" must be from 1 to 1440.");

        var tags = new List<string>();
        if (json["tags"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonValue tag || !tag.TryGetValue<string>(out var word) || word.Trim().Length is 0 or > 40)
                {
                    return (null, $"In {RelativePath}, \"tags\" must be a list of short words, like [\"windows\", \"gpu\"].");
                }
                if (!tags.Contains(word.Trim(), StringComparer.OrdinalIgnoreCase)) tags.Add(word.Trim());
            }
        }
        else if (json["tags"] is not null)
        {
            return (null, $"In {RelativePath}, \"tags\" must be a list, like [\"windows\", \"gpu\"].");
        }
        return (new TestSetup(prepare, test, couldNotRun, [.. tags], timeout, prepareTimeout), null);
    }

    /// <summary>
    /// The file in a folder on this PC: null with no problem when there's none. As it is in the
    /// folder, which may not be what's committed.
    /// </summary>
    public static (TestSetup? Setup, string? Problem, bool Exists) Read(string folder)
    {
        var path = PathIn(folder);
        try
        {
            if (!File.Exists(path)) return (null, null, false);
            var (setup, problem) = Parse(File.ReadAllText(path));
            return (setup, problem, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, $"{RelativePath} couldn't be read: {error.Message}", true);
        }
    }

    public static string Write(TestSetup setup)
    {
        var json = new JsonObject();
        if (setup.Prepare.Length > 0) json["prepare"] = setup.Prepare;
        json["test"] = setup.Test;
        if (setup.CouldNotRunExitCode is { } code) json["couldNotRunExitCode"] = code;
        if (setup.Tags.Length > 0) json["tags"] = new JsonArray([.. setup.Tags.Select(tag => (JsonNode?)tag)]);
        json["timeoutMinutes"] = setup.TimeoutMinutes;
        if (setup.PrepareTimeoutMinutes is { } prepare) json["prepareTimeoutMinutes"] = prepare;
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    /// <summary>Writes the file into the folder, making .agent-orange if it's not there. Null if it worked.</summary>
    public static string? Save(string folder, TestSetup setup)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, ".agent-orange"));
            File.WriteAllText(PathIn(folder), Write(setup), new System.Text.UTF8Encoding(false));
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{RelativePath} couldn't be saved: {error.Message}";
        }
    }
}
