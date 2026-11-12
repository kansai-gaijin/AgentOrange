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

    // A change bigger than this still goes alone, as the outbox always gives at least one.
    private const int MinBatchBytes = 50_000;

    private readonly LocalDatabase _database;
    private readonly ServerConnection _server;
    private readonly DeviceSession _devices;
    private readonly string _syncTurnName;
    private Mutex? _syncTurn;
    private bool _hasSyncTurn;
    private int _syncTurnThread;
    private bool _running;
    private int _batchLimit = BatchSize;
    private int _batchBytes = MaxBatchBytes;

    // A dev build refused by a server on a release keeps its changes queued until it restarts,
    // as setting them aside would leave them for good over what may only be a format difference.
    private bool _holdingChanges;

    public SyncEngine(LocalDatabase database, ServerConnection server, DeviceSession devices, string databasePath)
    {
        // Several windows can share a database, and only one of them needs to do this at a time.
        // Windows releases it if that window closes or crashes, and the next one takes over.
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databasePath.ToUpperInvariant())))[..32];
        _syncTurnName = $@"Local\AgentOrangeSync-{name}";

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

    /// <summary>Records arrived from the server, which are these.</summary>
    public event Action<IReadOnlyList<SyncChange>>? RecordsChanged;

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

        // Doubles back up as they go through, as what got this one refused can be true of the rest.
        _batchLimit = 1;
    }

    private bool HasSyncTurn()
    {
        if (_hasSyncTurn) return true;
        try
        {
            _syncTurn ??= new Mutex(false, _syncTurnName);
        }
        catch (UnauthorizedAccessException)
        {
            // A window running as administrator has it, and that window is the one that syncs.
            // This is tried again each time, so this one takes over once that closes.
            return false;
        }
        try
        {
            _hasSyncTurn = _syncTurn.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _hasSyncTurn = true; // The window that had it crashed, and this one has it now.
        }
        if (_hasSyncTurn) _syncTurnThread = Environment.CurrentManagedThreadId;
        return _hasSyncTurn;
    }

    // A window that can't sync right now shouldn't stop another one that can. Only the thread that
    // took a mutex can let it go.
    private void GiveUpSyncTurn()
    {
        if (!_hasSyncTurn || _syncTurnThread != Environment.CurrentManagedThreadId) return;
        _syncTurn!.ReleaseMutex();
        _hasSyncTurn = false;
    }

    public async Task SyncNowAsync()
    {
        if (_running) return;
        if (IsPaused() || _server.State != ServerState.Connected || !_devices.IsPaired)
        {
            GiveUpSyncTurn();
            if (OtherWindowSyncs)
            {
                OtherWindowSyncs = false; // Nothing's known about that while this can't sync at all.
                StatusChanged?.Invoke();
            }
            return;
        }

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
            if (stored is null)
            {
                // Only ever once for a server, as what it then refuses gets set aside.
                _database.QueueAllRecords();
                _database.SetSetting(cursorSetting, "0");
            }
            var cursor = stored is not null && long.TryParse(stored, out var storedCursor) ? storedCursor : 0;

            var onlyReceive = false;
            while (_server.Address == address && !IsPaused())
            {
                // The last answer was that this PC and the server are on different releases, so
                // this just asks again. Sending queued images would be thrown away every time.
                // The database work runs off the window's thread, as batches can be large.
                var (limit, bytes) = (_batchLimit, _batchBytes);
                var batch = _devices.VersionsDiffer || onlyReceive || _holdingChanges ? [] : await Task.Run(() => _database.ReadOutbox(limit, bytes));
                onlyReceive = false;
                var response = await _devices.SyncAsync(cursor, batch, epoch);
                if (response is null)
                {
                    // Too much for this connection. Sending it again as it was would time out again,
                    // and hold up everything behind it.
                    if (_devices.LastSyncTimedOut && batch.Count > 0)
                    {
                        _batchLimit = Math.Max(1, batch.Count / 2);
                        _batchBytes = Math.Max(MinBatchBytes, batch.Sum(change => change.Data.Length) / 2);
                        AppLog.Write($"A sync ran out of time, so the next sends at most {_batchLimit} changes and {_batchBytes / 1000} KB.");
                        return;
                    }
                    if (!_devices.LastSyncRefused || batch.Count == 0) return;
                    if (_devices.LastRefusalMayBeFormat)
                    {
                        AppLog.Write("A server on a release refused this dev build's changes, so they stay queued until the app restarts.");
                        _holdingChanges = true;
                        onlyReceive = true;
                        continue;
                    }

                    // Working through what it refuses shouldn't hold up what other PCs have sent.
                    NarrowDownRefused(batch);
                    onlyReceive = true;
                    continue;
                }
                if (batch.Count > 0)
                {
                    _batchLimit = Math.Min(BatchSize, _batchLimit * 2);
                    _batchBytes = (int)Math.Min(MaxBatchBytes, _batchBytes * 2L);
                }

                await Task.Run(() => _database.RemoveFromOutbox(batch));

                if (response.Epoch is { Length: > 0 } current && current != epoch)
                {
                    var oldEpoch = epoch;
                    epoch = current;
                    cursorSetting = CursorSetting(epoch);

                    // Already caught up with this server's new data through another address.
                    if (_database.GetSetting(cursorSetting) is { } known)
                    {
                        _database.ReplaceEpoch(oldEpoch, epoch);
                        _database.SetSetting(epochSetting, epoch);
                        cursor = long.TryParse(known, out var knownCursor) ? knownCursor : 0;
                        continue;
                    }

                    // A server that lost its data would never send anything past this PC's old
                    // cursor, and doesn't have what this PC has. So everything starts over, all
                    // at once, as stopping halfway would leave an old cursor in use with new data.
                    AppLog.Write("The server's data started over, so this PC is sending it everything again.");
                    _database.StartOverWithServer(epochSetting, oldEpoch, epoch, cursorSetting);
                    cursor = 0;
                    continue;
                }

                // Away for longer than the server keeps deletes, so what's here may include things
                // deleted since. It all comes again, which leaves those out.
                // While that's coming again, the cursor is behind on purpose, and the server says so
                // for every page. That's remembered across restarts, as stopping halfway would
                // otherwise start it over.
                var refetchSetting = $"sync_refetching:{epoch}";
                if (response.TooFarBehind && _database.GetSetting(refetchSetting) is null)
                {
                    AppLog.Write("This PC was away longer than the server keeps deletes, so it's taking everything from the server again.");
                    await Task.Run(() => _database.ForgetWhatCameFromServer(cursorSetting));
                    _database.SetSetting(refetchSetting, "1");
                    cursor = 0;
                    RecordsChanged?.Invoke([]);
                    continue;
                }

                var arrived = response.Changes;
                if (await Task.Run(() => _database.ApplyFromServer(arrived)) > 0) RecordsChanged?.Invoke(arrived);

                cursor = response.Cursor;
                _database.SetSetting(cursorSetting, cursor.ToString());
                LastSynced = DateTimeOffset.UtcNow;

                if (!response.HasMore) _database.RemoveSetting(refetchSetting);
                if (!response.HasMore && (_holdingChanges || _database.CountOutbox() == 0)) break;
            }
        }
        finally
        {
            _running = false;
            StatusChanged?.Invoke();
        }
    }
}
