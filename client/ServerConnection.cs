using System.Net.Http;
using System.Windows.Threading;

namespace AgentOrange;

public enum ServerState { NoAddress, Connecting, Connected, Disconnected }

/// <summary>Remembers the server's address and keeps checking whether it can be reached.</summary>
public sealed class ServerConnection
{
    private const string AddressSetting = "server_address";
    private const string HealthyAnswer = "🍊";
    private const int FailedChecksBeforeFallback = 2;
    private static readonly TimeSpan BackToChosenInterval = TimeSpan.FromSeconds(30);

    private readonly LocalDatabase _database;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private string? _addressBeingChecked;
    private string _chosenAddress;
    private int _failedChecks;
    private DateTimeOffset _triedChosenAt;

    public ServerConnection(LocalDatabase database)
    {
        _database = database;
        Address = database.GetSetting(AddressSetting) ?? "";
        _chosenAddress = Address;
        State = Address.Length == 0 ? ServerState.NoAddress : ServerState.Connecting;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await CheckAsync();
        timer.Start();
    }

    public string Address { get; private set; }

    /// <summary>The address that's saved. Address differs while falling back from it.</summary>
    public string ChosenAddress => _chosenAddress;

    public ServerState State { get; private set; }

    public event Action? StateChanged;

    /// <summary>Turns what someone typed into a clean address. An empty one means no server.</summary>
    public static bool TryCleanAddress(string input, out string address)
    {
        var text = input.Trim().TrimEnd('/');
        address = "";
        if (text.Length == 0) return true;
        if (!text.Contains("://")) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0
            || uri.UserInfo.Length > 0 // A login in an address would end up saved and logged.
            || (uri.Scheme == "http" && !uri.IsLoopback)) // Logins must not cross a network unencrypted.
        {
            return false;
        }

        // Lowercases the scheme and host, so Box and box are one server.
        address = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    public async Task SetAddressAsync(string address)
    {
        _database.SetSetting(AddressSetting, address);
        Address = address;
        _chosenAddress = address;
        _failedChecks = 0;
        await ReconnectAsync();
    }

    public async Task ReconnectAsync()
    {
        SetState(Address.Length == 0 ? ServerState.NoAddress : ServerState.Connecting);
        await CheckAsync();
    }

    public async Task CheckAsync()
    {
        var address = Address;
        if (address.Length == 0 || _addressBeingChecked == address) return;
        _addressBeingChecked = address;

        var reachable = await IsHealthyAsync(address);

        // A PC paired at more than one address, such as Tailscale and public HTTPS, carries on
        // through another when this one stops answering, and goes back once it answers again.
        // The chosen address stays what's saved.
        if (address == Address)
        {
            _failedChecks = reachable ? 0 : _failedChecks + 1;
            var moved = !reachable && _failedChecks >= FailedChecksBeforeFallback
                ? await TryOtherAddressesAsync(address)
                : reachable && address != _chosenAddress && await TryChosenAddressAsync(address);
            if (moved)
            {
                if (_addressBeingChecked == address) _addressBeingChecked = null;
                return;
            }
        }

        if (_addressBeingChecked == address) _addressBeingChecked = null;

        // The address may have changed while this check was running.
        if (address == Address) SetState(reachable ? ServerState.Connected : ServerState.Disconnected);
    }

    private async Task<bool> IsHealthyAsync(string address)
    {
        try
        {
            return await _http.GetStringAsync($"{address}/v1/health") == HealthyAnswer;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<bool> TryOtherAddressesAsync(string failing)
    {
        // Only to another way into this same server. A login can also be left over from one this
        // PC used to use, and anything synced there would never reach the real one.
        var thisServer = _database.GetSetting($"sync_epoch:{failing}");
        if (string.IsNullOrEmpty(thisServer)) return false;

        foreach (var other in _database.AddressesWithLogin().Where(other => other != failing && _database.GetSetting($"sync_epoch:{other}") == thisServer))
        {
            if (!await IsHealthyAsync(other) || Address != failing) continue;
            MoveTo(other);
            return true;
        }
        return false;
    }

    private async Task<bool> TryChosenAddressAsync(string current)
    {
        if (DateTimeOffset.UtcNow - _triedChosenAt < BackToChosenInterval) return false;
        _triedChosenAt = DateTimeOffset.UtcNow;
        if (!await IsHealthyAsync(_chosenAddress) || Address != current) return false;
        MoveTo(_chosenAddress);
        return true;
    }

    private void MoveTo(string address)
    {
        AppLog.Write($"The server stopped or started answering, so this PC moved to {address}.");
        Address = address;
        _failedChecks = 0;
        State = ServerState.Connected;
        StateChanged?.Invoke();
    }

    private void SetState(ServerState state)
    {
        if (state == State) return;
        State = state;
        StateChanged?.Invoke();
    }
}
