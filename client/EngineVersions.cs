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
    private const string UnitySettingsFolder = "ProjectSettings";
    private const string UnityVersionFile = "ProjectVersion.txt";

    private static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0 };

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Whether this engine's projects say what version they are.</summary>
    public static bool CanRead(string engine) => engine is "Unreal Engine" or "Godot" or "Unity";

    /// <summary>Which engine a folder's own project file is for, if it has one.</summary>
    public static Task<string?> DetectAsync(string folder) => Task.Run(() =>
        Find(folder, "*.uproject", _ => "Unreal Engine") ?? Find(folder, "project.godot", _ => "Godot") ?? Find(folder, UnityVersionFile, _ => "Unity", UnitySettingsFolder));

    public static Task<string?> FindAsync(string engine, string folder) => engine switch
    {
        "Unreal Engine" => Task.Run(() => Find(folder, "*.uproject", FromUnrealProject)),
        "Godot" => Task.Run(() => Find(folder, "project.godot", FromGodotProject)),
        "Unity" => Task.Run(() => Find(folder, UnityVersionFile, FromUnityProject, UnitySettingsFolder)),
        _ => Task.FromResult<string?>(null),
    };

    // A repo often keeps the game one folder down, next to its docs or tools. One folder that
    // can't be read doesn't stop the others being looked at.
    private static string? Find(string folder, string pattern, Func<string, string?> parse, string? inside = null)
    {
        if (!Directory.Exists(folder)) return null;

        var places = new List<string> { folder };
        try
        {
            // Links aren't followed, because one could lead onto a network share.
            places.AddRange(Directory.EnumerateDirectories(folder, "*", Everything).Where(path => !Path.GetFileName(path).StartsWith('.') && !SshConfigFile.IsLink(path)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write($"Couldn't look through a project folder: {error.Message}");
        }

        foreach (var place in places)
        {
            try
            {
                // Links aren't read, because opening one could reach a network share and send
                // the person's Windows login to whoever runs it.
                var within = inside is null ? place : Path.Combine(place, inside);
                if (inside is not null && (!Directory.Exists(within) || SshConfigFile.IsLink(within))) continue;
                var file = Directory.EnumerateFiles(within, pattern, Everything).FirstOrDefault(path => !SshConfigFile.IsLink(path));
                if (file is null || new FileInfo(file).Length > MaxProjectFileBytes) continue;
                if (parse(File.ReadAllText(file)) is { } found) return found;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Write($"Couldn't read the engine version: {error.Message}");
            }
        }
        return null;
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

    private static string? FromUnityProject(string text) => UnityVersion().Match(text) is { Success: true } version ? version.Groups[1].Value : null;

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

    [GeneratedRegex(@"^m_EditorVersion:\s*(\d+\.\d+(?:\.\d+)?(?:[xabfp]\d+(?:c\d+)?)?)\s*$", RegexOptions.Multiline)]
    private static partial Regex UnityVersion();

    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial Regex VersionNumber();

    [GeneratedRegex(@"^config/features\s*=.*$", RegexOptions.Multiline)]
    private static partial Regex GodotFeatures();

    [GeneratedRegex(@"""(\d+(?:\.\d+){1,3})""")]
    private static partial Regex QuotedVersion();

    [GeneratedRegex(@"^config_version\s*=\s*(\d+)", RegexOptions.Multiline)]
    private static partial Regex GodotConfigVersion();
}
