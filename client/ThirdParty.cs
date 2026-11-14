using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

/// <summary>A kind of job a third-party service can do for a workflow.</summary>
public enum ServiceUse { Models3D, Images, SoundEffects, Music, Voice }

/// <summary>Something the person types in to connect a service, such as its API key.</summary>
public sealed record ServiceField(string Key, string Label, string Hint, bool Secret = true, string[]? Options = null);

/// <summary>A service Agent Orange knows how to connect to. None of them is needed to use the app.</summary>
public sealed record ThirdPartyService(
    string Id,
    string Name,
    string Group,
    string Summary,
    ServiceUse[] Uses,
    ServiceField[] Fields,
    string KeyPage,
    string Docs,
    string? NoApi = null, // Why it can't be connected, for a service without a public API.
    bool ViaCodex = false); // Uses a Codex login's ChatGPT plan instead of a key.

/// <summary>What checking a service's key found out.</summary>
public sealed record ServiceCheck(bool Works, string Detail);

/// <summary>
/// The image, 3D and audio services the person has connected, and what each is used for.
/// Keys stay on this PC, encrypted for the Windows user, like the server login. They're
/// never written to the agent folder or given to agents: workflows call the services.
/// </summary>
public sealed partial class ThirdParty(LocalDatabase database)
{
    private const string UsesSetting = "third_party_uses";
    private const string FirstSetting = "third_party_first";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public event Action? Changed;

    public static string UseName(ServiceUse use) => use switch
    {
        ServiceUse.Models3D => "3D models",
        ServiceUse.Images => "Images",
        ServiceUse.SoundEffects => "Sound effects",
        ServiceUse.Music => "Music",
        ServiceUse.Voice => "Voice",
        _ => use.ToString(),
    };

    public static ThirdPartyService? Find(string id) => Catalog.FirstOrDefault(service => service.Id == id);

    private static string FieldSetting(string id, string field) => $"third_party:{id}:{field}";

    public string? Read(string id, string field)
    {
        var stored = database.GetSetting(FieldSetting(id, field));
        if (string.IsNullOrEmpty(stored)) return null;
        if (Find(id)?.Fields.FirstOrDefault(each => each.Key == field) is { Secret: false }) return stored;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null; // Saved by another Windows user, so it has to be typed in again here.
        }
    }

    public void Write(string id, string field, string value)
    {
        var secret = Find(id)?.Fields.FirstOrDefault(each => each.Key == field)?.Secret ?? true;
        var stored = value.Length == 0 || !secret
            ? value
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        if (stored.Length == 0) database.RemoveSetting(FieldSetting(id, field));
        else database.SetSetting(FieldSetting(id, field), stored);
        Changed?.Invoke();
    }

    /// <summary>Everything it needs has been typed in.</summary>
    public bool IsSetUp(ThirdPartyService service) =>
        service.NoApi is null && service.Fields.All(field => Read(service.Id, field.Key) is { Length: > 0 });

    /// <summary>Forgets its keys and what it was used for.</summary>
    public void Forget(ThirdPartyService service)
    {
        foreach (var field in service.Fields) database.RemoveSetting(FieldSetting(service.Id, field.Key));
        database.ChangeSetting(UsesSetting, stored => Change(stored, (Dictionary<string, ServiceUse[]> uses) => uses.Remove(service.Id)));
        database.ChangeSetting(FirstSetting, stored => Change(stored, (Dictionary<ServiceUse, string> first) =>
        {
            foreach (var use in first.Where(pair => pair.Value == service.Id).Select(pair => pair.Key).ToList()) first.Remove(use);
        }));
        Changed?.Invoke();
    }

    /// <summary>What the person has it doing. Everything it can do, until they say otherwise.</summary>
    public IReadOnlyList<ServiceUse> UsesOf(ThirdPartyService service)
    {
        var uses = Load<Dictionary<string, ServiceUse[]>>(UsesSetting);
        return uses.TryGetValue(service.Id, out var chosen)
            ? service.Uses.Where(chosen.Contains).ToList()
            : service.Uses;
    }

    public void SetUse(ThirdPartyService service, ServiceUse use, bool on)
    {
        var current = UsesOf(service).ToHashSet();
        if (on) current.Add(use);
        else current.Remove(use);
        var chosen = service.Uses.Where(current.Contains).ToArray();
        database.ChangeSetting(UsesSetting, stored => Change(stored, (Dictionary<string, ServiceUse[]> uses) => uses[service.Id] = chosen));
        Changed?.Invoke();
    }

    /// <summary>The services set up for a job, in the order they're listed.</summary>
    public IReadOnlyList<ThirdPartyService> For(ServiceUse use) =>
        Catalog.Where(service => IsSetUp(service) && UsesOf(service).Contains(use)).ToList();

    /// <summary>The one a workflow uses for a job unless it's told which: the person's pick, or the first set up.</summary>
    public ThirdPartyService? FirstFor(ServiceUse use)
    {
        var ready = For(use);
        var picked = Load<Dictionary<ServiceUse, string>>(FirstSetting).GetValueOrDefault(use);
        return ready.FirstOrDefault(service => service.Id == picked) ?? ready.FirstOrDefault();
    }

    public void SetFirst(ServiceUse use, ThirdPartyService service)
    {
        database.ChangeSetting(FirstSetting, stored => Change(stored, (Dictionary<ServiceUse, string> first) => first[use] = service.Id));
        Changed?.Invoke();
    }

    private T Load<T>(string setting) where T : new()
    {
        var stored = database.GetSetting(setting);
        if (string.IsNullOrEmpty(stored)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(stored, JsonOptions) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    private static string Change<T>(string? stored, Action<T> change) where T : new()
    {
        var value = new T();
        if (!string.IsNullOrEmpty(stored))
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(stored, JsonOptions) ?? new T();
            }
            catch (JsonException)
            {
            }
        }
        change(value);
        return JsonSerializer.Serialize(value, JsonOptions);
    }
}
