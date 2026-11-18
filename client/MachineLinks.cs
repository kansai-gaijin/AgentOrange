using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

/// <summary>
/// A link's secret half, which only the two PCs have: the key both derive from the link code, the
/// access the link was made with, and when it ends. Kept with DPAPI in this PC's own settings,
/// never synced, never sent anywhere, never logged.
/// </summary>
public sealed record MachineLinkKey(string LinkId, byte[] Key, string Access, double ExpiresAt, bool ThisPcIsMachine);

/// <summary>
/// How test machines are linked, as T3 Code links environments: a one-time code made on the
/// target PC, which never leaves it except as typed by a person on the other PC. The PC linking
/// proves it knows the code with an HMAC, and both derive the link's key from it, which signs every
/// job. The server only ever sees proofs and MACs.
/// </summary>
public static class MachineLinks
{
    // T3's alphabet: no 0, 1, I or O, which are easy to mistake for each other. 12 of them is about 60 bits.
    public const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int CodeLength = 12;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Wrong proofs a code takes before it's thrown away, as T3's guess limit and the server's are.</summary>
    public const int MaxWrongProofs = 5;

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
        "tests" => "Run the project's own prepare and test commands. Nothing else.",
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

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string NewLinkId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>What the PC linking sends: proof it knows the code, bound to both PCs, the access and this request.</summary>
    public static string Proof(string code, string linker, string machine, string access, string nonce) =>
        Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes($"ao-link|{linker}|{machine}|{access}|{nonce}")));

    /// <summary>Checked in constant time, as the code is the secret here.</summary>
    public static bool ProofMatches(string code, PendingLinkRequest request, string machine)
    {
        var expected = Encoding.ASCII.GetBytes(Proof(code, request.Linker, machine, request.Access, request.Nonce));
        var given = Encoding.ASCII.GetBytes(request.Proof.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public static byte[] LinkKey(string code, string linkId) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes($"ao-link-key|{linkId}"));

    /// <summary>Signs a job, and what it's for, so the server can't change any of it or send one of its own.</summary>
    public static string JobMac(byte[] key, string linkId, string projectId, string commit, string job) =>
        Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"ao-job|{linkId}|{projectId}|{commit.ToLowerInvariant()}|{job}")));

    public static bool JobMacMatches(byte[] key, TestRunInfo run)
    {
        var expected = Encoding.ASCII.GetBytes(JobMac(key, run.LinkId, run.ProjectId, run.Commit, run.Job));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(run.Mac.ToLowerInvariant()));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // Keys

    private const string KeyPrefix = "machine_link:";

    private sealed record StoredKey(string Key, string Access, double ExpiresAt, bool ThisPcIsMachine);

    public static void SaveKey(LocalDatabase database, MachineLinkKey link)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredKey(Convert.ToBase64String(link.Key), link.Access, link.ExpiresAt, link.ThisPcIsMachine));
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
            return key is null ? null : new MachineLinkKey(linkId, Convert.FromBase64String(key.Key), key.Access, key.ExpiresAt, key.ThisPcIsMachine);
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
}

/// <summary>A link code made on this PC, which lives only in memory, for five minutes and one use.</summary>
public sealed class LinkCode
{
    public LinkCode()
    {
        Code = MachineLinks.NewCode();
        MadeAt = DateTimeOffset.UtcNow;
    }

    public string Code { get; }

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
