using System.Windows.Threading;

namespace AgentOrange;

/// <summary>Sends this PC's queued changes to the server and brings back everyone else's.</summary>
public sealed class SyncEngine
{
    private const int BatchSize = 200;

    // The server accepts requests up to 2 MB, and images make up most of the data.
    private const int MaxBatchBytes = 1_400_000;

    private readonly LocalDatabase _database;
    private readonly ServerConnection _server;
    private readonly DeviceSession _devices;
    private bool _running;

    public SyncEngine(LocalDatabase database, ServerConnection server, DeviceSession devices)
    {
        _database = database;
        _server = server;
        _devices = devices;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => await SyncNowAsync();
        timer.Start();
    }

    /// <summary>Nothing goes either way while this says so. Queued changes wait.</summary>
    public Func<bool> IsPaused { get; set; } = () => false;

    public DateTimeOffset? LastSynced { get; private set; }

    public int WaitingToSync => _database.CountOutbox();

    /// <summary>The sync status may have changed.</summary>
    public event Action? StatusChanged;

    /// <summary>Records arrived from the server.</summary>
    public event Action? RecordsChanged;

    private static string CursorSetting(string epoch) => $"sync_cursor_for:{epoch}";

    public async Task SyncNowAsync()
    {
        if (_running || IsPaused() || _server.State != ServerState.Connected || !_devices.IsPaired) return;
        _running = true;

        try
        {
            // The address can change between requests, so this settles on one to begin with.
            var address = _server.Address;
            var epochSetting = $"sync_epoch:{address}";

            // One server can have several addresses, such as Tailscale and public HTTPS, so it's
            // the epoch that says which server this is. Asking for nothing past the end just
            // reads the epoch. Treating a second address as a new server would send this PC's
            // whole copy, over whatever newer changes other PCs have made.
            var epoch = _database.GetSetting(epochSetting);
            if (epoch is null)
            {
                var first = await _devices.SyncAsync(long.MaxValue, [], null);
                if (first?.Epoch is not { Length: > 0 } found || _server.Address != address) return;
                epoch = found;
                _database.SetSetting(epochSetting, epoch);
            }

            // The second setting is where cursors used to be kept. A PC updating from then still
            // has it, and without it would send its whole copy over everyone's newer changes.
            var cursorSetting = CursorSetting(epoch);
            var stored = _database.GetSetting(cursorSetting) ?? _database.GetSetting($"sync_cursor:{address}");
            if (stored is null) _database.QueueAllRecords();
            var cursor = stored is null ? 0 : long.Parse(stored);

            while (_server.Address == address && !IsPaused())
            {
                var batch = _database.ReadOutbox(BatchSize, MaxBatchBytes);
                var response = await _devices.SyncAsync(cursor, batch, epoch);
                if (response is null) return;

                _database.RemoveFromOutbox(batch);

                if (response.Epoch is { Length: > 0 } current && current != epoch)
                {
                    epoch = current;
                    cursorSetting = CursorSetting(epoch);

                    // Already caught up with this server's new data through another address.
                    if (_database.GetSetting(cursorSetting) is { } known)
                    {
                        _database.SetSetting(epochSetting, epoch);
                        cursor = long.Parse(known);
                        continue;
                    }

                    // A server that lost its data would never send anything past this PC's old
                    // cursor, and doesn't have what this PC has. So everything starts over.
                    AppLog.Write("The server's data started over, so this PC is sending it everything again.");
                    _database.StartOverWithServer(epochSetting, epoch, cursorSetting);
                    cursor = 0;
                    continue;
                }

                if (_database.ApplyFromServer(response.Changes) > 0) RecordsChanged?.Invoke();

                cursor = response.Cursor;
                _database.SetSetting(cursorSetting, cursor.ToString());
                LastSynced = DateTimeOffset.UtcNow;

                if (!response.HasMore && _database.CountOutbox() == 0) break;
            }
        }
        finally
        {
            _running = false;
            StatusChanged?.Invoke();
        }
    }
}
