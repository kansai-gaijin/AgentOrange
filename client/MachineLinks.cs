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

/// <summary>
/// How test machines are linked, as T3 Code links environments: a one-time code made on the
/// target PC, which never leaves it except as typed by a person on the other PC.
///
/// The two PCs swap ephemeral P-256 public keys through the server. The PC linking sends its key
/// with an HMAC proof, keyed with the code, over both PCs, the access, a nonce and its key. The
/// target checks it, makes its own key, and answers with an expiry it chose and a confirmation
/// MAC keyed with both the code and the link's key over the whole exchange. The link's key comes
/// from the ECDH secret, so cracking the code later from a stored proof gives nothing, and the
/// confirmation means the server can't put its own key in the middle without knowing the code
/// while it's live. Every job is then signed with the link's key, over a run id and time.
/// </summary>
public static class MachineLinks
{
    // T3's alphabet: no 0, 1, I or O, which are easy to mistake for each other. 12 of them is about 60 bits.
    public const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int CodeLength = 12;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(30);

    /// <summary>Wrong proofs a code takes before it's thrown away, as T3's guess limit and the server's are.</summary>
    public const int MaxWrongProofs = 5;

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
        "tests" => "Run the project's prepare and test commands, once a person on that PC allows them. They run as that PC's user, with its files and logins.",
        _ => "",
    };

    /// <summary>Whether a link with this access may do what needs the other.</summary>
    public static bool Allows(string access, string needed) =>
        Array.IndexOf(AccessLevels, access) is var has and >= 0 && has >= Array.IndexOf(AccessLevels, needed) && Array.IndexOf(AccessLevels, needed) >= 0;

    public static string NewCode() =>
        string.Create(CodeLength, 0, (letters, _) =>
        {
            for (var i = 0; i < letters.Length; i++) letters[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        });

    /// <summary>Case, spaces and dashes don't count, so XXXX-XXXX-XXXX works too. Null if it can't be a code.</summary>
    public static string? CleanCode(string typed)
    {
        var code = new string([.. typed.ToUpperInvariant().Where(letter => letter is not ('-' or ' ' or '\t'))]);
        return code.Length == CodeLength && code.All(letter => CodeAlphabet.Contains(letter)) ? code : null;
    }

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsId(string? text) => text is { Length: 32 } && text.All(Uri.IsHexDigit);

    // Linking

    /// <summary>A key pair for one linking, thrown away once it's done.</summary>
    public static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string PublicKey(ECDiffieHellman ephemeral) => Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo());

    /// <summary>What the PC linking sends: proof it knows the code, bound to both PCs, the access, this request and its key.</summary>
    public static string Proof(string code, string linker, string machine, string access, string nonce, string linkerKey) =>
        Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes($"ao-link|{linker}|{machine}|{access}|{nonce}|{linkerKey}")));

    /// <summary>Checked in constant time, as the code is the secret here.</summary>
    public static bool ProofMatches(string code, string access, PendingLinkRequest request, string machine) =>
        FixedTimeEquals(Proof(code, request.Linker, machine, access, request.Nonce, request.Key), request.Proof);

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

    /// <summary>The target's answer: keyed with the code, so only it could make it while the code was live, and with the link's key, so it shows it has the same one.</summary>
    public static string Confirm(string code, byte[] linkKey, string transcript)
    {
        var key = Encoding.UTF8.GetBytes(code).Concat(linkKey).ToArray();
        try
        {
            return Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"ao-link-accept|{transcript}")));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool ConfirmMatches(string code, byte[] linkKey, string transcript, string given) =>
        FixedTimeEquals(Confirm(code, linkKey, transcript), given);

    // Jobs

    /// <summary>Signs a job, its run id and what it's for, so the server can't change, make or replay one.</summary>
    public static string JobMac(byte[] key, string runId, string linkId, string projectId, string commit, string job) =>
        Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"ao-job|{runId.ToLowerInvariant()}|{linkId}|{projectId}|{commit.ToLowerInvariant()}|{job}")));

    public static bool JobMacMatches(byte[] key, TestRunInfo run) =>
        FixedTimeEquals(JobMac(key, run.Id, run.LinkId, run.ProjectId, run.Commit, run.Job), run.Mac);

    /// <summary>
    /// What a person allows: the repo and both commands, exactly. Lengths first, so no two
    /// different sets of them can run together into the same text.
    /// </summary>
    public static string CommandsHash(string repoUrl, string prepare, string test) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes($"{repoUrl.Length}:{repoUrl}|{prepare.Length}:{prepare}|{test.Length}:{test}")));

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

    // The commands a person on this PC allowed to run here, for a link and project.
    private static string AllowedKey(string linkId, string projectId) => $"test_allowed:{linkId}:{projectId}";

    public static bool IsAllowedHere(LocalDatabase database, string linkId, string projectId, string hash) =>
        database.GetSetting(AllowedKey(linkId, projectId)) is { Length: > 0 } allowed && FixedTimeEquals(allowed, hash);

    public static void AllowHere(LocalDatabase database, string linkId, string projectId, string hash) => database.SetSetting(AllowedKey(linkId, projectId), hash);

    // The commands this PC last signed for a machine and project, which a person here saw.
    private static string SignedKey(string machine, string projectId) => $"test_signed:{machine}:{projectId}";

    public static bool SignedBefore(LocalDatabase database, string machine, string projectId, string hash) =>
        database.GetSetting(SignedKey(machine, projectId)) == hash;

    public static void RememberSigned(LocalDatabase database, string machine, string projectId, string hash) => database.SetSetting(SignedKey(machine, projectId), hash);

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

/// <summary>A link code made on this PC, for one access level, which lives only in memory, for five minutes and one use.</summary>
public sealed class LinkCode
{
    public LinkCode(string access = "tests")
    {
        Code = MachineLinks.NewCode();
        Access = access;
        MadeAt = DateTimeOffset.UtcNow;
    }

    public string Code { get; }

    /// <summary>The most a link made with this code gets, chosen here, and covered by the proof.</summary>
    public string Access { get; }

    public DateTimeOffset MadeAt { get; }

    public DateTimeOffset ExpiresAt => MadeAt + MachineLinks.CodeLifetime;

    public bool Expired => DateTimeOffset.UtcNow >= ExpiresAt;

    public int WrongProofs { get; set; }

    /// <summary>T3's countdown: "Expires in 4m 59s", "Expires in 30s", "Expires in a moment", "Expired".</summary>
    public string ExpiresText
    {
        get
        {
            var left = ExpiresAt - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return "Expired";
            if (left.TotalSeconds < 5) return "Expires in a moment";
            var seconds = (int)Math.Ceiling(left.TotalSeconds);
            return seconds >= 60 ? $"Expires in {seconds / 60}m {seconds % 60}s" : $"Expires in {seconds}s";
        }
    }
}
