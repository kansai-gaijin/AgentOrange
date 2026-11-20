using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// What an agent gives AO's tools, checked against each tool's own description of its arguments
/// before the tool runs, so a refusal names the argument, says what was wrong and lists what's
/// allowed, rather than a bare "Say page.". Agents called tools with names that weren't theirs
/// (query for text, code for script), and with numbers and yes-or-no as words; the words are taken
/// as meant, and the names are refused with the real ones.
/// </summary>
public partial class MainWindow
{
    // The arguments as the tool reads them, or a refusal saying what's wrong.
    private static JsonObject CheckedArguments(McpTool tool, JsonObject given)
    {
        var unknown = given.Select(each => each.Key).Where(name => !tool.Properties.ContainsKey(name)).ToList();
        if (unknown.Count > 0)
        {
            var meant = unknown.Select(name => Closest(name, tool.Properties.Select(each => each.Key)) is { } close ? $"{name} (did you mean {close}?)" : name);
            throw new McpException(-32602, $"{tool.Name} has no {(unknown.Count == 1 ? "argument" : "arguments")} called {string.Join(" or ", meant)}. {ArgumentsOf(tool)}");
        }

        var taken = new JsonObject();
        foreach (var (name, value) in given)
        {
            var schema = tool.Properties[name] as JsonObject;
            taken[name] = value is null ? null : (schema?["type"]?.GetValue<string>()) switch
            {
                "string" => AsText(tool, name, value, schema),
                "boolean" => AsYesOrNo(tool, name, value),
                "integer" => AsWholeNumber(tool, name, value),
                _ => value.DeepClone(),
            };
        }
        foreach (var needed in tool.Required.Where(name => taken[name] is null || taken[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length == 0))
        {
            throw new McpException(-32602, Needs(tool, needed));
        }
        return taken;
    }

    // What a missing argument is for, from the tool's own description of it.
    private static string Needs(McpTool tool, string name) =>
        $"{tool.Name} needs {name}{(tool.Properties[name]?["description"]?.GetValue<string>() is { Length: > 0 } about ? $": {about}" : ".")} {ArgumentsOf(tool)}";

    private static string ArgumentsOf(McpTool tool) =>
        tool.Properties.Count == 0
            ? $"{tool.Name} takes no arguments."
            : $"{tool.Name}'s arguments: {string.Join(", ", tool.Properties.Select(each => tool.Required.Contains(each.Key) ? $"{each.Key} (needed)" : each.Key))}.";

    private static JsonNode AsText(McpTool tool, string name, JsonNode value, JsonObject? schema)
    {
        var text = value is JsonValue plain
            ? plain.TryGetValue<string>(out var words) ? words : plain.ToJsonString().Trim('"')
            : throw new McpException(-32602, $"{tool.Name}'s {name} is text, not {(value is JsonArray ? "a list" : "an object")}. {ArgumentsOf(tool)}");
        if (schema?["enum"] is JsonArray allowed && allowed.Select(each => each?.GetValue<string>()).ToList() is var choices && !choices.Contains(text))
        {
            var match = choices.FirstOrDefault(choice => choice?.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase) == true)
                ?? throw new McpException(-32602, $"{tool.Name}'s {name} is one of {string.Join(" or ", choices)}; it was {text}.");
            text = match;
        }
        return JsonValue.Create(text);
    }

    private static JsonNode AsYesOrNo(McpTool tool, string name, JsonNode value)
    {
        if (value is JsonValue plain && plain.TryGetValue<bool>(out var yes)) return JsonValue.Create(yes);
        if (value is JsonValue words && words.TryGetValue<string>(out var said) && bool.TryParse(said.Trim(), out var meant)) return JsonValue.Create(meant);
        throw new McpException(-32602, $"{tool.Name}'s {name} is true or false; it was {value.ToJsonString()}.");
    }

    private static JsonNode AsWholeNumber(McpTool tool, string name, JsonNode value)
    {
        if (value is JsonValue plain)
        {
            if (plain.TryGetValue<int>(out var number)) return JsonValue.Create(number);
            if (plain.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) < int.MaxValue) return JsonValue.Create((int)real);
            if (plain.TryGetValue<string>(out var words) && int.TryParse(words.Trim(), out var parsed)) return JsonValue.Create(parsed);
        }
        throw new McpException(-32602, $"{tool.Name}'s {name} is a whole number, such as 3; it was {value.ToJsonString()}.");
    }

    /// <summary>The name among these that's nearest the one given, if one's near enough to be what was meant.</summary>
    internal static string? Closest(string asked, IEnumerable<string> names)
    {
        static string Bare(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        var bare = Bare(asked);
        var best = names.Select(name => (Name: name, Apart: Apart(bare, Bare(name)))).OrderBy(each => each.Apart).FirstOrDefault();
        if (best.Name is null) return null;
        var near = best.Apart <= Math.Max(1, bare.Length / 3);
        var within = names.FirstOrDefault(name => bare.Length >= 3 && (Bare(name).Contains(bare) || bare.Contains(Bare(name))) && Bare(name).Length >= 3);
        return near ? best.Name : within;
    }

    // How many letters apart two names are: changes, additions and removals.
    private static int Apart(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var kept = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = kept;
            }
        }
        return row[b.Length];
    }

    /// <summary>
    /// A relayed tool's own arguments, set against what was given, for adding to its error when they
    /// don't match: the names it takes, which are needed, and what was given that it doesn't take.
    /// </summary>
    internal static string? ArgumentsMismatch(string toolName, JsonObject? schema, JsonObject given)
    {
        var properties = schema?["properties"] as JsonObject ?? [];
        var required = (schema?["required"] as JsonArray ?? []).Select(each => each?.GetValue<string>()).OfType<string>().ToList();
        var unknown = given.Select(each => each.Key).Where(name => !properties.ContainsKey(name)).ToList();
        var missing = required.Where(name => !given.ContainsKey(name)).ToList();
        if (unknown.Count == 0 && missing.Count == 0) return null;

        var said = new StringBuilder();
        foreach (var name in unknown) said.Append($"{toolName} has no argument called {name}{(Closest(name, properties.Select(each => each.Key)) is { } close ? $" (did you mean {close}?)" : "")}. ");
        if (missing.Count > 0) said.Append($"It needs {string.Join(" and ", missing)}. ");
        said.Append(properties.Count == 0
            ? $"{toolName} takes no arguments."
            : $"{toolName}'s arguments: {string.Join(", ", properties.Select(each => $"{each.Key}{(each.Value?["type"]?.ToString() is { Length: > 0 } type ? $" ({type}{(required.Contains(each.Key) ? ", needed" : "")})" : required.Contains(each.Key) ? " (needed)" : "")}"))}.");
        return said.ToString();
    }
}
