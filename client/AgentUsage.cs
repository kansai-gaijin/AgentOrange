using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// One of a subscription's limits: how much of it is used, in percent, and when it starts again.
/// Blocked once it's run out, until it resets.
/// </summary>
public sealed record AgentLimit(string Kind, double? Used, DateTimeOffset? ResetsAt, bool Blocked)
{
    public string Name => Kind switch
    {
        "five_hour" => "Session",
        "seven_day" => "Weekly",
        "seven_day_opus" => "Weekly · Opus",
        "seven_day_sonnet" => "Weekly · Sonnet",
        "overage" => "Extra usage",
        _ => Kind.Replace('_', ' '),
    };

    /// <summary>How long the limit runs for, for telling whether it's being used faster than it lasts.</summary>
    public TimeSpan? Length => Kind switch
    {
        "five_hour" => TimeSpan.FromHours(5),
        _ when Kind.StartsWith("seven_day", StringComparison.Ordinal) => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>"Ahead of pace" when more is used than the time gone would allow, as T3 Code says it.</summary>
    public string? Pace
    {
        get
        {
            if (Used is not { } used || ResetsAt is not { } resets || Length is not { } length) return null;
            var gone = 100 * (1 - (resets - DateTimeOffset.UtcNow) / length);
            return used > gone + 5 ? "Ahead of pace" : used < gone - 5 ? "Under pace" : "On pace";
        }
    }
}

/// <summary>
/// Each login's subscription limits, as its agent program last said them. Claude Code says them when
/// asked (get_usage, which uses none of the limits up), and as they change while it works
/// (rate_limit_event). Without a conversation running, a program is started just to ask.
///
/// What was last heard is kept on this PC, with when, so a check that doesn't answer (as a login
/// that's run out of its limit may not) or a restart doesn't lose a login's limits and reset times.
/// Only the program's own login status says a login is signed out (#120).
/// </summary>
public sealed class AgentUsage
{
    private static readonly TimeSpan ProbeWaitsFor = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, Dictionary<string, AgentLimit>> _byProvider = [];
    private readonly Dictionary<string, DateTimeOffset> _checked = [];
    private readonly Dictionary<string, DateTimeOffset> _heard = [];
    private readonly HashSet<string> _heardSinceStarting = [];
    private readonly Dictionary<string, string> _notSignedIn = [];
    private string? _file;

    /// <summary>On the window's thread: a login's limits changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<AgentLimit> For(string? providerId) =>
        providerId is not null && _byProvider.TryGetValue(providerId, out var limits)
            ? [.. limits.Values.OrderBy(limit => limit.Kind == "five_hour" ? 0 : limit.Kind == "seven_day" ? 1 : 2)]
            : [];

    public DateTimeOffset? CheckedAt(string providerId) => _checked.TryGetValue(providerId, out var at) ? at : null;

    /// <summary>When its program last said any of its limits, here or before Agent Orange last started.</summary>
    public DateTimeOffset? HeardAt(string providerId) => _heard.TryGetValue(providerId, out var at) ? at : null;

    /// <summary>
    /// Its limits are as last heard, not as just said: the latest check didn't say them, or none
    /// has since Agent Orange started.
    /// </summary>
    public bool AsLastHeard(string providerId) =>
        HeardAt(providerId) is { } heard && (!_heardSinceStarting.Contains(providerId) || CheckedAt(providerId) is { } asked && asked > heard);

    /// <summary>Why it isn't signed in, as its program's login status says, or null when it is or that couldn't be told.</summary>
    public string? NotSignedIn(string providerId) => _notSignedIn.TryGetValue(providerId, out var why) ? why : null;

    /// <summary>When a login that ran out can be used again: the latest of its limits that's run out, or null if none has.</summary>
    public DateTimeOffset? BlockedUntil(string? providerId) =>
        For(providerId).Where(limit => limit.Blocked || limit.Used >= 100).Select(limit => limit.ResetsAt).Max();

    /// <summary>Takes what it said. A limit said without how much is used keeps what was known.</summary>
    public void Update(string providerId, IEnumerable<AgentLimit> limits, bool complete)
    {
        if (!_byProvider.TryGetValue(providerId, out var known)) known = _byProvider[providerId] = [];
        var now = DateTimeOffset.UtcNow;
        if (complete) _checked[providerId] = now;
        var said = false;
        foreach (var limit in limits)
        {
            said = true;
            known[limit.Kind] = known.TryGetValue(limit.Kind, out var before)
                ? limit with { Used = limit.Used ?? (limit.Blocked ? 100 : before.Used), ResetsAt = limit.ResetsAt ?? before.ResetsAt }
                : limit;
        }
        if (said)
        {
            _heard[providerId] = now;
            _heardSinceStarting.Add(providerId);
            _notSignedIn.Remove(providerId); // Only a login that's signed in says its limits.
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>What its program's login status said after a check that didn't say its limits: null when signed in, or when it couldn't be told.</summary>
    public void SetNotSignedIn(string providerId, string? why)
    {
        if (NotSignedIn(providerId) == why) return;
        if (why is null) _notSignedIn.Remove(providerId);
        else _notSignedIn[providerId] = why;
        Changed?.Invoke();
    }

    // Kept on this PC: each login's limits as last said, and when.
    private sealed record Kept(DateTimeOffset HeardAt, List<KeptLimit> Limits);

    private sealed record KeptLimit(string Kind, double? Used, DateTimeOffset? ResetsAt, bool Blocked);

    private static readonly System.Text.Json.JsonSerializerOptions KeptJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Keeps what's heard in this file from now on, and takes back what was kept there.</summary>
    public void KeepIn(string file)
    {
        _file = file;
        try
        {
            if (!File.Exists(file)) return;
            var kept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Kept>>(File.ReadAllText(file), KeptJson) ?? [];
            foreach (var (providerId, login) in kept)
            {
                if (_heard.ContainsKey(providerId) || login.Limits is null) continue; // Heard since, which is newer.
                _byProvider[providerId] = login.Limits.ToDictionary(limit => limit.Kind, limit => new AgentLimit(limit.Kind, limit.Used, limit.ResetsAt, limit.Blocked));
                _heard[providerId] = login.HeardAt;
            }
            Changed?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Write($"Couldn't read the usage last heard: {error.Message}");
        }
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            var kept = _heard.ToDictionary(pair => pair.Key, pair => new Kept(pair.Value, [.. For(pair.Key).Select(limit => new KeptLimit(limit.Kind, limit.Used, limit.ResetsAt, limit.Blocked))]));
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, System.Text.Json.JsonSerializer.Serialize(kept, KeptJson));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't keep the usage last heard: {error.Message}");
        }
    }

    /// <summary>The limits in a get_usage reply, whose use is already in percent.</summary>
    public static List<AgentLimit> FromUsage(JsonObject? reply)
    {
        var limits = new List<AgentLimit>();
        if (reply?["rate_limits"] is not JsonObject all) return limits;
        foreach (var (kind, value) in all)
        {
            if (value is not JsonObject limit || limit["utilization"] is not JsonValue used || !used.TryGetValue<double>(out var percent)) continue;
            DateTimeOffset? resets = limit["resets_at"] is JsonValue at && at.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
            limits.Add(new AgentLimit(kind, percent, resets, percent >= 100));
        }
        return limits;
    }

    /// <summary>The limit a rate_limit_event is about, whose use is a fraction and whose reset is in seconds.</summary>
    public static AgentLimit? FromEvent(JsonObject? info)
    {
        if (info is null) return null;
        var kind = Str(info, "rateLimitType") ?? "five_hour";
        double? used = info["utilization"] is JsonValue fraction && fraction.TryGetValue<double>(out var part) ? Math.Round(part * 100, 1) : null;
        DateTimeOffset? resets = info["resetsAt"] is JsonValue seconds && seconds.TryGetValue<double>(out var epoch) ? DateTimeOffset.FromUnixTimeSeconds((long)epoch) : null;
        var overage = Str(info, "overageStatus") is "allowed" or "allowed_warning" || IsTrue(info["isUsingOverage"]) || IsTrue(info["overageInUse"]);
        return new AgentLimit(kind, used, resets, Str(info, "status") == "rejected" && !overage);
    }

    /// <summary>
    /// A Codex snapshot's limits: its primary and secondary windows, their use in percent and their
    /// reset in seconds. Without a length, the primary is five hours and the secondary a week, as T3 Code assumes.
    /// </summary>
    public static List<AgentLimit> FromCodex(JsonObject? snapshot)
    {
        var limits = new List<AgentLimit>();
        if (snapshot is null) return limits;
        foreach (var (slot, fallback) in new[] { ("primary", 300), ("secondary", 10080) })
        {
            if (snapshot[slot] is not JsonObject window) continue;
            double? used = window["usedPercent"] is JsonValue percent && percent.TryGetValue<double>(out var value) ? value : null;
            var minutes = window["windowDurationMins"] is JsonValue length && length.TryGetValue<int>(out var mins) ? mins : fallback;
            DateTimeOffset? resets = window["resetsAt"] is JsonValue at && at.TryGetValue<double>(out var epoch) ? DateTimeOffset.FromUnixTimeSeconds((long)epoch) : null;
            var kind = minutes switch { 300 => "five_hour", 10080 => "seven_day", _ => $"{slot}_{minutes}" };
            limits.Add(new AgentLimit(kind, used, resets, used >= 100));
        }
        return limits;
    }

    /// <summary>Asks a login's agent program for its limits, without sending anything to a model. Whether it said any.</summary>
    public async Task<bool> ProbeAsync(AgentProvider provider, string folder)
    {
        var before = HeardAt(provider.Id);
        if (provider.Kind == ProviderKind.Codex) await ProbeCodexAsync(provider, folder);
        else await ProbeClaudeAsync(provider, folder);
        if (HeardAt(provider.Id) == before)
        {
            // Asked, and nothing said: what was last heard stays, marked as that.
            _checked[provider.Id] = DateTimeOffset.UtcNow;
            Changed?.Invoke();
            return false;
        }
        return true;
    }

    private async Task ProbeClaudeAsync(AgentProvider provider, string folder)
    {
        Process? process = null;
        try
        {
            Directory.CreateDirectory(folder);
            process = Process.Start(AgentProviders.SessionStartInfo(provider, "-p --input-format stream-json --output-format stream-json --verbose", folder));
            if (process is null) return;
            ChildProcesses.Track(process);
            _ = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = "usage",
                ["request"] = new JsonObject { ["subtype"] = "get_usage", ["skip_behaviors"] = true },
            }.ToJsonString());
            await process.StandardInput.FlushAsync();

            using var waiting = new CancellationTokenSource(ProbeWaitsFor);
            while (await process.StandardOutput.ReadLineAsync(waiting.Token) is { } line)
            {
                if (!line.Contains("\"control_response\"", StringComparison.Ordinal)) continue;
                if (JsonNode.Parse(line)?["response"] is not JsonObject response || Str(response, "request_id") != "usage") continue;
                if (Str(response, "subtype") == "success") Update(provider.Id, FromUsage(response["response"] as JsonObject), complete: true);
                else AppLog.Write($"{provider.Label} didn't say its usage: {Str(response, "error") ?? Str(response, "subtype") ?? "no reason given"}");
                break;
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't ask {provider.Label} about its usage: {error.Message}");
        }
        finally
        {
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
            process?.Dispose();
        }
    }

    // Codex's app server, initialised, then asked for account/rateLimits/read.
    private async Task ProbeCodexAsync(AgentProvider provider, string folder)
    {
        Process? process = null;
        try
        {
            Directory.CreateDirectory(folder);
            process = Process.Start(AgentProviders.SessionStartInfo(provider, "app-server", folder));
            if (process is null) return;
            ChildProcesses.Track(process);
            _ = process.StandardError.ReadToEndAsync();
            async Task Send(JsonObject message)
            {
                await process.StandardInput.WriteLineAsync(message.ToJsonString());
                await process.StandardInput.FlushAsync();
            }
            await Send(new JsonObject
            {
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "Agent Orange", ["title"] = "Agent Orange", ["version"] = "1" }, ["capabilities"] = new JsonObject() },
            });

            // Its models are asked for too, so the picker has them before any conversation starts Codex.
            using var waiting = new CancellationTokenSource(ProbeWaitsFor);
            bool limitsDone = false, modelsDone = false;
            while (!(limitsDone && modelsDone) && await process.StandardOutput.ReadLineAsync(waiting.Token) is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject reply || reply["id"] is not JsonValue id || reply["method"] is not null) continue;
                var result = reply["result"] as JsonObject;
                switch (id.GetValue<long>())
                {
                    case 1:
                        await Send(new JsonObject { ["method"] = "initialized" });
                        await Send(new JsonObject { ["id"] = 2, ["method"] = "account/rateLimits/read" });
                        await Send(new JsonObject { ["id"] = 3, ["method"] = "model/list", ["params"] = new JsonObject() });
                        break;
                    case 2:
                        limitsDone = true;
                        var snapshot = result?["rateLimitsByLimitId"]?["codex"] as JsonObject ?? result?["rateLimits"] as JsonObject;
                        Update(provider.Id, FromCodex(snapshot), complete: true);
                        break;
                    case 3:
                        modelsDone = true;
                        if (AgentConversation.TakeCodexModels(provider.Id, result)) Changed?.Invoke();
                        break;
                }
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't ask {provider.Label} about its usage: {error.Message}");
        }
        finally
        {
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
            process?.Dispose();
        }
    }

    private static bool IsTrue(JsonNode? value) => value?.GetValueKind() == System.Text.Json.JsonValueKind.True;

    private static string? Str(JsonObject? json, string name) =>
        json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
