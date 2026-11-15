using System.Diagnostics;
using System.IO;
using System.Text;

namespace AgentOrange;

/// <summary>
/// Blender Lab's official Blender MCP (projects.blender.org/lab/blender_mcp), set up by Agent Orange:
/// its "mcp" extension, built from source and installed into Blender, which runs a socket server in
/// Blender and starts on its own (on localhost:9876); and its "blender-mcp" MCP server, put in a
/// Python environment of Agent Orange's own, which agents run and which talks to that socket.
/// </summary>
internal static class BlenderMcp
{
    public const string Version = "v1.0.3";
    private const string Repository = "https://projects.blender.org/lab/blender_mcp.git";

    private static string Folder(string agentFolder) => Path.Combine(agentFolder, "blender-mcp");

    /// <summary>The MCP server agents run for Blender, once it's set up.</summary>
    public static string ServerProgram(string agentFolder) => Path.Combine(Folder(agentFolder), "python", "Scripts", "blender-mcp.exe");

    private static string Done(string agentFolder, string blenderProgram) =>
        Path.Combine(Folder(agentFolder), $"installed-{Version}-{BlenderVersion(blenderProgram)}.txt");

    // Such as 5.3, as Blender keeps an extension for each version apart.
    private static string BlenderVersion(string blenderProgram) =>
        FileVersionInfo.GetVersionInfo(blenderProgram).ProductVersion is { } version && version.Split('.') is [var major, var minor, ..] ? $"{major}.{minor}" : "unknown";

    /// <summary>
    /// Run as Blender starts (--python-expr), so no splash covers the window an agent looks at: it's
    /// turned off for this start only, and the person's own preference is put back, unsaved.
    /// </summary>
    public const string WithoutSplash = """
        import bpy
        p = bpy.context.preferences
        shown, dirty = p.view.show_splash, p.is_dirty
        p.view.show_splash = False
        def back():
            p.view.show_splash = shown
            p.is_dirty = dirty
        bpy.app.timers.register(back, first_interval=1.0)
        """;

    public static bool IsSetUp(string agentFolder, string blenderProgram) =>
        File.Exists(ServerProgram(agentFolder)) && File.Exists(Done(agentFolder, blenderProgram));

    /// <summary>Sets both up, if they aren't. Says what went wrong, or null when it's ready.</summary>
    public static async Task<string?> SetUpAsync(string agentFolder, string blenderProgram, Action<string> say)
    {
        if (IsSetUp(agentFolder, blenderProgram)) return null;
        var folder = Folder(agentFolder);
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, $"source-{Version}");

        if (!Directory.Exists(source))
        {
            say("Getting Blender Lab's Blender MCP…");
            if (await RunAsync("git", folder, "clone", "--depth", "1", "--branch", Version, Repository, source) is { } failed) return $"Couldn't get Blender MCP: {failed}";
        }

        if (!File.Exists(ServerProgram(agentFolder)))
        {
            say("Setting up its MCP server…");
            var python = Path.Combine(folder, "python");
            if (!File.Exists(Path.Combine(python, "Scripts", "python.exe"))
                && await RunAsync("python", folder, "-m", "venv", python) is { } noVenv
                && await RunAsync("py", folder, "-3", "-m", "venv", python) is not null)
            {
                return $"Blender MCP's server needs Python 3.10 or newer on this PC: {noVenv}";
            }
            if (await RunAsync(Path.Combine(python, "Scripts", "python.exe"), folder, "-m", "pip", "install", "--quiet", "--disable-pip-version-check", Path.Combine(source, "mcp")) is { } noServer) return $"Couldn't set up Blender MCP's server: {noServer}";
        }

        say("Installing its extension into Blender…");
        var built = Path.Combine(folder, $"extension-{Version}");
        Directory.CreateDirectory(built);
        if (await RunAsync(blenderProgram, folder, "--command", "extension", "build", "--source-dir", Path.Combine(source, "addon", "blender_mcp_addon"), "--output-dir", built) is { } noBuild) return $"Blender couldn't build the extension: {noBuild}";
        if (Directory.EnumerateFiles(built, "*.zip").FirstOrDefault() is not { } package) return "Blender built no extension.";
        if (await RunAsync(blenderProgram, folder, "--command", "extension", "install-file", "--repo", "user_default", "--enable", package) is { } noInstall) return $"Blender couldn't install the extension: {noInstall}";

        File.WriteAllText(Done(agentFolder, blenderProgram), $"Blender MCP {Version} set up {DateTimeOffset.Now:u}\n");
        return null;
    }

    // Null when it worked, or what it said when it didn't.
    private static async Task<string?> RunAsync(string program, string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var run = Process.Start(start)!;
            var output = run.StandardOutput.ReadToEndAsync();
            var errors = run.StandardError.ReadToEndAsync();
            await run.WaitForExitAsync();
            var said = (await errors + "\n" + await output).Trim();
            AppLog.Write($"Blender MCP: {Path.GetFileName(program)} {string.Join(' ', arguments)} → {run.ExitCode}{(said.Length > 0 ? $": {Last(said)}" : "")}");
            return run.ExitCode == 0 ? null : Last(said);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return error.Message;
        }
    }

    private static string Last(string said) => said.Length > 600 ? said[^600..] : said;
}
