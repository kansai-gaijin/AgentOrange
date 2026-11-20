using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// A project's rules and lessons: one document, on the project's Lessons page, that every agent's
/// briefing includes, and every helper agent a Claude Code session starts is given too. Rules are the
/// person's standing rules, such as how to test and what never to run, written once instead of into
/// every opening message. Lessons are what agents learned about the tools, one line each under the
/// tool's name, added with add_lesson so the next conversation doesn't hit the same wrong argument or
/// API name again. The person edits and prunes it like any other document. It's the project's, not a
/// stage's, as tools behave the same in every stage.
/// </summary>
public static partial class ProjectLessons
{
    /// <summary>Where it's kept among the project's documents.</summary>
    public const string Key = "lessons";

    public const string Page = "Lessons";

    private const string RulesHeading = "## Rules";
    private const string LessonsHeading = "## Lessons";

    // Biggest it goes in a briefing whole; past that, the page has the rest.
    private const int LongestInBriefing = 8000;

    /// <summary>What the page starts with, until something's written: where each kind goes.</summary>
    public const string Template = """
        ## Rules

        _Your standing rules for every agent on this project, such as how to test, what never to run, and what to check with screenshots. Every agent and its helpers are given them. Only you change them._

        ## Lessons

        _What agents learned about the tools, one line each under the tool's name. Agents add them with add_lesson. Change or delete any that are wrong or no longer needed._
        """;

    /// <summary>
    /// The document with the lesson added under its tool's heading in Lessons, made if it isn't there,
    /// or unchanged when that tool already has it.
    /// </summary>
    public static (string Markdown, bool Added) Add(string? markdown, string tool, string lesson, string? evidence, DateOnly day)
    {
        var lines = (markdown is { Length: > 0 } ? markdown : Template).Replace("\r\n", "\n").TrimEnd().Split('\n').ToList();
        var line = $"- {OneLine(lesson)}{(evidence is { Length: > 0 } ? $" Evidence: {OneLine(evidence)}" : "")} ({day.ToString("d MMM yyyy", CultureInfo.InvariantCulture)})";

        var lessons = lines.FindIndex(each => each.Trim().Equals(LessonsHeading, StringComparison.OrdinalIgnoreCase));
        if (lessons < 0)
        {
            lines.AddRange(["", LessonsHeading]);
            lessons = lines.Count - 1;
        }
        var sectionEnd = NextHeading(lines, lessons + 1, "## ");
        var toolHeading = lines.FindIndex(lessons + 1, sectionEnd - lessons - 1, each => each.Trim().Equals($"### {tool}", StringComparison.OrdinalIgnoreCase));
        if (toolHeading < 0)
        {
            var at = LastWritten(lines, lessons, sectionEnd) + 1;
            lines.InsertRange(at, ["", $"### {tool}", line]);
            return (Joined(lines), true);
        }
        var toolEnd = Math.Min(NextHeading(lines, toolHeading + 1, "### "), sectionEnd);
        if (lines.Skip(toolHeading + 1).Take(toolEnd - toolHeading - 1).Any(each => each.Contains(OneLine(lesson), StringComparison.OrdinalIgnoreCase))) return (Joined(lines), false);
        lines.Insert(LastWritten(lines, toolHeading, toolEnd) + 1, line);
        return (Joined(lines), true);
    }

    // The next heading of this level or above, from a line on, or the end.
    private static int NextHeading(List<string> lines, int from, string level)
    {
        for (var at = from; at < lines.Count; at++)
        {
            var trimmed = lines[at].TrimStart();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal) || level == "### " && trimmed.StartsWith("### ", StringComparison.Ordinal)) return at;
        }
        return lines.Count;
    }

    // The last line with something on it, from a heading to before the next.
    private static int LastWritten(List<string> lines, int heading, int end)
    {
        var last = heading;
        for (var at = heading + 1; at < end; at++) if (lines[at].Trim().Length > 0) last = at;
        return last;
    }

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Joined(List<string> lines) => string.Join("\n", lines).Trim() + "\n";

    /// <summary>
    /// What goes in a briefing: the document without the page's own hints, or null when nothing has
    /// been written in it yet.
    /// </summary>
    public static string? ForBriefing(string? markdown)
    {
        if (markdown is not { Length: > 0 }) return null;
        var kept = markdown.Replace("\r\n", "\n").Split('\n').Where(line => !Hint().IsMatch(line.Trim())).ToList();
        if (!kept.Any(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))) return null;
        var text = Regex.Replace(string.Join("\n", kept), @"\n{3,}", "\n\n").Trim();
        if (text.Length <= LongestInBriefing) return text;
        var cut = text[..LongestInBriefing];
        return cut[..Math.Max(0, cut.LastIndexOf('\n'))] + "\n\n(Cut short, to stay small. The rest is on the project's Lessons page in Agent Orange.)";
    }

    // A line wholly in italics, as the page's hints are.
    [GeneratedRegex(@"^_[^_].*_$")]
    private static partial Regex Hint();

    /// <summary>The briefing's section on them, or null when the project has none.</summary>
    public static string? BriefingSection(string? markdown) => ForBriefing(markdown) is { } text
        ? $"""
            ## This project's rules and lessons

            From the project's Lessons page in Agent Orange. The rules are the person's standing rules: follow them in everything you do, and only the person changes them. The lessons are what earlier agents learned about the tools. Helper agents you start are given these too.

            {Demoted(text)}
            """
        : null;

    // Its headings a level down, under the briefing's own.
    private static string Demoted(string text) => Regex.Replace(text, @"^(#{2,5}) ", "#$1 ", RegexOptions.Multiline);

    /// <summary>What a helper agent is told as it starts: the rules and lessons, so the agent that started it needn't pass them on.</summary>
    public static string? ForHelpers(string? markdown) => ForBriefing(markdown) is { } text
        ? $"<agent-orange-rules>\nThe person's standing rules for this project, and what earlier agents learned about its tools, from Agent Orange. Follow the rules in everything you do.\n\n{text}\n</agent-orange-rules>"
        : null;

    public const string HelperHookArgument = "--brief-helper";

    /// <summary>
    /// Claude Code's SubagentStart hook: gives each helper agent the rules and lessons kept in this
    /// file as it starts. A file that isn't there gives nothing.
    /// </summary>
    public static int RunHelperHook(string file, TextWriter output)
    {
        try
        {
            if (!File.Exists(file) || File.ReadAllText(file) is not { Length: > 0 } text) return 0;
            // As written, rather than with <, > and ' as \u escapes: it's read as JSON, never as a web page.
            var asWritten = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            output.Write(new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "SubagentStart", ["additionalContext"] = text } }.ToJsonString(asWritten));
            output.Flush();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Nothing to add, rather than a helper that can't start.
        }
        return 0;
    }

    /// <summary>The hook as it goes in a Claude Code settings file, beside the patch-script one.</summary>
    public static JsonArray HelperHooks(string program, string file) => new(new JsonObject
    {
        ["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            // Forward slashes, which both Git Bash and cmd read as they are.
            ["command"] = $"\"{program.Replace('\\', '/')}\" {HelperHookArgument} \"{file.Replace('\\', '/')}\"",
            ["timeout"] = 15,
        }),
    });
}
