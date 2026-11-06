using System.Windows.Threading;

namespace AgentOrange;

/// <summary>Sends this PC's queued changes to the server and brings back everyone else's.</summary>
public sealed class SyncEngine
{
    private const int BatchSize = 200;

    // The server accepts requests up to 2 MB, and images make up most of the data.
    private const int MaxBatchCharacters = 1_400_000;

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

    public DateTimeOffset? LastSynced { get; private set; }

    public int WaitingToSync => _database.CountOutbox();

    /// <summary>The sync status may have changed.</summary>
    public event Action? StatusChanged;

    /// <summary>Records arrived from the server.</summary>
    public event Action? RecordsChanged;

    public async Task SyncNowAsync()
    {
        if (_running || _server.State != ServerState.Connected || !_devices.IsPaired) return;
        _running = true;

        try
        {
            // Everything here belongs to one server, and the address can change between requests.
            var address = _server.Address;
            var cursorSetting = $"sync_cursor:{address}";
            var epochSetting = $"sync_epoch:{address}";
            var stored = _database.GetSetting(cursorSetting);
            if (stored is null) _database.QueueAllRecords();
            var cursor = stored is null ? 0 : long.Parse(stored);

            while (_server.Address == address)
            {
                var batch = _database.ReadOutbox(BatchSize, MaxBatchCharacters);
                var response = await _devices.SyncAsync(cursor, batch);
                if (response is null) return;

                _database.RemoveFromOutbox(batch);

                // A server that lost its data would never send anything past this PC's old
                // cursor, and doesn't have what this PC has. So everything starts over.
                var knownEpoch = _database.GetSetting(epochSetting);
                if (response.Epoch is { Length: > 0 } epoch && epoch != knownEpoch)
                {
                    _database.SetSetting(epochSetting, epoch);
                    if (knownEpoch is not null)
                    {
                        AppLog.Write("The server's data started over, so this PC is sending it everything again.");
                        _database.QueueAllRecords();
                        cursor = 0;
                        _database.SetSetting(cursorSetting, "0");
                        continue;
                    }
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
