using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>An Unreal Engine installed on this PC: its version, such as 5.9, and its folder.</summary>
public sealed record UnrealInstall(string Version, string Folder)
{
    public string Editor => Path.Combine(Folder, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
}

/// <summary>
/// A game's Unreal project, made the way Unreal's own New Project wizard makes one from its Blank
/// C++ template (the template's TemplateDefs.ini says how), and ready for agents: Unreal's MCP
/// server starts on its own on Agent Orange's port, with all of Unreal's toolsets for it turned on.
/// </summary>
public static partial class UnrealProject
{
    /// <summary>The port Unreal's MCP server is given in projects Agent Orange makes or opens.</summary>
    public const int McpPort = 8666;

    private const string Template = "TP_Blank";

    /// <summary>The engines installed on this PC, newest first.</summary>
    public static IReadOnlyList<UnrealInstall> Installed()
    {
        var folders = new List<string>();

        // The Epic Games launcher's own list of what it installed.
        try
        {
            var listed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
            if (File.Exists(listed) && JsonNode.Parse(File.ReadAllText(listed))?["InstallationList"] is JsonArray list)
            {
                folders.AddRange(list.Select(item => item?["InstallLocation"]?.GetValue<string>()).OfType<string>());
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            AppLog.Write($"Couldn't read the Epic Games launcher's list: {error.Message}");
        }

        // And where it usually puts them, for one installed some other way.
        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        if (Directory.Exists(epic)) folders.AddRange(Directory.GetDirectories(epic, "UE_*"));

        return folders
            .Select(folder => Path.GetFullPath(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(folder => VersionOf(folder) is { } version ? new UnrealInstall(version, folder) : null)
            .OfType<UnrealInstall>()
            .Where(install => File.Exists(install.Editor))
            .GroupBy(install => install.Version)
            .Select(same => same.First())
            .OrderByDescending(install => Version.TryParse(install.Version, out var number) ? number : new Version(0, 0))
            .ToList();
    }

    // As its Build.version says, such as 5.9.
    private static string? VersionOf(string folder)
    {
        try
        {
            var build = Path.Combine(folder, "Engine", "Build", "Build.version");
            if (!File.Exists(build)) return null;
            var json = JsonNode.Parse(File.ReadAllText(build));
            return $"{json?["MajorVersion"]?.GetValue<int>()}.{json?["MinorVersion"]?.GetValue<int>()}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The engine a project is for, by the version its .uproject names.</summary>
    public static UnrealInstall? EngineFor(string uproject)
    {
        try
        {
            var association = JsonNode.Parse(File.ReadAllText(uproject))?["EngineAssociation"]?.GetValue<string>() ?? "";
            return Installed().FirstOrDefault(install => install.Version == association);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string? Find(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.uproject").FirstOrDefault() : null;

    /// <summary>
    /// A name Unreal takes for a project: letters and digits, starting with a letter, as its
    /// wizard allows, and at most 20 long, as longer ones make paths too long when it builds.
    /// </summary>
    public static string NameFor(string projectName)
    {
        var name = NotNameCharacters().Replace(projectName, "");
        name = name.TrimStart("0123456789".ToCharArray());
        if (name.Length == 0) name = "Game";
        return name.Length > 20 ? name[..20] : name;
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NotNameCharacters();

    /// <summary>
    /// Makes the project in the folder and builds it, so Unreal opens it without asking to. Says
    /// what went wrong, or null once it's done. Nothing already in the folder is overwritten.
    /// </summary>
    public static async Task<string?> CreateAsync(UnrealInstall engine, string folder, string name, Action<string> say, CancellationToken cancel)
    {
        var template = Path.Combine(engine.Folder, "Templates", Template);
        if (!Directory.Exists(template)) return $"Unreal {engine.Version} has no Blank C++ template ({Template}).";
        if (Find(folder) is { } existing) return $"This folder already has an Unreal project, {Path.GetFileName(existing)}.";

        // Every file it would make, worked out first, so nothing is half made over what's there.
        var copies = new List<(string From, string To)>();
        foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(template, file);
            if (Skipped(relative)) continue;
            copies.Add((file, Path.Combine(folder, Renamed(relative, name))));
        }
        var uproject = Path.Combine(folder, $"{name}.uproject");
        var mcpSettings = Path.Combine(folder, "Config", "DefaultEditorPerProjectUserSettings.ini");
        if (copies.Select(copy => copy.To).Append(uproject).Append(mcpSettings).FirstOrDefault(File.Exists) is { } taken)
        {
            return $"The folder already has {Path.GetRelativePath(folder, taken)}, which the Unreal project would replace.";
        }

        say("Copying Unreal's Blank C++ template…");
        foreach (var (from, to) in copies)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (Path.GetExtension(from).TrimStart('.').ToLowerInvariant() is "cpp" or "h" or "ini" or "cs")
            {
                await File.WriteAllTextAsync(to, ReplaceName(await File.ReadAllTextAsync(from, cancel), name), new UTF8Encoding(false), cancel);
            }
            else
            {
                File.Copy(from, to);
            }
        }

        // The project file, as the wizard writes it: the template's, for this engine and name.
        var project = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(template, $"{Template}.uproject"), cancel))!.AsObject();
        project["EngineAssociation"] = engine.Version;
        project["Modules"] = new JsonArray(new JsonObject { ["Name"] = name, ["Type"] = "Runtime", ["LoadingPhase"] = "Default" });
        var plugins = project["Plugins"] as JsonArray ?? [];
        project["Plugins"] = plugins;
        foreach (var plugin in new[] { "ModelContextProtocol", "AllToolsets" })
        {
            if (!plugins.Any(item => item?["Name"]?.GetValue<string>() == plugin)) plugins.Add(new JsonObject { ["Name"] = plugin, ["Enabled"] = true });
        }
        await File.WriteAllTextAsync(uproject, project.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("  ", "\t"), new UTF8Encoding(false), cancel);

        // Its own ID, which the wizard gives every new project.
        var game = Path.Combine(folder, "Config", "DefaultGame.ini");
        await File.AppendAllTextAsync(game, $"\n[/Script/EngineSettings.GeneralProjectSettings]\nProjectID={Guid.NewGuid():N}\nProjectName={name}\n", cancel);

        // Unreal's MCP server, started with the editor on Agent Orange's port, whoever opens it.
        await File.WriteAllTextAsync(mcpSettings, $"[/Script/ModelContextProtocol.ModelContextProtocolSettings]\nbAutoStartServer=True\nServerPortNumber={McpPort}\n", cancel);

        await AddToGitIgnoreAsync(folder, cancel);
        await AddLfsAttributesAsync(folder, cancel);

        say($"Building {name} for Unreal {engine.Version}…");
        return await BuildAsync(engine, uproject, name, say, cancel);
    }

    // What the template's TemplateDefs.ini leaves out of a new project.
    private static bool Skipped(string relative)
    {
        var path = relative.Replace('\\', '/');
        return path.StartsWith("Media/", StringComparison.OrdinalIgnoreCase)
            || path.Equals($"{Template}.uproject", StringComparison.OrdinalIgnoreCase)
            || path.Equals("Config/TemplateDefs.ini", StringComparison.OrdinalIgnoreCase)
            || path.Equals("Config/config.ini", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path) is "Manifest.json" or "contents.txt";
    }

    private static string Renamed(string relative, string name) =>
        Regex.Replace(relative, Regex.Escape(Template), name.Replace("$", "$$"), RegexOptions.IgnoreCase);

    // Upper case, lower case, then any case, as its ReplacementsInFiles go.
    private static string ReplaceName(string text, string name) =>
        Regex.Replace(
            text.Replace(Template.ToUpperInvariant(), name.ToUpperInvariant()).Replace(Template.ToLowerInvariant(), name.ToLowerInvariant()),
            Regex.Escape(Template), name.Replace("$", "$$"), RegexOptions.IgnoreCase);

    // What Unreal makes as it works, which doesn't belong in the repo.
    private static async Task AddToGitIgnoreAsync(string folder, CancellationToken cancel)
    {
        string[] ignored = ["Binaries/", "Intermediate/", "Saved/", "DerivedDataCache/", "Plugins/*/Binaries/", "Plugins/*/Intermediate/", ".vs/", "*.sln", "*.VC.db", "*.opensdf", "*.sdf", "*.suo"];
        var path = Path.Combine(folder, ".gitignore");
        var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, cancel) : [];
        var missing = ignored.Where(line => !lines.Contains(line)).ToList();
        if (missing.Count == 0) return;
        var start = lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[^1]) ? "\n" : "";
        await File.AppendAllTextAsync(path, $"{start}# Unreal Engine\n{string.Join("\n", missing)}\n", cancel);
    }

    /// <summary>Unreal's assets and the art and sound that go into them are big binary files, which plain
    /// Git keeps whole in every clone forever. These go through Git LFS instead, from the first commit.</summary>
    public static readonly string[] LfsPatterns =
    [
        "*.uasset", "*.umap", "*.ubulk", "*.uexp", "*.upk",
        "*.png", "*.jpg", "*.jpeg", "*.tga", "*.psd", "*.exr", "*.hdr", "*.tif", "*.tiff", "*.bmp",
        "*.fbx", "*.obj", "*.glb", "*.gltf", "*.blend", "*.abc", "*.usd", "*.usdz",
        "*.wav", "*.mp3", "*.ogg", "*.flac", "*.mp4", "*.mov", "*.ttf", "*.otf",
    ];

    private static async Task AddLfsAttributesAsync(string folder, CancellationToken cancel)
    {
        var path = Path.Combine(folder, ".gitattributes");
        var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, cancel) : [];
        var missing = LfsPatterns.Where(pattern => !lines.Any(line => line.StartsWith(pattern + " ", StringComparison.Ordinal))).ToList();
        if (missing.Count == 0) return;
        var start = lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[^1]) ? "\n" : "";
        var added = string.Join("\n", missing.Select(pattern => $"{pattern} filter=lfs diff=lfs merge=lfs -text"));
        await File.AppendAllTextAsync(path, $"{start}# Unreal assets, art and sound through Git LFS\n{added}\n", cancel);
    }

    /// <summary>Builds the editor's copy of the project's code, saying how far it's got.</summary>
    public static async Task<string?> BuildAsync(UnrealInstall engine, string uproject, string name, Action<string> say, CancellationToken cancel)
    {
        var build = Path.Combine(engine.Folder, "Engine", "Build", "BatchFiles", "Build.bat");
        var start = new ProcessStartInfo(build) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(uproject)! };
        foreach (var argument in new[] { $"{name}Editor", "Win64", "Development", $"-Project={uproject}", "-WaitMutex" }) start.ArgumentList.Add(argument);

        var said = new List<string>();
        try
        {
            using var run = Process.Start(start)!;
            using var stop = cancel.Register(() =>
            {
                try
                {
                    run.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            });
            async Task Read(StreamReader from)
            {
                while (await from.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    lock (said) said.Add(line);
                    // Its steps, such as "[3/12] Compile Module.AnimalPlanet.cpp".
                    if (line.StartsWith('[')) say($"Building {name}… {line}");
                }
            }
            await Task.WhenAll(Read(run.StandardOutput), Read(run.StandardError), run.WaitForExitAsync(CancellationToken.None));
            cancel.ThrowIfCancellationRequested();
            AppLog.Write($"Unreal build of {name}: {run.ExitCode}\n{string.Join("\n", said.TakeLast(40))}");
            if (run.ExitCode == 0) return null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Couldn't run Unreal's build: {error.Message}";
        }

        // Its errors, or else its last words.
        var errors = said.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
        return $"Unreal couldn't build {name}:\n{string.Join("\n", errors.Count > 0 ? errors : said.TakeLast(6))}";
    }

    /// <summary>
    /// Opens the project in its engine, with Unreal's MCP server on Agent Orange's port, on its own, so
    /// the editor stays open when Agent Orange restarts (#101).
    /// </summary>
    public static async Task<string?> OpenAsync(string uproject)
    {
        if (EngineFor(uproject) is not { } engine) return $"The Unreal version {Path.GetFileName(uproject)} is for isn't installed on this PC.";
        var (_, problem) = await DetachedProcess.StartAsync(engine.Editor, [uproject, "-ModelContextProtocolStartServer", $"-ModelContextProtocolPort={McpPort}"], Path.GetDirectoryName(uproject));
        return problem is null ? null : $"Unreal didn't start: {problem}";
    }
}
