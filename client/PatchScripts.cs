using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// Keeps agents from changing files with a script written into a shell command: a heredoc fed to
/// cat or Python, sed -i, perl -i, python -c, a PowerShell here-string sent to Set-Content. An
/// apostrophe in the code breaks the shell's quoting, and an exact-text patch misses on tabs, spaces
/// or line endings, after which the old code goes on running as if it had changed. The edit tools
/// have neither problem.
///
/// Claude Code sessions run it before every shell command, as a PreToolUse hook in the settings
/// Agent Orange writes for them ("AgentOrange.exe --check-command"), so it also covers the helper
/// agents a session starts. Codex is told the same rule in the briefing. Scripts that only read,
/// such as one printing a JSON file, are left alone.
/// </summary>
internal static partial class PatchScripts
{
    public const string HookArgument = "--check-command";

    private const string Instead =
        " Scripts and patches written into a shell command break on quotes, tabs and line endings, and a patch that misses still leaves the old code to run."
        + " Change a file with the Edit tool, or make one with Write. To run a script, Write it to a file first, then run that file."
        + " Check the change is there before running anything long. Tell any helper agents the same.";

    /// <summary>Why the command is refused, or null when it's fine to run.</summary>
    public static string? Problem(string command)
    {
        if (What(command) is not { } what) return null;
        return $"Agent Orange stopped this command: it {what}." + Instead;
    }

    private static string? What(string command)
    {
        if (InPlaceSed().IsMatch(command)) return "edits a file in place with sed -i";
        if (InPlacePerl().IsMatch(command)) return "edits a file in place with perl -i";

        // Heredocs: each one's body, and the command it's given to.
        var lines = command.Replace("\r\n", "\n").Split('\n');
        for (var at = 0; at < lines.Length; at++)
        {
            foreach (Match heredoc in Heredoc().Matches(lines[at]))
            {
                var end = heredoc.Groups["end"].Value;
                var body = new StringBuilder();
                for (var next = at + 1; next < lines.Length; next++)
                {
                    var line = heredoc.Groups["tabs"].Success ? lines[next].TrimStart('\t') : lines[next];
                    if (line == end) break;
                    body.AppendLine(lines[next]);
                }
                // The command it's given to, and what's done with that command's output.
                var head = lines[at][..heredoc.Index];
                head = head[(head.LastIndexOfAny([';', '|', '&', '(']) + 1)..];
                var rest = lines[at][(heredoc.Index + heredoc.Length)..];
                var piped = PipedTo().Match(rest) is { Success: true } pipe ? pipe.Groups["program"].Value : "";
                if (FileWriter().IsMatch(head) && (Redirect().IsMatch((head + rest).Replace("2>&1", "")) || TeeToFile().IsMatch(head + rest)))
                    return "writes a file from a heredoc";
                if (ApplyPatch().IsMatch(head) || ApplyPatch().IsMatch(rest)) return "applies a patch written into a heredoc";
                if ((Shell().IsMatch(head) || Shell().IsMatch(piped)) && What(body.ToString()) is { } inside) return inside;
                if ((Interpreter().IsMatch(head) || Interpreter().IsMatch(piped)) && WritesFiles(body.ToString())) return "runs a script from a heredoc that writes files";
            }
        }

        // Code given on the command line, such as python -c "...".
        if (InlineCode().Match(command) is { Success: true } inline && WritesFiles(command[(inline.Index + inline.Length)..]))
            return $"runs {inline.Groups["program"].Value} {inline.Groups["flag"].Value} code that writes files";

        // PowerShell's here-strings, written to a file or fed to a program.
        foreach (Match text in HereString().Matches(command))
        {
            var after = command[(text.Index + text.Length)..];
            var before = command[..text.Index];
            if (PipedTo().Match(after) is { Success: true } piped && Interpreter().IsMatch(piped.Groups["program"].Value) && WritesFiles(text.Groups["body"].Value))
                return "runs a script from a here-string that writes files";
            if (PowerShellWriter().IsMatch(after) || PowerShellWriter().IsMatch(before) && Regex.IsMatch(before, @"-Value\s*$", RegexOptions.IgnoreCase))
                return "writes a file from a here-string";
        }
        if (PowerShellWriter().IsMatch(command) && (Regex.IsMatch(command, @"\s-c?replace\s", RegexOptions.IgnoreCase) || command.Contains(".Replace(", StringComparison.Ordinal)))
            return "patches a file by replacing text and writing it back";
        return null;
    }

    // Code that writes or appends to files, in the languages agents reach for.
    private static bool WritesFiles(string code) => WriteCalls().IsMatch(code);

    /// <summary>
    /// What Claude Code's hook runs: the tool call comes in as JSON on standard input, and a refused
    /// command exits with 2 and says why on standard error, which Claude Code gives to the agent.
    /// Anything it can't read is let through, so a change in the hook's input never stops an agent.
    /// </summary>
    public static int RunHook(TextReader input, TextWriter error)
    {
        try
        {
            var call = JsonNode.Parse(input.ReadToEnd());
            if (call?["tool_input"]?["command"] is not JsonValue value || !value.TryGetValue<string>(out var command)) return 0;
            if (Problem(command) is not { } problem) return 0;
            error.WriteLine(problem);
            error.Flush();
            return 2;
        }
        catch (Exception failed) when (failed is JsonException or IOException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>The hook as it goes in a Claude Code settings file: on every Bash and PowerShell call, the main agent's and its helpers'.</summary>
    public static JsonObject Hooks(string program) => new()
    {
        ["PreToolUse"] = new JsonArray(new JsonObject
        {
            ["matcher"] = "Bash|PowerShell",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                // Forward slashes, which both Git Bash and cmd read as they are.
                ["command"] = $"\"{program.Replace('\\', '/')}\" {HookArgument}",
                ["timeout"] = 15,
            }),
        }),
    };

    [GeneratedRegex(@"(?<![\w./-])g?sed(?:\.exe)?\s(?:[^\n|;&]*?\s)?(?:-(?!-)[a-zA-Z]*i|--in-place)")]
    private static partial Regex InPlaceSed();

    [GeneratedRegex(@"(?<![\w./-])perl(?:\.exe)?\s(?:[^\n|;&]*?\s)?-(?!-)[a-zA-Z]*i")]
    private static partial Regex InPlacePerl();

    [GeneratedRegex(@"(?<!<)<<(?<tabs>-)?\s*(?:'(?<end>[^'\s]+)'|""(?<end>[^""\s]+)""|\\?(?<end>[A-Za-z_][\w]*))")]
    private static partial Regex Heredoc();

    [GeneratedRegex(@"(?<![\w./-])(?:cat|tee)(?:\.exe)?\b")]
    private static partial Regex FileWriter();

    [GeneratedRegex(@"(?<![\w./-])tee(?:\.exe)?\s+(?:-\S+\s+)*[^\s<>|-]")]
    private static partial Regex TeeToFile();

    // A redirect into a file, not a copy of one stream onto another, such as 2>&1.
    [GeneratedRegex(@"(?<![<>=-])>>?\s*(?!&)[^\s>]")]
    private static partial Regex Redirect();

    [GeneratedRegex(@"(?<![\w./-])(?:git\s+apply|patch)\b")]
    private static partial Regex ApplyPatch();

    [GeneratedRegex(@"(?<![\w./-])(?:bash|sh|zsh)(?:\.exe)?\b")]
    private static partial Regex Shell();

    [GeneratedRegex(@"(?<![\w./-])(?:python[\d.]*|py|node|deno|bun|ruby|perl|php|pwsh|powershell)(?:\.exe)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Interpreter();

    [GeneratedRegex(@"(?<![\w./-])(?<program>python[\d.]*|py|node|ruby|perl|php)(?:\.exe)?\b[^\n|;&]*?\s(?<flag>-c|-e|-E|-p|--eval|-r)(?=\s)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"@(?<quote>['""])\r?\n(?<body>.*?)\r?\n\k<quote>@", RegexOptions.Singleline)]
    private static partial Regex HereString();

    [GeneratedRegex(@"^\s*\|\s*(?:&\s*)?(?<program>[^\s|;]+)")]
    private static partial Regex PipedTo();

    [GeneratedRegex(@"\b(?:Set-Content|Add-Content|Out-File)\b|\[(?:System\.)?IO\.File\]::(?:Write|Append)All", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellWriter();

    [GeneratedRegex(@"\bopen\s*\([^\n]*?,\s*(?:mode\s*=\s*)?[rbuf]?['""][rbtU]*[wax+][rbtU+]*['""]|\.open\s*\(\s*(?:mode\s*=\s*)?['""][rbtU]*[wax+]|\.write_(?:text|bytes)\s*\(|\b(?:writeFile|appendFile|writeFileSync|appendFileSync|createWriteStream)\s*\(|\bFile\.(?:write|binwrite)\b|\bfile_put_contents\s*\(|\binplace\s*=\s*(?:True|1)\b|\b(?:Set-Content|Add-Content|Out-File)\b|\bFile\]?::(?:Write|Append)All|\bFile\.(?:Write|Append)All(?:Text|Lines|Bytes)\b")]
    private static partial Regex WriteCalls();
}
