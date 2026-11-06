using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// A project's engine version is whatever its own files say, so it's never typed in. Null
/// means the files don't say, such as for a new project that has no engine project yet.
/// </summary>
public static partial class EngineVersions
{
    private const string CustomBuild = "custom build";
    private const int MaxProjectFileBytes = 1_000_000;

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Task<string?> FindAsync(string engine, string folder) => engine switch
    {
        "Unreal Engine" => Task.Run(() => Find(folder, "*.uproject", FromUnrealProject)),
        "Godot" => Task.Run(() => Find(folder, "project.godot", FromGodotProject)),
        _ => Task.FromResult<string?>(null),
    };

    // A repo often keeps the game one folder down, next to its docs or tools.
    private static string? Find(string folder, string pattern, Func<string, string?> parse)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;

            var places = new[] { folder }.Concat(
                Directory.EnumerateDirectories(folder).Where(path => !Path.GetFileName(path).StartsWith('.')));
            foreach (var place in places)
            {
                var file = Directory.EnumerateFiles(place, pattern).FirstOrDefault();
                if (file is null) continue;
                return new FileInfo(file).Length > MaxProjectFileBytes ? null : parse(File.ReadAllText(file));
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write($"Couldn't read the engine version: {error.Message}");
            return null;
        }
    }

    // EngineAssociation is a version for an installed engine, and an ID for one built from source.
    private static string? FromUnrealProject(string text)
    {
        try
        {
            using var project = JsonDocument.Parse(text, LenientJson);
            if (project.RootElement.ValueKind != JsonValueKind.Object
                || !project.RootElement.TryGetProperty("EngineAssociation", out var association)
                || association.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = association.GetString()?.Trim() ?? "";
            if (value.Length == 0) return null;
            return VersionNumber().IsMatch(value) ? value : CustomBuild;
        }
        catch (JsonException error)
        {
            AppLog.Write($"Couldn't read an Unreal project file: {error.Message}");
            return null;
        }
    }

    // Godot 4 lists its version among the project's features. Godot 3 doesn't, but it has
    // its own number for the file's format.
    private static string? FromGodotProject(string text)
    {
        var features = GodotFeatures().Match(text);
        if (features.Success)
        {
            var version = QuotedVersion().Match(features.Value);
            if (version.Success) return version.Groups[1].Value;
        }

        return GodotConfigVersion().Match(text) switch
        {
            { Success: true, Groups: var groups } when groups[1].Value == "5" => "4",
            { Success: true, Groups: var groups } when groups[1].Value == "4" => "3",
            _ => null,
        };
    }

    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial Regex VersionNumber();

    [GeneratedRegex(@"^config/features\s*=.*$", RegexOptions.Multiline)]
    private static partial Regex GodotFeatures();

    [GeneratedRegex(@"""(\d+(?:\.\d+){1,3})""")]
    private static partial Regex QuotedVersion();

    [GeneratedRegex(@"^config_version\s*=\s*(\d+)", RegexOptions.Multiline)]
    private static partial Regex GodotConfigVersion();
}
