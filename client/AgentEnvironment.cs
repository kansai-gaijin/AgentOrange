using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace AgentOrange;

/// <summary>
/// What the programs an agent session runs are started with, whatever the provider. On a Windows
/// set to another language, compilers, .NET and Python would otherwise print in it, in the console's
/// own code page (such as cp932), so build errors reached the agent as mojibake and Python failed
/// to read files that weren't in that code page. Sessions get UTF-8 and English instead, and
/// ripgrep on PATH, which Codex expects and Claude Code only has for its own search.
/// </summary>
internal static class AgentEnvironment
{
    public static void Apply(ProcessStartInfo info, string? toolsFolder)
    {
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["VSLANG"] = "1033"; // MSVC and MSBuild, in English.
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        if (!info.Environment.ContainsKey("LANG")) info.Environment["LANG"] = "en_US.UTF-8"; // Git and Git Bash's tools.
        if (toolsFolder is not null && Ripgrep.IsIn(toolsFolder))
        {
            info.Environment["PATH"] = toolsFolder + Path.PathSeparator + (info.Environment.TryGetValue("PATH", out var path) ? path : "");
        }
    }

    /// <summary>What the session's cmd runs first, so the console its programs share writes UTF-8.</summary>
    public static string CodePageFirst => $"\"{Path.Combine(Environment.SystemDirectory, "chcp.com")}\" 65001 >nul & ";
}

/// <summary>
/// ripgrep's official release, put in the agent files folder's bin folder the first time it's
/// needed, unless rg is on PATH already. The download is checked against the release's own hash
/// before anything in it is used.
/// </summary>
internal static class Ripgrep
{
    private const string Version = "15.2.0";
    private const string Release = $"https://github.com/BurntSushi/ripgrep/releases/download/{Version}/ripgrep-{Version}-x86_64-pc-windows-msvc.zip";
    private const string Sha256 = "71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5";

    public static string Folder(string agentFolder) => Path.Combine(agentFolder, "bin");

    public static bool IsIn(string folder) => File.Exists(Path.Combine(folder, "rg.exe"));

    /// <summary>Puts rg.exe in the bin folder if it isn't on PATH or there already. Returns what went wrong, or null.</summary>
    public static async Task<string?> EnsureAsync(string agentFolder)
    {
        var folder = Folder(agentFolder);
        if (IsIn(folder) || AgentProviders.OnPath("rg")) return null;
        var work = Path.Combine(Path.GetTempPath(), "AgentOrange-ripgrep", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await http.GetByteArrayAsync(Release);
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.Ordinal))
                return "ripgrep's download didn't match its release's hash, so it wasn't used.";
            var zip = Path.Combine(work, "ripgrep.zip");
            await File.WriteAllBytesAsync(zip, bytes);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var program = archive.Entries.FirstOrDefault(entry => entry.Name.Equals("rg.exe", StringComparison.OrdinalIgnoreCase));
                if (program is null) return "ripgrep's release had no rg.exe in it.";
                Directory.CreateDirectory(folder);
                var unpacked = Path.Combine(work, "rg.exe");
                program.ExtractToFile(unpacked, overwrite: true);
                File.Move(unpacked, Path.Combine(folder, "rg.exe"), overwrite: true);
            }
            AppLog.Write($"Put ripgrep {Version} in {folder} for agent sessions.");
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Write($"Couldn't get ripgrep for agent sessions: {error.Message}");
            return error.Message;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Only a temporary folder.
            }
        }
    }
}
