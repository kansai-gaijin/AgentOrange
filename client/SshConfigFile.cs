using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// Reads an SSH settings file named by a folder's Git settings. This is done here instead of
/// by ssh, because ssh runs the commands a settings file can contain, and looking at a folder
/// someone was handed must not run anything from it. Files that need ssh's own handling can't
/// be answered for, so Include and Match count as not knowing.
/// </summary>
internal static partial class SshConfigFile
{
    private const int MaxBytes = 1_000_000;

    /// <summary>Null means this can't tell. Either value can be null if the file doesn't set it.</summary>
    public static (string? Host, string? User)? Read(string path, string host)
    {
        try
        {
            if (!IsOnThisPc(path)) return null;

            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxBytes) return null;

            string? hostName = null;
            string? user = null;
            var applies = true;
            foreach (var line in File.ReadLines(path))
            {
                var setting = Setting().Match(line);
                if (!setting.Success) continue;

                var value = setting.Groups[2].Value.Trim().Trim('"');
                switch (setting.Groups[1].Value.ToLowerInvariant())
                {
                    case "include" or "match":
                        return null;
                    case "host":
                        applies = HostApplies(value, host);
                        break;
                    case "hostname" when applies:
                        hostName ??= value.Replace("%h", host, StringComparison.Ordinal);
                        break;
                    case "user" when applies:
                        user ??= value;
                        break;
                }
            }
            return (hostName ?? host, user);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Just opening a file on a network share can send the person's Windows login to whoever
    // runs that share, and a folder someone was handed gets to name this path.
    private static bool IsOnThisPc(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
    }

    /// <summary>Works out a path the way Git's shell would, from inside the folder.</summary>
    public static string Resolve(string path, string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = path;
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal)) text = home + text[1..];
        text = text
            .Replace("${HOME}", home, StringComparison.OrdinalIgnoreCase)
            .Replace("$HOME", home, StringComparison.OrdinalIgnoreCase);
        text = Environment.ExpandEnvironmentVariables(text);
        return Path.GetFullPath(text, folder);
    }

    // The patterns on a Host line: * and ? are wildcards, and a ! pattern rules a host out.
    private static bool HostApplies(string patterns, string host)
    {
        var matched = false;
        foreach (var pattern in patterns.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var negated = pattern.StartsWith('!');
            var wanted = "^" + Regex.Escape(negated ? pattern[1..] : pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (!Regex.IsMatch(host, wanted, RegexOptions.IgnoreCase)) continue;
            if (negated) return false;
            matched = true;
        }
        return matched;
    }

    [GeneratedRegex(@"^\s*([A-Za-z]+)(?:\s*=\s*|\s+)([^#]*)")]
    private static partial Regex Setting();
}
