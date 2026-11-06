using System.Security.Cryptography;
using System.Text;
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
    private readonly Mutex _syncTurn;
    private bool _hasSyncTurn;
    private bool _running;
    private int _batchLimit = BatchSize;

    public SyncEngine(LocalDatabase database, ServerConnection server, DeviceSession devices, string databasePath)
    {
        // Several windows can share a database, and only one of them needs to do this at a time.
        // Windows releases it if that window closes or crashes, and the next one takes over.
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databasePath.ToUpperInvariant())))[..32];
        _syncTurn = new Mutex(false, $@"Local\AgentOrangeSync-{name}");

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

    /// <summary>Changes the server wouldn't take, set aside so everything else can still sync. They stay on this PC.</summary>
    public int RefusedChanges => _database.CountRefused();

    /// <summary>Another window of the app has the database's turn to sync.</summary>
    public bool OtherWindowSyncs { get; private set; }

    public int WaitingToSync => _database.CountOutbox();

    /// <summary>The sync status may have changed.</summary>
    public event Action? StatusChanged;

    /// <summary>Records arrived from the server.</summary>
    public event Action? RecordsChanged;

    private static string CursorSetting(string epoch) => $"sync_cursor_for:{epoch}";

    // One bad change would otherwise be first in line forever, and nothing behind it would ever
    // sync. So smaller and smaller batches are sent until it's alone, and it's then set aside.
    private void NarrowDownRefused(List<SyncChange> batch)
    {
        if (batch.Count > 1)
        {
            _batchLimit = Math.Max(1, batch.Count / 2);
            return;
        }

        AppLog.Write($"The server refused a change, so it was set aside and stays on this PC: {batch[0].Collection}/{batch[0].Id}");
        _database.SetAsideRefused(batch[0]);
        _batchLimit = BatchSize;
    }

    private bool HasSyncTurn()
    {
        if (_hasSyncTurn) return true;
        try
        {
            _hasSyncTurn = _syncTurn.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _hasSyncTurn = true; // The window that had it crashed, and this one has it now.
        }
        return _hasSyncTurn;
    }

    public async Task SyncNowAsync()
    {
        if (_running || IsPaused() || _server.State != ServerState.Connected || !_devices.IsPaired) return;

        var hasTurn = HasSyncTurn();
        if (OtherWindowSyncs == hasTurn)
        {
            OtherWindowSyncs = !hasTurn;
            StatusChanged?.Invoke();
        }
        if (!hasTurn) return;

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
                // The last answer was that this PC and the server are on different releases, so
                // this just asks again. Sending queued images would be thrown away every time.
                var batch = _devices.VersionsDiffer ? [] : _database.ReadOutbox(_batchLimit, MaxBatchBytes);
                var response = await _devices.SyncAsync(cursor, batch, epoch);
                if (response is null)
                {
                    if (_devices.LastSyncRefused && batch.Count > 0) NarrowDownRefused(batch);
                    return;
                }
                _batchLimit = Math.Min(BatchSize, _batchLimit * 2);

                _database.RemoveFromOutbox(batch);

                if (response.Epoch is { Length: > 0 } current && current != epoch)
                {
                    _database.ReplaceEpoch(epoch, current);
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
