using System.Net.Http;
using System.Windows.Threading;

namespace AgentOrange;

public enum ServerState { NoAddress, Connecting, Connected, Disconnected }

/// <summary>Remembers the server's address and keeps checking whether it can be reached.</summary>
public sealed class ServerConnection
{
    private const string AddressSetting = "server_address";
    private const string HealthyAnswer = "🍊";

    private readonly LocalDatabase _database;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private string? _addressBeingChecked;

    public ServerConnection(LocalDatabase database)
    {
        _database = database;
        Address = database.GetSetting(AddressSetting) ?? "";
        State = Address.Length == 0 ? ServerState.NoAddress : ServerState.Connecting;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await CheckAsync();
        timer.Start();
    }

    public string Address { get; private set; }

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

        bool reachable;
        try
        {
            reachable = await _http.GetStringAsync($"{address}/v1/health") == HealthyAnswer;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            reachable = false;
        }

        if (_addressBeingChecked == address) _addressBeingChecked = null;

        // The address may have changed while this check was running.
        if (address == Address) SetState(reachable ? ServerState.Connected : ServerState.Disconnected);
    }

    private void SetState(ServerState state)
    {
        if (state == State) return;
        State = state;
        StateChanged?.Invoke();
    }
}
