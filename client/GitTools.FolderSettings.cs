using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>A folder's own Git settings, which can run programs, and what's been allowed of them.</summary>
public static partial class GitTools
{
    /// <summary>
    /// Settings in a folder that make Git run a program or send a login somewhere else when it
    /// reaches a remote. A folder can come from anyone, as a zip or on a shared drive, so these
    /// are only used once the person has seen them. Null means that couldn't be found out,
    /// which has to be treated as not being safe to use them.
    /// </summary>
    public static Task<FolderGitSettings?> FolderRunnableSettingsAsync(string folder) =>
        ReadFolderSettingsAsync(folder, FolderSettingsThatMatter, null);

    /// <summary>
    /// Finishing a download checks out files and works inside each submodule, using that one's own
    /// settings and hooks, and there are too many ways for those to run a program to list them all.
    /// So anything that isn't known to be safe needs allowing, including what a settings file
    /// includes. What a clone, Git LFS and submodules set up for themselves doesn't come up. Refused
    /// says it can't be allowed at all, and null means this couldn't be read.
    /// </summary>
    public static async Task<FinishLook?> LookBeforeFinishingAsync(string folder)
    {
        // What's remembered goes by path, and something else can have appeared there since.
        switch (await HoldsNoteFromHereAsync(folder))
        {
            case false:
                TryNoting(() => RememberUnfinishedDownload(folder, false));
                return new FinishLook(FolderGitSettings.None, "This app can't confirm this is the copy it was downloading, so nothing was run in it. If its large files or submodules are missing and you trust it, run git lfs pull and git submodule update --init --recursive in it yourself.");
            case null:
                // It may just be on a drive that's unplugged, and it's still unfinished when that's back.
                return new FinishLook(FolderGitSettings.None, "That folder can't be read right now, so nothing was run in it. Finish the download once it's available again.");
        }

        var own = await ReadSettingLinesAsync(folder, ".", setting => SafeWhileFinishing().IsMatch(setting));
        var inSubmodules = await LookThroughSubmodulesAsync(folder);
        if (own is null || inSubmodules is null) return null;
        return new FinishLook(SummedUp([.. own, .. inSubmodules.Lines]), inSubmodules.Refused);
    }

    /// <summary>
    /// What agents, and this app's own Git in the agents window and the Git actions, would use of a
    /// folder's own settings (#20). Agents run any Git command there, and the app runs status, diff,
    /// fetch, commit and push, which can start a program in many ways: core.fsmonitor, hooks, filter
    /// and diff drivers, editors, pagers, signing and credential programs, included settings files.
    /// So, as before finishing a download, anything not known to be safe needs allowing: the
    /// settings, the hooks wherever core.hooksPath puts them, each file in the folder a setting
    /// names, and what its submodules keep. Hooks and named files go in by what's in them, so a pull
    /// that changes one asks again. Refused says it can't be allowed at all, and null means this
    /// couldn't be read.
    /// </summary>
    public static async Task<FinishLook?> LookBeforeAgentsAsync(string folder)
    {
        if (!await FolderExistsAsync(folder)) return new FinishLook(FolderGitSettings.None, null);
        var own = await ReadSettingLinesAsync(folder, ".", setting => SafeWhileFinishing().IsMatch(setting) || SafeForAgents().IsMatch(setting));
        if (own is null) return null;
        var lines = new List<string>(own);
        try
        {
            lines.AddRange(await Task.Run(() => FilesNamedIn(folder, own)));

            // Where Git itself looks for hooks: core.hooksPath, or the Git data's own, wherever that is.
            var hooks = await RunAsync("git", folder, "rev-parse", "--git-path", "hooks");
            if (hooks is null) return null;
            if (hooks.ExitCode == 0 && hooks.Output.Trim() is { Length: > 0 } hooksPath)
            {
                var hooksFolder = Path.GetFullPath(hooksPath, folder);
                lines.AddRange(await Task.Run(() => HooksInFolder(hooksFolder, Path.GetRelativePath(folder, hooksFolder))));
            }

            // A folder that keeps its Git data elsewhere, as a linked worktree does, has its
            // submodules' data there too; Git's own answers above already cover its settings and hooks.
            if (await Task.Run(() => Directory.Exists(Path.Combine(folder, ".git"))))
            {
                if (await LookThroughSubmodulesAsync(folder, withOwnHooks: false) is not { } inSubmodules) return null;
                if (inSubmodules.Refused is { } refused) return new FinishLook(SummedUp(lines), refused.Replace("so nothing was run in this folder", "so agents can't work in this folder"));
                lines.AddRange(inSubmodules.Lines);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Write($"Couldn't look through a folder's Git settings for agents: {error.Message}");
            return null;
        }
        return new FinishLook(SummedUp(lines), null);
    }

    public static string AgentsApprovalKey(string folder) => $"{Path.TrimEndingDirectorySeparator(folder)}|agents";

    // A file in the folder that a setting names, such as the script a credential helper, fsmonitor
    // or sshCommand's -F points at: allowing the setting has to cover what's in it, as a pull can
    // change it. Anything the value names that isn't a file there is left to the setting's own text.
    private static List<string> FilesNamedIn(string folder, IEnumerable<string> settings)
    {
        var found = new List<string>();
        var inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        foreach (var setting in settings)
        {
            var parts = setting.Split('\n', 2);
            if (parts.Length < 2) continue;
            foreach (Match token in QuotedText().Matches(parts[1]).Concat(NamedPath().Matches(parts[1])))
            {
                var named = token.Value.Trim('\'', '"').TrimStart('!');
                if (named.Contains("://") || named.IndexOfAny(['/', '\\']) < 0 || named.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                string full;
                try
                {
                    full = Path.GetFullPath(named.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), named[2..]) : named, folder);
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }
                if (!full.StartsWith(inside, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                using var contents = File.OpenRead(full);
                found.Add($"{OneLine(parts[0])}: the file {OneLine(Path.GetRelativePath(folder, full))} ({Convert.ToHexString(SHA256.HashData(contents))[..12]})");
            }
        }
        return found.Distinct().ToList();
    }

    private const int MaxSubmoduleDepth = 8;
    private const int MaxHookBytes = 1_000_000;

    private sealed record SubmoduleLook(List<string> Lines, string? Refused);

    private static async Task<SubmoduleLook?> LookThroughSubmodulesAsync(string folder, bool withOwnHooks = true)
    {
        var lines = new List<string>();
        try
        {
            var gitDirectory = Path.Combine(folder, ".git");
            if (await Task.Run(() => File.Exists(gitDirectory))) return new SubmoduleLook(lines, "This folder keeps its Git data somewhere else, which a clone from this app never does, so what it would use can't be checked and nothing was run in it. If you trust it, run git submodule update --init --recursive and git lfs pull in the folder yourself.");
            if (!await Task.Run(() => Directory.Exists(gitDirectory))) return new SubmoduleLook(lines, null);
            if (await Task.Run(() => File.Exists(Path.Combine(gitDirectory, "commondir")))) return new SubmoduleLook(lines, CantBeUsed("This folder's Git data", "it has a commondir file, which keeps its hooks and settings somewhere else"));
            if (withOwnHooks) lines.AddRange(await Task.Run(() => HooksIn(gitDirectory, ".git")));
            var places = new HashSet<string>(await Task.Run(() => GitFolderSafety.ModuleDirectories(gitDirectory)), StringComparer.OrdinalIgnoreCase);

            // A submodule's folder can also keep its Git data itself, or say it's somewhere else.
            var waiting = new Stack<(string Folder, int Depth)>();
            waiting.Push((folder, 0));
            while (waiting.TryPop(out var worktree))
            {
                if (await IsLinkInFolderAsync(worktree.Folder, ".gitmodules")) return new SubmoduleLook(lines, CantBeUsed(".gitmodules", "it's a link"));
                if (await DeclaredSubmodulePathsAsync(worktree.Folder) is not { } paths) return null;
                foreach (var path in paths)
                {
                    var (problem, keeps, carriesOn) = await Task.Run(() => WhereSubmoduleKeepsData(folder, gitDirectory, worktree.Folder, path));
                    if (problem is not null) return new SubmoduleLook(lines, CantBeUsed($"The submodule {OneLine(path)}", problem));
                    if (keeps is not null) places.Add(keeps);
                    if (carriesOn is null) continue;
                    if (worktree.Depth >= MaxSubmoduleDepth) return new SubmoduleLook(lines, CantBeUsed($"The submodule {OneLine(path)}", "submodules go too many levels deep to check"));
                    waiting.Push((carriesOn, worktree.Depth + 1));
                }
            }

            foreach (var place in places)
            {
                if (await GitFolderSafety.SubmoduleDataProblemAsync(folder, place, ReadSettingsFileAsync) is { } refused) return new SubmoduleLook(lines, refused);

                var name = Path.GetRelativePath(folder, place);
                if (await Task.Run(() => File.Exists(Path.Combine(place, "commondir")))) return new SubmoduleLook(lines, CantBeUsed($"The Git data at {name}", "it has a commondir file, which keeps its hooks and settings somewhere else"));
                foreach (var file in new[] { "config", "config.worktree" })
                {
                    var settingsFile = Path.Combine(place, file);
                    if (!await Task.Run(() => File.Exists(settingsFile))) continue;
                    if (await ReadSettingsFileAsync(settingsFile) is not { } settings) return null;
                    if (settings.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(setting => SubmoduleInclude().IsMatch(setting.Split('\n', 2)[0])))
                    {
                        return new SubmoduleLook(lines, CantBeUsed($"The Git data at {name}", "its settings include another file, which could change after being allowed"));
                    }
                    lines.AddRange(settings.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(setting => !SafeWhileFinishing().IsMatch(setting) && !IsSubmodulesOwnWorktree(setting, place, folder)).Select(setting => $"{name}: {setting}"));
                }
                lines.AddRange(await Task.Run(() => HooksIn(place, name)));
            }
            return new SubmoduleLook(lines, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write($"Couldn't read a submodule's settings: {error.Message}");
            return null;
        }
    }

    private static string CantBeUsed(string what, string why) => $"{what} can't be used, so nothing was run in this folder: {why}. Git would follow it, so this can't be allowed. Fix that, or use another copy.";

    // What Git sets up for a submodule, which is safe as long as it stays inside the project.
    private static bool IsSubmodulesOwnWorktree(string setting, string place, string folder)
    {
        var parts = setting.Split('\n', 2);
        return parts.Length == 2 && parts[0].Equals("core.worktree", StringComparison.OrdinalIgnoreCase)
            && Path.GetFullPath(parts[1], place).StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Both copies, as Git falls back on the committed one if the one in the folder is gone.
    private static async Task<List<string>?> DeclaredSubmodulePathsAsync(string worktree)
    {
        var paths = new List<string>();
        var declared = Path.Combine(worktree, ".gitmodules");
        if (await Task.Run(() => File.Exists(declared) && !SshConfigFile.IsLink(declared)))
        {
            if (await ReadSettingsFileAsync(declared) is not { } inFolder) return null;
            paths.AddRange(SubmodulePathsIn(inFolder));
        }

        var committed = await RunAsync("git", worktree, "config", "--blob", "HEAD:.gitmodules", "-z", "--list");
        if (committed is null or { ExitCode: < 0 }) return null;
        if (committed.ExitCode == 0) paths.AddRange(SubmodulePathsIn(committed.Output));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Task<bool> IsLinkInFolderAsync(string folder, string name) => Task.Run(() =>
    {
        try
        {
            return SshConfigFile.IsLink(Path.Combine(folder, name));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    });

    private static IEnumerable<string> SubmodulePathsIn(string settings) =>
        settings.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('\n', 2)).Where(parts => parts.Length == 2 && SubmodulePathSetting().IsMatch(parts[0])).Select(parts => parts[1]);

    // Nothing here gets followed. A link on the way to one could lead onto a network share.
    private static (string? Problem, string? Keeps, string? CarriesOn) WhereSubmoduleKeepsData(string folder, string gitDirectory, string worktree, string path)
    {
        var submodule = Path.GetFullPath(Path.Combine(worktree, path));
        if (!submodule.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return ("is outside this folder", null, null);
        if (SshConfigFile.HasLinkBelowChosenPath(folder, submodule)) return ("is reached through a link", null, null);

        var entry = Path.Combine(submodule, ".git");
        if (!File.Exists(entry) && !Directory.Exists(entry)) return (null, null, null);
        if (SshConfigFile.IsLink(entry)) return ("its .git is a link", null, null);
        if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) return (null, entry, submodule);

        if (new FileInfo(entry).Length > MaxHookBytes) return ("its .git file is too big to check", null, null);
        var line = File.ReadLines(entry).FirstOrDefault(text => text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return (null, null, submodule);
        var target = line["gitdir:".Length..].Trim();
        var resolved = Path.GetFullPath(target, submodule);
        return resolved.StartsWith(Path.Combine(gitDirectory, "modules") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? (null, null, submodule) // One of the ones already being looked at.
            : ($"keeps its Git data at {target}", null, submodule);
    }

    // Anything that isn't one of Git's samples, or a hook Git LFS added for itself.
    private static List<string> HooksIn(string place, string name) => HooksInFolder(Path.Combine(place, "hooks"), name);

    private static List<string> HooksInFolder(string folder, string name)
    {
        var found = new List<string>();
        var hooks = new DirectoryInfo(folder);
        if (!hooks.Exists) return found;
        foreach (var hook in hooks.EnumerateFiles().Where(file => !file.Name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)))
        {
            if (hook.Length <= MaxHookBytes && StandardLfsHook().IsMatch(File.ReadAllText(hook.FullName))) continue;

            // Read as it goes, as one could be any size, and approving it covers everything in it.
            using var contents = hook.OpenRead();
            found.Add($"{name}: the hook {hook.Name} ({Convert.ToHexString(SHA256.HashData(contents))[..12]})");
        }
        return found;
    }

    public static string FinishApprovalKey(string folder) => $"{Path.TrimEndingDirectorySeparator(folder)}|finish";

    private static async Task<FolderGitSettings?> ReadFolderSettingsAsync(string folder, string wanted, Func<string, bool>? harmless) =>
        await ReadSettingLinesAsync(folder, wanted, harmless) is { } settings ? SummedUp(settings) : null;

    private static async Task<List<string>?> ReadSettingLinesAsync(string folder, string wanted, Func<string, bool>? harmless)
    {
        if (!await FolderExistsAsync(folder)) return [];

        // Asked for by scope, because a folder's settings can also be in a file only used per
        // worktree, or in one it includes. NUL-separated, so a value can't hide on a second line.
        var found = await RunAsync("git", folder, "config", "--show-scope", "--includes", "-z", "--get-regexp", wanted);
        if (found is null) return null;
        if (found.ExitCode == 1) return [];
        if (found.ExitCode != 0) return null;

        var parts = found.Output.Split('\0');
        var settings = new List<string>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (parts[i] is "local" or "worktree" && harmless?.Invoke(parts[i + 1]) != true) settings.Add(parts[i + 1]);
        }
        return settings;
    }

    private static FolderGitSettings SummedUp(List<string> settings)
    {
        if (settings.Count == 0) return FolderGitSettings.None;

        // Only a fingerprint is kept, because these can hold live logins.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', settings))));

        // Everything approved has to have been read, so what can't all be shown can't be approved.
        var shown = settings.Select(ForShowing).ToList();
        var changesConnection = settings.Any(setting => ConnectionSetting().IsMatch(setting.Split('\n')[0]));
        if (shown.Count > MaxSettingsShown || shown.Any(line => line.Length > MaxSettingShown)) return new FolderGitSettings("", fingerprint, false, changesConnection);
        return new FolderGitSettings(string.Join('\n', shown), fingerprint, ChangesConnection: changesConnection);
    }

    // Enough to judge what a setting does, without putting a login on the screen. A value can't
    // fake extra lines in the prompt.
    private static string ForShowing(string setting)
    {
        var parts = setting.Split('\n', 2);
        var key = OneLine(HideLogins(parts[0]));
        if (parts.Length == 1) return key;
        if (SensitiveSetting().IsMatch(parts[0])) return $"{key} (hidden)";

        return $"{key} {OneLine(HideLogins(parts[1]))}";
    }

    /// <summary>Set up when the app starts, because the answers are kept in its database.</summary>
    public static Func<string, string?> AllowedFolderSettings { get; set; } = _ => null;

    public static Action<string, string> RememberAllowedFolderSettings { get; set; } = (_, _) => { };

    public static bool AreAllowed(string folder, FolderGitSettings settings) =>
        settings.IsEmpty || (settings.CanBeApproved && AllowedFolderSettings(folder) == settings.Fingerprint);

    /// <summary>
    /// A folder whose SSH settings file needs ssh's own handling can't be understood until it's
    /// allowed to run, and until then it would just be refused. So this asks, when choosing one.
    /// Says whether it was allowed just now, as that shouldn't outlast the folder being refused.
    /// </summary>
    public static async Task<bool> AskAboutUnreadableSshSettingsAsync(string folder)
    {
        if (await InspectFolderAsync(folder) is not { NeedsSettingsApproval: true }) return false;
        if (await FolderRunnableSettingsAsync(folder) is not { IsEmpty: false, CanBeApproved: true } settings) return false;

        // A message box can't scroll, and what's approved has to have been read. Not being asked
        // leaves this saying it can't tell, so the folder's remote has to say github.com instead.
        if (settings.Shown.Length > MaxSettingsInDialog) return false;

        if (Dialogs.Confirm($"To tell whether this folder's remote is on GitHub, its SSH settings have to run, and they can run programs or send your login somewhere else:\n\n{settings.Shown}\n\nOnly use them if you trust where this folder came from. Use them?"))
        {
            RememberAllowedFolderSettings(folder, settings.Fingerprint);
            return true;
        }
        return false;
    }

    public static void ForgetAllowedFolderSettings(string folder) => RememberAllowedFolderSettings(folder, "");

    internal const string CantTellSshSettings = "Couldn't tell whether";
    private const int MaxSettingsShown = 20;
    private const int MaxSettingShown = 600;
    private const int MaxSettingsInDialog = 1000;

    private const string FolderSettingsThatMatter =
        @"^(core\.(sshcommand|askpass|gitproxy)|credential\..*|http\..*|url\..*|remote\..*\.(uploadpack|receivepack|vcs|proxy))$";
}
