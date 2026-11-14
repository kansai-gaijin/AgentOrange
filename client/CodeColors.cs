using System.Text.RegularExpressions;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// Colours for code, in the colours of T3 Code's Shiki theme, "pierre-dark" (taken from the theme
/// in T3's own build). T3 runs Shiki's grammars; this reads the same kinds of thing (keywords,
/// strings, numbers, comments, functions, types, properties, operators, punctuation) with a small
/// reader per family of languages, which is close for the code agents write.
/// </summary>
internal static partial class CodeColors
{
    public static readonly SolidColorBrush Plain = Brush("#fafafa");
    private static readonly SolidColorBrush Comment = Brush("#737373");
    private static readonly SolidColorBrush Str = Brush("#5ecc71");
    private static readonly SolidColorBrush Number = Brush("#68cdf2");
    private static readonly SolidColorBrush Constant = Brush("#ffd452");
    private static readonly SolidColorBrush Keyword = Brush("#ff678d");
    private static readonly SolidColorBrush Variable = Brush("#ffa359");
    private static readonly SolidColorBrush Language = Brush("#ffab16"); // this, self
    private static readonly SolidColorBrush Function = Brush("#9d6afb");
    private static readonly SolidColorBrush Type = Brush("#d568ea");
    private static readonly SolidColorBrush Operator = Brush("#08c0ef");
    private static readonly SolidColorBrush Punctuation = Brush("#636363");
    private static readonly SolidColorBrush Tag = Brush("#ff855e");
    private static readonly SolidColorBrush Attribute = Brush("#60d199");

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static readonly HashSet<string> Scripts = ["js", "javascript", "jsx", "mjs", "cjs", "ts", "typescript", "tsx", "cs", "csharp", "c#", "java", "kotlin", "kt", "swift", "go", "rust", "rs", "c", "cpp", "c++", "h", "hpp", "glsl", "hlsl", "shader", "gdscript", "gd", "python", "py", "php", "dart", "lua", "zig"];

    private static readonly HashSet<string> Keywords =
    [
        // JavaScript and TypeScript
        "const", "let", "var", "function", "return", "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "new", "class", "extends",
        "import", "from", "export", "default", "async", "await", "try", "catch", "finally", "throw", "typeof", "instanceof", "in", "of", "yield", "static",
        "get", "set", "interface", "type", "enum", "implements", "public", "private", "protected", "readonly", "declare", "as", "delete", "void", "keyof",
        // C#, Java, C, C++, Go, Rust, Swift
        "using", "namespace", "sealed", "partial", "record", "struct", "abstract", "override", "virtual", "internal", "params", "ref", "out", "is", "not",
        "and", "or", "when", "with", "init", "required", "foreach", "goto", "lock", "unsafe", "fixed", "base", "event", "operator", "implicit", "explicit",
        "package", "final", "throws", "func", "go", "defer", "select", "chan", "map", "range", "fn", "impl", "trait", "pub", "mod", "use", "crate", "mut",
        "match", "loop", "where", "move", "dyn", "let", "include", "define", "typedef", "extern", "inline", "template", "typename", "auto", "constexpr",
        // Python, GDScript, Lua
        "def", "elif", "lambda", "pass", "raise", "except", "global", "nonlocal", "assert", "del", "extends", "signal", "export", "onready", "tool", "preload",
        "then", "end", "local", "until", "repeat", "elseif",
        // Types written as words
        "int", "float", "double", "bool", "string", "char", "long", "short", "byte", "object", "decimal", "uint", "ulong", "number", "boolean", "any", "unknown", "never",
    ];

    private static readonly HashSet<string> Constants = ["true", "false", "null", "undefined", "None", "True", "False", "nil", "NaN", "Infinity"];

    private static readonly HashSet<string> Selves = ["this", "self", "super"];

    /// <summary>The source as runs of words, each with its colour.</summary>
    public static List<(string Text, Brush Brush)> Color(string source, string language)
    {
        var name = language.Trim().ToLowerInvariant();
        if (name.Contains(' ')) name = name[..name.IndexOf(' ')];
        if (Scripts.Contains(name)) return Script(source, name is "python" or "py" or "gdscript" or "gd" ? "#" : name is "lua" ? "--" : "//");
        return name switch
        {
            "json" or "jsonc" or "json5" => Json(source),
            "md" or "markdown" => Markdown(source),
            "html" or "xml" or "xaml" or "svg" or "vue" or "csproj" => Markup(source),
            "css" or "scss" or "less" => Css(source),
            "sh" or "bash" or "zsh" or "shell" or "fish" or "powershell" or "pwsh" or "ps1" or "ps" or "bat" or "cmd" => Shell(source),
            "yaml" or "yml" or "toml" or "ini" => Settings(source),
            _ => [(source, Plain)],
        };
    }

    // C-like and Python-like code: comments, strings, numbers, words and the marks between them.
    private static List<(string, Brush)> Script(string source, string lineComment)
    {
        var runs = new List<(string, Brush)>();
        var at = 0;
        var afterDot = false;
        while (at < source.Length)
        {
            var c = source[at];
            if (source.AsSpan(at).StartsWith(lineComment))
            {
                var end = source.IndexOf('\n', at);
                end = end < 0 ? source.Length : end;
                runs.Add((source[at..end], Comment));
                at = end;
                continue;
            }
            if (lineComment == "//" && source.AsSpan(at).StartsWith("/*"))
            {
                var end = source.IndexOf("*/", at + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                runs.Add((source[at..end], Comment));
                at = end;
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                at = Quoted(source, at, runs);
                afterDot = false;
                continue;
            }
            if (char.IsDigit(c))
            {
                var end = at;
                while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] is '.' or '_')) end++;
                runs.Add((source[at..end], Number));
                at = end;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$' or '@' or '#')
            {
                var end = at + 1;
                while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] is '_' or '$')) end++;
                var word = source[at..end];
                var next = end;
                while (next < source.Length && source[next] is ' ' or '\t') next++;
                var calls = next < source.Length && source[next] is '(' or '<' && (next + 1 >= source.Length || source[next + 1] != '=');
                Brush brush =
                    Selves.Contains(word) ? Language
                    : Constants.Contains(word) ? Number
                    : !afterDot && Keywords.Contains(word) ? Keyword
                    : calls ? Function
                    : char.IsUpper(word[0]) && !afterDot && word.Length > 1 && word.Any(char.IsLower) ? Type
                    : word.All(letter => char.IsUpper(letter) || letter == '_' || char.IsDigit(letter)) && word.Length > 1 ? Constant
                    : Variable;
                runs.Add((word, brush));
                at = end;
                afterDot = false;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                var end = at;
                while (end < source.Length && char.IsWhiteSpace(source[end])) end++;
                runs.Add((source[at..end], Plain));
                at = end;
                continue;
            }
            afterDot = c == '.';
            runs.Add((c.ToString(), "=+-*/%<>!&|^~?:".Contains(c) ? Operator : Punctuation));
            at++;
        }
        return runs;
    }

    // A string, with ${ } in template strings marked as T3's theme marks them.
    private static int Quoted(string source, int at, List<(string, Brush)> runs)
    {
        var quote = source[at];
        var end = at + 1;
        while (end < source.Length && source[end] != quote && (quote == '`' || source[end] != '\n'))
        {
            if (source[end] == '\\') end++;
            end++;
        }
        end = Math.Min(source.Length, end + 1);
        var text = source[at..end];
        if (quote != '`' || !text.Contains("${", StringComparison.Ordinal))
        {
            runs.Add((text, Str));
            return end;
        }
        var from = 0;
        foreach (Match part in TemplatePart().Matches(text))
        {
            if (part.Index > from) runs.Add((text[from..part.Index], Str));
            runs.Add(("${", Keyword));
            runs.AddRange(Script(part.Groups[1].Value, "//"));
            runs.Add(("}", Keyword));
            from = part.Index + part.Length;
        }
        if (from < text.Length) runs.Add((text[from..], Str));
        return end;
    }

    private static List<(string, Brush)> Json(string source)
    {
        var runs = new List<(string, Brush)>();
        foreach (Match part in JsonPart().Matches(source))
        {
            Brush brush = part.Groups["key"].Success ? Tag
                : part.Groups["string"].Success ? Str
                : part.Groups["number"].Success ? Number
                : part.Groups["word"].Success ? Operator
                : part.Groups["space"].Success ? Plain
                : Punctuation;
            runs.Add((part.Value, brush));
        }
        return runs;
    }

    private static List<(string, Brush)> Markdown(string source)
    {
        var runs = new List<(string, Brush)>();
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i > 0) runs.Add(("\n", Plain));
            if (line.TrimStart().StartsWith('#')) runs.Add((line, Tag));
            else if (line.TrimStart().StartsWith('>')) runs.Add((line, Comment));
            else
            {
                var bullet = MarkdownBullet().Match(line);
                var rest = line;
                if (bullet.Success)
                {
                    runs.Add((bullet.Value, Tag));
                    rest = line[bullet.Length..];
                }
                var from = 0;
                foreach (Match part in MarkdownInline().Matches(rest))
                {
                    if (part.Index > from) runs.Add((rest[from..part.Index], Plain));
                    Brush brush = part.Groups["bold"].Success ? Constant : part.Groups["italic"].Success ? Keyword : part.Groups["code"].Success ? Str : Keyword;
                    runs.Add((part.Value, brush));
                    from = part.Index + part.Length;
                }
                if (from < rest.Length) runs.Add((rest[from..], Plain));
            }
        }
        return runs;
    }

    private static List<(string, Brush)> Markup(string source)
    {
        var runs = new List<(string, Brush)>();
        var from = 0;
        foreach (Match tag in MarkupTag().Matches(source))
        {
            if (tag.Index > from) runs.Add((source[from..tag.Index], Plain));
            if (tag.Value.StartsWith("<!--", StringComparison.Ordinal))
            {
                runs.Add((tag.Value, Comment));
            }
            else
            {
                runs.Add((tag.Groups["open"].Value, Punctuation));
                runs.Add((tag.Groups["name"].Value, Tag));
                foreach (Match part in MarkupAttribute().Matches(tag.Groups["rest"].Value))
                {
                    Brush brush = part.Groups["attr"].Success ? Attribute : part.Groups["value"].Success ? Str : part.Groups["space"].Success ? Plain : Punctuation;
                    runs.Add((part.Value, brush));
                }
                runs.Add((tag.Groups["close"].Value, Punctuation));
            }
            from = tag.Index + tag.Length;
        }
        if (from < source.Length) runs.Add((source[from..], Plain));
        return runs;
    }

    private static List<(string, Brush)> Css(string source)
    {
        var runs = new List<(string, Brush)>();
        var inside = false;
        foreach (Match part in CssPart().Matches(source))
        {
            var text = part.Value;
            Brush brush;
            if (part.Groups["comment"].Success) brush = Comment;
            else if (part.Groups["string"].Success) brush = Str;
            else if (text == "{") { inside = true; brush = Punctuation; }
            else if (text == "}") { inside = false; brush = Punctuation; }
            else if (part.Groups["word"].Success)
            {
                var next = source.AsSpan(part.Index + part.Length).TrimStart();
                brush = !inside ? (text.StartsWith('.') ? Attribute : text.StartsWith('#') ? Function : Tag)
                    : next.StartsWith(":") ? Operator
                    : char.IsDigit(text[0]) || text.StartsWith('#') ? Number
                    : Number;
            }
            else if (part.Groups["space"].Success) brush = Plain;
            else brush = Punctuation;
            runs.Add((text, brush));
        }
        return runs;
    }

    private static List<(string, Brush)> Shell(string source)
    {
        var runs = new List<(string, Brush)>();
        foreach (var (line, index) in source.Split('\n').Select((line, index) => (line, index)))
        {
            if (index > 0) runs.Add(("\n", Plain));
            var first = true;
            foreach (Match part in ShellPart().Matches(line))
            {
                Brush brush;
                if (part.Groups["comment"].Success) brush = Comment;
                else if (part.Groups["string"].Success) brush = Str;
                else if (part.Groups["variable"].Success) brush = Variable;
                else if (part.Groups["flag"].Success) brush = Operator;
                else if (part.Groups["word"].Success)
                {
                    brush = first ? Function : Constants.Contains(part.Value) ? Number : Plain;
                    first = false;
                }
                else if (part.Groups["space"].Success) brush = Plain;
                else
                {
                    if (part.Value is "|" or "&&" or ";" or "||") first = true;
                    brush = Operator;
                }
                runs.Add((part.Value, brush));
            }
        }
        return runs;
    }

    private static List<(string, Brush)> Settings(string source)
    {
        var runs = new List<(string, Brush)>();
        foreach (var (line, index) in source.Split('\n').Select((line, index) => (line, index)))
        {
            if (index > 0) runs.Add(("\n", Plain));
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                runs.Add((line, Comment));
                continue;
            }
            var key = SettingKey().Match(line);
            if (!key.Success)
            {
                runs.Add((line, trimmed.StartsWith('[') ? Type : Plain));
                continue;
            }
            runs.Add((key.Groups[1].Value, Tag));
            runs.Add((key.Groups[2].Value, Punctuation));
            var value = line[key.Length..];
            var bare = value.Trim();
            runs.Add((value, bare.StartsWith('"') || bare.StartsWith('\'') ? Str : double.TryParse(bare, out _) || Constants.Contains(bare) ? Number : Plain));
        }
        return runs;
    }

    [GeneratedRegex(@"\$\{([^}]*)\}")]
    private static partial Regex TemplatePart();

    [GeneratedRegex(@"(?<key>""(?:[^""\\]|\\.)*""(?=\s*:))|(?<string>""(?:[^""\\]|\\.)*"")|(?<number>-?\d[\d.eE+-]*)|(?<word>true|false|null)|(?<space>\s+)|(?<mark>.)")]
    private static partial Regex JsonPart();

    [GeneratedRegex(@"^\s*([-*+]|\d+[.)])\s")]
    private static partial Regex MarkdownBullet();

    [GeneratedRegex(@"(?<bold>\*\*[^*]+\*\*)|(?<code>`[^`]+`)|(?<italic>(?<![\w*])\*[^*\s][^*]*\*)|(?<link>\]\([^)]*\))")]
    private static partial Regex MarkdownInline();

    [GeneratedRegex(@"<!--[\s\S]*?-->|(?<open></?)(?<name>[\w:.-]+)(?<rest>[^>]*?)(?<close>/?>)")]
    private static partial Regex MarkupTag();

    [GeneratedRegex(@"(?<attr>[\w:.-]+)(?==)|(?<value>""[^""]*""|'[^']*')|(?<space>\s+)|(?<mark>.)")]
    private static partial Regex MarkupAttribute();

    [GeneratedRegex(@"(?<comment>/\*[\s\S]*?\*/)|(?<string>""[^""]*""|'[^']*')|(?<word>[#.]?[\w-]+%?)|(?<space>\s+)|(?<mark>.)")]
    private static partial Regex CssPart();

    [GeneratedRegex(@"(?<comment>#.*$)|(?<string>""(?:[^""\\]|\\.)*""|'[^']*')|(?<variable>\$\{?[\w:]+\}?)|(?<flag>(?<=\s)--?[\w-]+)|(?<word>[\w./\\:-]+)|(?<space>\s+)|(?<mark>&&|\|\||.)")]
    private static partial Regex ShellPart();

    [GeneratedRegex(@"^(\s*[\w.-]+)(\s*[:=])")]
    private static partial Regex SettingKey();
}
