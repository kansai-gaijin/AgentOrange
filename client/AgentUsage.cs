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
        "seven_day" => "Week",
        "seven_day_opus" => "Week · Opus",
        "seven_day_sonnet" => "Week · Sonnet",
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
/// </summary>
public sealed class AgentUsage
{
    private static readonly TimeSpan ProbeWaitsFor = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, Dictionary<string, AgentLimit>> _byProvider = [];
    private readonly Dictionary<string, DateTimeOffset> _checked = [];

    /// <summary>On the window's thread: a login's limits changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<AgentLimit> For(string? providerId) =>
        providerId is not null && _byProvider.TryGetValue(providerId, out var limits)
            ? [.. limits.Values.OrderBy(limit => limit.Kind == "five_hour" ? 0 : limit.Kind == "seven_day" ? 1 : 2)]
            : [];

    public DateTimeOffset? CheckedAt(string providerId) => _checked.TryGetValue(providerId, out var at) ? at : null;

    /// <summary>When a login that ran out can be used again: the latest of its limits that's run out, or null if none has.</summary>
    public DateTimeOffset? BlockedUntil(string? providerId) =>
        For(providerId).Where(limit => limit.Blocked || limit.Used >= 100).Select(limit => limit.ResetsAt).Max();

    /// <summary>Takes what it said. A limit said without how much is used keeps what was known.</summary>
    public void Update(string providerId, IEnumerable<AgentLimit> limits, bool complete)
    {
        if (!_byProvider.TryGetValue(providerId, out var known)) known = _byProvider[providerId] = [];
        if (complete) _checked[providerId] = DateTimeOffset.UtcNow;
        foreach (var limit in limits)
        {
            known[limit.Kind] = known.TryGetValue(limit.Kind, out var before)
                ? limit with { Used = limit.Used ?? (limit.Blocked ? 100 : before.Used), ResetsAt = limit.ResetsAt ?? before.ResetsAt }
                : limit;
        }
        Changed?.Invoke();
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

    /// <summary>Asks a login's agent program for its limits, without sending anything to a model.</summary>
    public async Task ProbeAsync(AgentProvider provider, string folder)
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

    private static bool IsTrue(JsonNode? value) => value?.GetValueKind() == System.Text.Json.JsonValueKind.True;

    private static string? Str(JsonObject? json, string name) =>
        json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
