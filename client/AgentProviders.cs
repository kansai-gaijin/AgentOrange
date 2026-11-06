using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrange;

public enum ProviderKind { Claude, Codex }

public sealed record AgentProvider(string Id, ProviderKind Kind, string Label, string Program, string LoginFolder, bool Enabled);

public sealed record ProviderStatus(bool ProgramFound, string? Version, bool SignedIn, string Detail);

/// <summary>
/// The agent programs this PC can run, each with its own login folder so they
/// never touch the person's normal setup. These stay on this PC, because they
/// hold local paths. The programs' own status commands are the source of truth
/// for whether they're signed in.
/// </summary>
public sealed partial class AgentProviders(LocalDatabase database, string agentFolder)
{
    private const string Setting = "agent_providers";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Agents use subscription logins. They must never quietly pick up a paid API key.
    private static readonly string[] KeysToHide = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "CODEX_API_KEY"];

    public static string DefaultProgram(ProviderKind kind) => kind == ProviderKind.Claude ? "claude" : "codex";

    public static string KindName(ProviderKind kind) => kind == ProviderKind.Claude ? "Claude Code" : "Codex";

    public List<AgentProvider> List()
    {
        var stored = database.GetSetting(Setting);
        return string.IsNullOrEmpty(stored)
            ? []
            : JsonSerializer.Deserialize<List<AgentProvider>>(stored, JsonOptions) ?? [];
    }

    public void Save(AgentProvider provider)
    {
        var providers = List();
        var index = providers.FindIndex(existing => existing.Id == provider.Id);
        if (index >= 0) providers[index] = provider;
        else providers.Add(provider);
        Store(providers);
    }

    /// <summary>Forgets a provider. Its login folder is left where it is.</summary>
    public void Remove(string id)
    {
        var providers = List();
        providers.RemoveAll(provider => provider.Id == id);
        Store(providers);
    }

    public string NewLoginFolder(ProviderKind kind)
    {
        var root = Path.Combine(agentFolder, "logins");
        var prefix = kind == ProviderKind.Claude ? "claude" : "codex";
        var taken = List().Select(provider => provider.LoginFolder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var folder = Path.Combine(root, $"{prefix}-{number}");
            if (!taken.Contains(folder) && !Directory.Exists(folder)) return folder;
        }
    }

    public async Task<ProviderStatus> CheckAsync(AgentProvider provider)
    {
        var version = await RunAsync(provider, "--version");
        if (version is null || version.ExitCode != 0)
        {
            return new ProviderStatus(false, null, false, $"Can't run \"{provider.Program}\"");
        }

        var versionNumber = VersionNumber().Match(version.Output).Value;
        var status = await RunAsync(provider, provider.Kind == ProviderKind.Claude ? "auth status" : "login status");
        if (status is null) return new ProviderStatus(true, versionNumber, false, "Couldn't check the login");

        var (signedIn, detail) = provider.Kind == ProviderKind.Claude
            ? ReadClaudeStatus(status.Output)
            : ReadCodexStatus(status.Output);
        return new ProviderStatus(true, versionNumber, signedIn, detail);
    }

    /// <summary>Runs Codex's sign-in, which opens the browser, and waits for it to finish.</summary>
    public async Task<bool> SignInCodexAsync(AgentProvider provider, CancellationToken cancel)
    {
#if DEBUG
        if (Headless.Active) return false; // It would open a browser on someone's screen.
#endif
        using var process = Process.Start(StartInfo(provider, "login", hidden: true));
        if (process is null) return false;

        try
        {
            await process.WaitForExitAsync(cancel);
            return process.ExitCode == 0 && (await CheckAsync(provider)).SignedIn;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return false;
        }
    }

    /// <summary>Claude Code signs in interactively, so it gets a terminal of its own.</summary>
    public static void OpenClaudeSignIn(AgentProvider provider)
    {
#if DEBUG
        if (Headless.Active) return; // It would open a terminal on someone's screen.
#endif
        var info = StartInfo(provider, "auth login", hidden: false);
        info.Arguments = info.Arguments.Replace("/c ", "/k ");
        Process.Start(info)?.Dispose();
    }

    public async Task<bool> SignOutAsync(AgentProvider provider)
    {
        var result = await RunAsync(provider, provider.Kind == ProviderKind.Claude ? "auth logout" : "logout");
        return result is { ExitCode: 0 };
    }

    private void Store(List<AgentProvider> providers) =>
        database.SetSetting(Setting, JsonSerializer.Serialize(providers, JsonOptions));

    private static (bool SignedIn, string Detail) ReadClaudeStatus(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            if (!root.TryGetProperty("loggedIn", out var loggedIn) || !loggedIn.GetBoolean()) return (false, "Not signed in");

            var method = root.TryGetProperty("authMethod", out var authMethod) ? authMethod.GetString() : null;
            if (method != "claude.ai") return (false, "Signed in without a subscription");

            var plan = root.TryGetProperty("subscriptionType", out var subscription) ? subscription.GetString() : null;
            return (true, string.IsNullOrEmpty(plan) ? "Claude subscription" : $"Claude {Capitalize(plan)} subscription");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return (false, "Couldn't read the login status");
        }
    }

    private static (bool SignedIn, string Detail) ReadCodexStatus(string output)
    {
        if (output.Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase)) return (true, "ChatGPT subscription");
        if (output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)) return (false, "Not signed in");
        return output.Contains("Logged in", StringComparison.OrdinalIgnoreCase)
            ? (false, "Signed in without a subscription")
            : (false, "Couldn't read the login status");
    }

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private sealed record CommandResult(int ExitCode, string Output);

    private static async Task<CommandResult?> RunAsync(AgentProvider provider, string arguments)
    {
        using var timeout = new CancellationTokenSource(CommandTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(StartInfo(provider, arguments, hidden: true));
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(process.ExitCode, await output + await errors);
        }
        catch (OperationCanceledException)
        {
            process?.Kill(entireProcessTree: true);
            return null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't run {provider.Program}: {error.Message}");
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    // Goes through cmd because npm installs these programs as .cmd files.
    private static ProcessStartInfo StartInfo(AgentProvider provider, string arguments, bool hidden)
    {
        Directory.CreateDirectory(provider.LoginFolder);

        var shell = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec
            ? comSpec
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        var info = new ProcessStartInfo(shell, $"/d /s /c \"\"{provider.Program}\" {arguments}\"")
        {
            WorkingDirectory = provider.LoginFolder,
            UseShellExecute = false,
            CreateNoWindow = hidden,
            RedirectStandardOutput = hidden,
            RedirectStandardError = hidden,
        };
        info.Environment[provider.Kind == ProviderKind.Claude ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME"] = provider.LoginFolder;
        info.Environment["NoDefaultCurrentDirectoryInExePath"] = "1"; // Only ever run what's on PATH.
        foreach (var key in KeysToHide) info.Environment.Remove(key);
        return info;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();
}
