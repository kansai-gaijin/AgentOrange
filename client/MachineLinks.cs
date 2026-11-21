using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

/// <summary>
/// A link's secret half, which only the two PCs have: the key both derived when linking, the
/// access the link was made with, when it ends, and when this PC saved it. Kept with DPAPI in this
/// PC's own settings, never synced, never sent anywhere, never logged.
/// </summary>
public sealed record MachineLinkKey(string LinkId, byte[] Key, string Access, double ExpiresAt, bool ThisPcIsMachine, double SavedAt = 0);

/// <summary>What the target answers a link request with, and the key it keeps.</summary>
public sealed record LinkAcceptance(string LinkId, string MachineKey, long ExpiresAt, string Confirm, byte[] LinkKey);

/// <summary>
/// How test machines are linked, as T3 Code links environments when it already has a session on
/// the other machine (#95): every PC paired with the server is the owner's, so the server is that
/// session, and there's no code to type. The target's "Let other PCs run tests here" is its
/// consent, and it answers by itself, unattended.
///
/// The two PCs swap ephemeral P-256 public keys through the server, each sent by a paired login
/// the server checked. The PC linking sends its key and a nonce. The target makes its own key,
/// and answers with an expiry it chose and a confirmation MAC, keyed with the link's key, over
/// the whole exchange, which the PC linking checks before keeping the link, so both are sure they
/// hold the same key. The link's key comes from the ECDH secret, which never reaches the server.
/// So the server is trusted while a link is made, to pass the keys on as sent, as T3 trusts its
/// own session there; once made, nothing the server keeps can sign a job, and a server taken over
/// later can't make one. Every job is signed with the link's key, over a run id and time, and a
/// link still good is renewed with a request and an answer each MACed with it.
///
/// Before #95 a one-time code made on the target, typed on the other PC, keyed the request and
/// the confirmation, so the server couldn't put its own keys in the middle without it. That
/// asked for a person at both PCs, which the unattended case can't have.
/// </summary>
public static class MachineLinks
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(30);

    /// <summary>How long the PC linking waits for an answer, as long as the server keeps a request.</summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromMinutes(10);

    /// <summary>A link with less than this left is renewed by itself, while both PCs are online.</summary>
    public static readonly TimeSpan RenewWithin = TimeSpan.FromDays(7);

    /// <summary>A renewal request older than this is ignored, so one can't be sent again later.</summary>
    public static readonly TimeSpan RenewMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>A job sent longer ago than this is refused, so an old one can't be sent again later.</summary>
    public static readonly TimeSpan JobMaxAge = TimeSpan.FromHours(6);

    /// <summary>The access levels a link can have, lowest first. Only Tests is used so far.</summary>
    public static readonly string[] AccessLevels = ["tests", "agents", "full"];

    public static string AccessLabel(string access) => access switch
    {
        "tests" => "Tests",
        "agents" => "Agents",
        "full" => "Full access",
        _ => access,
    };

    public static string AccessDescription(string access) => access switch
    {
        "tests" => "Run the tests the project's repo declares, in .agent-orange/tests.json, at a commit this PC sends. They run as that PC's user, with its files and logins.",
        _ => "",
    };

    /// <summary>Whether a link with this access may do what needs the other.</summary>
    public static bool Allows(string access, string needed) =>
        Array.IndexOf(AccessLevels, access) is var has and >= 0 && has >= Array.IndexOf(AccessLevels, needed) && Array.IndexOf(AccessLevels, needed) >= 0;

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsId(string? text) => text is { Length: 32 } && text.All(Uri.IsHexDigit);

    // Linking

    /// <summary>A key pair for one linking, thrown away once it's done.</summary>
    public static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string PublicKey(ECDiffieHellman ephemeral) => Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo());

    /// <summary>Everything both PCs agreed on, which the link's key and the target's confirmation cover.</summary>
    public static string Transcript(string linker, string machine, string access, string nonce, string linkerKey, string machineKey, string linkId, long expiresAt) =>
        $"{linker}|{machine}|{access}|{nonce}|{linkerKey}|{machineKey}|{linkId}|{expiresAt.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The link's key, from this PC's ephemeral key and the other's. Null if the other's isn't a P-256 key.</summary>
    public static byte[]? LinkKey(ECDiffieHellman mine, string theirs, string transcript)
    {
        try
        {
            using var other = ECDiffieHellman.Create();
            other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(theirs), out _);
            if (other.KeySize != 256) return null;
            var secret = mine.DeriveRawSecretAgreement(other.PublicKey);
            try
            {
                return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"ao-link-key|{transcript}"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The target's answer, keyed with the link's key, so it shows it has the same one, over everything agreed.</summary>
    public static string Confirm(byte[] linkKey, string transcript) =>
        Hex(HMACSHA256.HashData(linkKey, Encoding.UTF8.GetBytes($"ao-link-confirm|{transcript}")));

    public static bool ConfirmMatches(byte[] linkKey, string transcript, string given) =>
        FixedTimeEquals(Confirm(linkKey, transcript), given);

    /// <summary>
    /// As the target: the answer to a request, with a key pair made for this one link and an expiry
    /// of this PC's own. Null when the other PC's key isn't a P-256 key.
    /// </summary>
    public static LinkAcceptance? Accept(PendingLinkRequest request, string me)
    {
        var linkId = NewId();
        using var ephemeral = NewEphemeral();
        var machineKey = PublicKey(ephemeral);
        var expiresAt = (DateTimeOffset.UtcNow + LinkLifetime).ToUnixTimeSeconds();
        var transcript = Transcript(request.Linker, me, request.Access, request.Nonce, request.Key, machineKey, linkId, expiresAt);
        return LinkKey(ephemeral, request.Key, transcript) is { } linkKey
            ? new LinkAcceptance(linkId, machineKey, expiresAt, Confirm(linkKey, transcript), linkKey)
            : null;
    }

    /// <summary>
    /// As the PC linking: the link's key from the target's answer to its request, once its
    /// confirmation checks out and its expiry is one this PC could have been given. Null if not.
    /// </summary>
    public static byte[]? TakeAnswer(ECDiffieHellman mine, string me, string machine, string access, string nonce, LinkAnswer answer)
    {
        if (answer is not { Answer: "accepted", LinkId: { } linkId, ExpiresAt: { } expires, Key: { } machineKey, Confirm: { } confirm } || !IsId(linkId)) return null;
        var expiresAt = (long)expires;
        var now = DateTimeOffset.UtcNow;
        if (expiresAt <= now.ToUnixTimeSeconds() || expiresAt > (now + LinkLifetime + TimeSpan.FromDays(1)).ToUnixTimeSeconds()) return null;
        var transcript = Transcript(me, machine, access, nonce, PublicKey(mine), machineKey, linkId, expiresAt);
        return LinkKey(mine, machineKey, transcript) is { } linkKey && ConfirmMatches(linkKey, transcript, confirm) ? linkKey : null;
    }

    /// <summary>What the PC linking says when the target turned its request down.</summary>
    public static string RefusedText(string? reason, string machine) => reason switch
    {
        "not_accepting" => $"{machine} doesn't let other PCs run tests on it now. Turn on Settings › Test machines › Let other PCs run tests here on {machine}, then link again.",
        "access" => $"{machine} doesn't offer that access.",
        "unreadable" => $"{machine} couldn't read this PC's key. Link again.",
        _ => $"{machine} didn't take the link.",
    };

    // Jobs

    /// <summary>Signs a job, its run id and what it's for, so the server can't change, make or replay one.</summary>
    public static string JobMac(byte[] key, string runId, string linkId, string projectId, string commit, string job) =>
        Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"ao-job|{runId.ToLowerInvariant()}|{linkId}|{projectId}|{commit.ToLowerInvariant()}|{job}")));

    public static bool JobMacMatches(byte[] key, TestRunInfo run) =>
        FixedTimeEquals(JobMac(key, run.Id, run.LinkId, run.ProjectId, run.Commit, run.Job), run.Mac);

    // Renewing

    /// <summary>The PC linking's request to renew, over the link, a nonce, when it was sent and the expiry it asks for.</summary>
    public static string RenewMac(byte[] key, string linkId, string nonce, long sentAt, long expiresAt) =>
        Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"ao-link-renew|{linkId}|{nonce}|{sentAt.ToString(CultureInfo.InvariantCulture)}|{expiresAt.ToString(CultureInfo.InvariantCulture)}")));

    /// <summary>The target's answer, over the request's nonce and the expiry the target set.</summary>
    public static string RenewedMac(byte[] key, string linkId, string nonce, long expiresAt) =>
        Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"ao-link-renewed|{linkId}|{nonce}|{expiresAt.ToString(CultureInfo.InvariantCulture)}")));

    public static bool MacMatches(string expected, string given) => FixedTimeEquals(expected, given);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static bool FixedTimeEquals(string expected, string given) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given.ToLowerInvariant()));

    // What this PC keeps, outside sync

    private const string KeyPrefix = "machine_link:";

    private sealed record StoredKey(string Key, string Access, double ExpiresAt, bool ThisPcIsMachine, double SavedAt = 0);

    public static void SaveKey(LocalDatabase database, MachineLinkKey link)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredKey(Convert.ToBase64String(link.Key), link.Access, link.ExpiresAt, link.ThisPcIsMachine, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(json);
        database.SetSetting(KeyPrefix + link.LinkId, Convert.ToBase64String(encrypted));
    }

    public static MachineLinkKey? ReadKey(LocalDatabase database, string linkId)
    {
        if (database.GetSetting(KeyPrefix + linkId) is not { Length: > 0 } stored) return null;
        try
        {
            var json = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            var key = JsonSerializer.Deserialize<StoredKey>(json);
            CryptographicOperations.ZeroMemory(json);
            return key is null ? null : new MachineLinkKey(linkId, Convert.FromBase64String(key.Key), key.Access, key.ExpiresAt, key.ThisPcIsMachine, key.SavedAt);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        {
            return null; // Made by another Windows user or PC, so the link has to be made again.
        }
    }

    public static void ForgetKey(LocalDatabase database, string linkId) => database.RemoveSetting(KeyPrefix + linkId);

    /// <summary>Every link this PC holds a key for.</summary>
    public static List<string> KnownLinks(LocalDatabase database) =>
        [.. database.SettingKeysStartingWith(KeyPrefix).Select(key => key[KeyPrefix.Length..])];

    // Run ids this PC has taken, so the same job can't be run twice. Kept well past the time a job
    // is good for, after which it'd be refused for its age anyway.
    private const string SeenRunsKey = "test_runs_seen";

    /// <summary>Notes the run as taken. False if it had been already.</summary>
    public static bool TakeRunId(LocalDatabase database, string runId)
    {
        var taken = false;
        database.ChangeSetting(SeenRunsKey, stored =>
        {
            Dictionary<string, long> seen;
            try
            {
                seen = stored is { Length: > 0 } ? JsonSerializer.Deserialize<Dictionary<string, long>>(stored) ?? [] : [];
            }
            catch (JsonException)
            {
                seen = [];
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var old in seen.Where(each => now - each.Value > 3 * 24 * 60 * 60).Select(each => each.Key).ToList()) seen.Remove(old);
            taken = seen.TryAdd(runId.ToLowerInvariant(), now);
            return JsonSerializer.Serialize(seen);
        });
        return taken;
    }
}
