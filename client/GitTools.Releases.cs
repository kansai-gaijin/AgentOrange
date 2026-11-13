using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>The newest release of a repo, and the file in it that's a Windows build.</summary>
public sealed record ToolRelease(string Tag, string FileName, long Size);

/// <summary>
/// Reads and downloads GitHub releases with the GitHub CLI's login, so a private repo works as
/// long as the signed-in account can see it.
/// </summary>
public static partial class GitTools
{
    /// <summary>The account the GitHub CLI is signed in as, or null if it isn't.</summary>
    public static Task<string?> GitHubLoginAsync() => ActiveAccountAsync();

    public static bool IsRepoName(string slug) => RepoName().IsMatch(slug);

    public static async Task<(ToolRelease? Release, string? Problem)> LatestReleaseAsync(string slug)
    {
        if (!IsRepoName(slug)) return (null, "Enter a repo as owner/name.");

        var asked = await RunAsync("gh", Path.GetTempPath(), "release", "view", "--repo", slug, "--json", "tagName,assets");
        if (asked is null) return (null, "This needs the GitHub CLI (gh), which wasn't found.");
        if (asked.ExitCode != 0)
        {
            if (asked.Errors == TimedOut) return (null, "GitHub didn't answer.");
            if (asked.Errors.Contains("release not found", StringComparison.OrdinalIgnoreCase)) return (null, $"{slug} has no releases yet.");
            if (asked.Errors.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)) return (null, $"{slug} wasn't found, or this GitHub account can't see it.");
            if (asked.Errors.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)) return (null, "Sign in to the GitHub CLI first, with gh auth login.");
            return (null, FirstLine(asked.Errors) ?? "GitHub couldn't be asked.");
        }

        try
        {
            using var json = JsonDocument.Parse(asked.Output);
            var tag = json.RootElement.GetProperty("tagName").GetString() ?? "";
            var zips = json.RootElement.GetProperty("assets").EnumerateArray()
                .Select(asset => new ToolRelease(tag, asset.GetProperty("name").GetString() ?? "", asset.GetProperty("size").GetInt64()))
                .Where(asset => asset.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // One zip is the build. With several, the one that says Windows is.
            var build = zips.Count == 1 ? zips[0] : zips.FirstOrDefault(zip => WindowsBuild().IsMatch(zip.FileName));
            return build is null || tag.Length == 0
                ? (null, $"Release {tag} of {slug} has no Windows zip.")
                : (build, null);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, "GitHub's answer couldn't be read.");
        }
    }

    /// <summary>Downloads the release's file into the folder. Returns what went wrong, if anything.</summary>
    public static async Task<string?> DownloadReleaseAsync(string slug, ToolRelease release, string folder)
    {
        var asked = await RunAsync("gh", folder, "release", "download", release.Tag, "--repo", slug, "--pattern", release.FileName, "--dir", folder, "--clobber");
        if (asked is null) return "This needs the GitHub CLI (gh), which wasn't found.";
        if (asked.ExitCode == 0) return null;
        return asked.Errors == TimedOut ? "The download took too long." : FirstLine(asked.Errors) ?? "The download failed.";
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$")]
    private static partial Regex RepoName();

    [GeneratedRegex(@"win", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsBuild();
}
