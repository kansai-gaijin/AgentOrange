using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

public enum PairResult { Paired, WrongKey, TooManyTries, PairingOff, CantReach }

public sealed record DeviceInfo(string Id, string Name, bool Current, double LastSeenAt)
{
    public string LastSeenLabel
    {
        get
        {
            var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds((long)LastSeenAt);
            if (ago.TotalMinutes < 2) return "Last seen just now";
            if (ago.TotalHours < 1) return $"Last seen {(int)ago.TotalMinutes} minutes ago";
            if (ago.TotalDays < 1) return ago.TotalHours < 2 ? "Last seen 1 hour ago" : $"Last seen {(int)ago.TotalHours} hours ago";
            return ago.TotalDays < 2 ? "Last seen yesterday" : $"Last seen {(int)ago.TotalDays} days ago";
        }
    }
}

/// <summary>Epoch is different if the server's data has started over.</summary>
public sealed record SyncResponse(long Cursor, bool HasMore, List<SyncChange> Changes, string? Epoch);

/// <summary>This PC's login to the server. It's stored encrypted, per server address.</summary>
public sealed class DeviceSession(LocalDatabase database, ServerConnection server)
{
    private sealed record PairResponse(string Id, string Token);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) }; // Images can take a while.

    public bool IsPaired => HasLogin(server.Address);

    public bool HasLogin(string address) => ReadToken(TokenSettingFor(address)) is not null;

    public event Action? PairingChanged;

    // Keyed by address, so a login is only ever sent to the server that issued it. The address
    // can change while a request is out, so each request settles on its setting before it starts.
    private static string TokenSettingFor(string address) => $"device_token:{address}";

    private string? ReadToken(string setting)
    {
        var stored = database.GetSetting(setting);
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null; // Made by another Windows user or PC, so this PC has to pair again.
        }
    }

    public async Task<PairResult> PairAsync(string address, string key, string name)
    {
        try
        {
            var response = await _http.PostAsJsonAsync($"{address}/v1/pair", new { key, name });
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized: return PairResult.WrongKey;
                case HttpStatusCode.TooManyRequests: return PairResult.TooManyTries;
                case HttpStatusCode.ServiceUnavailable: return PairResult.PairingOff;
            }
            response.EnsureSuccessStatusCode();

            // Anything answering that address gets here, and it may not be an Agent Orange server.
            var paired = await response.Content.ReadFromJsonAsync<PairResponse>();
            if (string.IsNullOrEmpty(paired?.Token)) return PairResult.CantReach;

            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(paired.Token), null, DataProtectionScope.CurrentUser);
            database.SetSetting(TokenSettingFor(address), Convert.ToBase64String(encrypted));
            PairingChanged?.Invoke();
            return PairResult.Paired;
        }
        catch (Exception error) when (IsNetworkFailure(error))
        {
            return PairResult.CantReach;
        }
    }

    /// <summary>Returns null if the list can't be fetched. A rejected login also clears IsPaired.</summary>
    public async Task<List<DeviceInfo>?> ListAsync()
    {
        try
        {
            var response = await SendAsync(HttpMethod.Get, "/v1/devices");
            if (response is null || !response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<List<DeviceInfo>>();
        }
        catch (Exception error) when (IsNetworkFailure(error))
        {
            return null;
        }
    }

    /// <summary>Returns null if the sync didn't go through, in which case nothing is lost.</summary>
    public async Task<SyncResponse?> SyncAsync(long cursor, List<SyncChange> changes, string? epoch)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Post, "/v1/sync", new { cursor, changes, epoch });
            if (response is null || !response.IsSuccessStatusCode) return null;

            // Something like a proxy can answer 200 with anything, and queued changes must only
            // be cleared by a real answer.
            var answer = await response.Content.ReadFromJsonAsync<SyncResponse>();
            return answer is { Changes: not null, Epoch.Length: > 0 } ? answer : null;
        }
        catch (Exception error) when (IsNetworkFailure(error))
        {
            return null;
        }
    }

    public async Task<string?> ServerVersionAsync()
    {
        try
        {
            var response = await SendAsync(HttpMethod.Get, "/v1/version");
            return response is { IsSuccessStatusCode: true } ? await response.Content.ReadAsStringAsync() : null;
        }
        catch (Exception error) when (IsNetworkFailure(error))
        {
            return null;
        }
    }

    public Task<bool> RenameAsync(string id, string name) =>
        SucceedsAsync(HttpMethod.Patch, $"/v1/devices/{id}", new { name });

    public async Task<bool> RemoveAsync(DeviceInfo device)
    {
        var setting = TokenSettingFor(server.Address);
        var token = ReadToken(setting);
        var removed = await SucceedsAsync(HttpMethod.Delete, $"/v1/devices/{device.Id}", null);
        if (removed && device.Current && token is not null) ForgetLogin(setting, token);
        return removed;
    }

    private async Task<bool> SucceedsAsync(HttpMethod method, string path, object? body)
    {
        try
        {
            var response = await SendAsync(method, path, body);
            return response is { IsSuccessStatusCode: true };
        }
        catch (Exception error) when (IsNetworkFailure(error))
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var address = server.Address;
        var setting = TokenSettingFor(address);
        if (ReadToken(setting) is not { } token) return null;

        using var request = new HttpRequestMessage(method, address + path);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);

        var response = await _http.SendAsync(request);
        // This device was removed from the server, so its login is no use any more. Only the
        // server itself can say so, not a proxy or access rule along the way.
        if (response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.Contains("X-Agent-Orange")) ForgetLogin(setting, token);
        return response;
    }

    // A late answer about an old login mustn't erase a new one made in the meantime.
    private void ForgetLogin(string setting, string token)
    {
        if (ReadToken(setting) != token) return;
        database.SetSetting(setting, "");
        PairingChanged?.Invoke();
    }

    private static bool IsNetworkFailure(Exception error) =>
        error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException;
}
