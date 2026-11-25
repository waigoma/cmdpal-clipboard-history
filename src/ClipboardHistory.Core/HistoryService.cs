using System.Globalization;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Sequence;
using Waigoma.ClipboardHistory.Store;

namespace Waigoma.ClipboardHistory;

// Decides what each clipboard change becomes: stored, skipped, or a reset of the paste sequence. Clipboard callbacks,
// timers and startup can run on different threads, so the capture and backfill decisions run under _gate. The worker
// is started outside that lock, because starting it must never wait on this service. Handlers of Store.Changed run
// inside Upsert while _gate is held, so they must not block.
public sealed class HistoryService : IDisposable
{
    private const string MetaPaused = "paused";
    private const string MetaHeartbeat = "heartbeat";
    private const string MetaLastSequence = "last_sequence";

    // Maintenance runs once an hour when Tick is called once a minute.
    private const long MaintenanceEveryTicks = 60;
    private static readonly TimeSpan SweepMinAge = TimeSpan.FromHours(1);

    private readonly HistoryStore _store;
    private readonly Func<HistoryOptions> _options;
    private readonly Func<ClipboardSnapshot?> _read;
    private readonly IBackfillSource _backfill;
    private readonly Action _startWorker;
    private readonly SequentialPaste _sequence;
    private readonly TimeProvider _clock;

    // Capture and backfill decisions only, so the getter of Paused never waits on them.
    private readonly Lock _gate = new();

    // Serialises writes of the pause flag without involving _gate.
    private readonly Lock _pauseGate = new();

    // Guards _heartbeatMs, so the stored heartbeat is only ever raised and a stale write cannot lower it.
    private readonly Lock _heartbeatGate = new();

    private volatile bool _paused;

    // The heartbeat read at construction is the backfill window. A capture before Startup must not move that window,
    // or copies made while the app was closed would be hidden.
    private readonly DateTimeOffset? _backfillSince;

    private long? _heartbeatMs;
    private long _ticks;

    public HistoryService(HistoryStore store, Func<HistoryOptions> options, Func<ClipboardSnapshot?> read, IBackfillSource backfill,
        Action startWorker, SequentialPaste sequence, TimeProvider clock)
    {
        _store = store;
        _options = options;
        _read = read;
        _backfill = backfill;
        _startWorker = startWorker;
        _sequence = sequence;
        _clock = clock;

        _backfillSince = ReadStoredHeartbeat();
        _heartbeatMs = _backfillSince?.ToUnixTimeMilliseconds();

        // Pause is stored so that it survives a restart; a store that cannot answer leaves capture on.
        Guarded("HistoryService.Constructor.Paused", () => _paused = _store.GetMeta(MetaPaused) == "1");
    }

    public bool Paused
    {
        get => _paused;

        set
        {
            lock (_pauseGate)
            {
                // Persisted before the field changes, so a failed write leaves the in-memory state unchanged.
                if (_paused == value)
                {
                    return;
                }

                _store.SetMeta(MetaPaused, value ? "1" : null);
                _paused = value;
            }
        }
    }

    public void Startup()
    {
        // Order matters: the heartbeat is written last, so the backfill window is never moved past copies it still needs.
        Guarded("HistoryService.Startup.ResetRunning", () => _store.Jobs.ResetRunning());
        Guarded("HistoryService.Startup.Expire", ExpireRetained);
        Guarded("HistoryService.Startup.EnforceTotalLimit", TrimToTotalLimit);
        Guarded("HistoryService.Startup.Sweep", SweepUnreferencedFiles);
        Guarded("HistoryService.Startup.Backfill", Backfill);

        // The backfill can add old or large items, so retention and the total limit are applied once more.
        Guarded("HistoryService.Startup.ExpireAfterBackfill", ExpireRetained);
        Guarded("HistoryService.Startup.EnforceTotalLimitAfterBackfill", TrimToTotalLimit);
        Guarded("HistoryService.Startup.Heartbeat", WriteHeartbeatNow);
        Guarded("HistoryService.Startup.Worker", () =>
        {
            if (_store.Jobs.HasPending())
            {
                _startWorker();
            }
        });
    }

    public void OnClipboardChanged()
    {
        try
        {
            ClipboardSnapshot? snapshot = _read();
            if (snapshot is null)
            {
                return;
            }

            bool startWorker;
            lock (_gate)
            {
                startWorker = Capture(snapshot);
            }

            if (startWorker)
            {
                _startWorker();
            }
        }
        catch (Exception ex)
        {
            Log.Error("HistoryService.OnClipboardChanged", ex);
        }
    }

    // Called once a minute. Maintenance runs on every 60th call, so the first hour's work happens at minute 60.
    public void Tick()
    {
        Guarded("HistoryService.Tick.Heartbeat", WriteHeartbeatNow);

        if (Interlocked.Increment(ref _ticks) % MaintenanceEveryTicks != 0)
        {
            return;
        }

        Guarded("HistoryService.Tick.Expire", ExpireRetained);
        Guarded("HistoryService.Tick.EnforceTotalLimit", TrimToTotalLimit);
        Guarded("HistoryService.Tick.Sweep", SweepUnreferencedFiles);
    }

    // The store belongs to the caller, so disposing this service only records that the app was running.
    public void Dispose()
    {
        Guarded("HistoryService.Dispose.Heartbeat", WriteHeartbeatNow);
    }

    // Runs under _gate. Returns true when a job is waiting, so the caller starts the worker after releasing the lock.
    private bool Capture(ClipboardSnapshot snapshot)
    {
        // Recorded even when nothing is stored, so a later backfill does not replay a copy the user has already seen.
        _store.SetMeta(MetaLastSequence, SequenceText(snapshot.Sequence));

        // Our own paste writes must not reset the sequence, or a repeated paste would restart on every press.
        if (snapshot.IsOwnWrite)
        {
            return false;
        }

        HistoryOptions options = _options();
        if (CaptureFilter.ShouldIgnore(snapshot, options.ExcludedApps))
        {
            return false;
        }

        if (_paused)
        {
            _sequence.Reset();
            return false;
        }

        CapturedEntry? entry = CaptureBuilder.Build(snapshot, options, _clock.GetUtcNow());
        if (entry is null)
        {
            return false;
        }

        _store.Upsert(entry);
        _sequence.Reset();
        WriteHeartbeatFor(entry);
        Guarded("HistoryService.Capture.EnforceTotalLimit", TrimToTotalLimit);
        return PendingJobs();
    }

    // Runs at startup only. Copies made while the app was closed are recovered from the system history, and the
    // current clipboard is recorded if it changed since the last sequence seen.
    private void Backfill()
    {
        if (_paused)
        {
            // Pause means the user chose not to record, so only the sequence is updated, which keeps the
            // copy from being picked up after the user resumes.
            RecordLastSequence();
            return;
        }

        // The system history read can take seconds, so it runs outside the lock to keep clipboard capture responsive.
        HistoryOptions options = _options();
        IReadOnlyList<BackfillItem> items = ReadWindowsHistory(options);

        lock (_gate)
        {
            if (_paused)
            {
                RecordLastSequence();
                return;
            }

            RecordWindowsHistory(items, options);
            RecordCurrentClipboard(options);
        }
    }

    // The system history is only trusted when a heartbeat existed at construction and nothing is excluded. Without a
    // heartbeat the app has never run here, so old history could flood the list. Excluded apps need filtering the
    // system history cannot provide, so it is not read at all.
    private IReadOnlyList<BackfillItem> ReadWindowsHistory(HistoryOptions options)
    {
        DateTimeOffset? window = _backfillSince;
        if (window is null || options.ExcludedApps.Count > 0)
        {
            return Array.Empty<BackfillItem>();
        }

        try
        {
            DateTimeOffset since = window.Value;

            // The source is asked to filter, but filtering again here means a lax source cannot revive old items.
            return _backfill.ReadNewerThan(since)
                .Where(item => item.CopiedAt > since)
                .OrderBy(item => item.CopiedAt)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("HistoryService.Backfill.Read", ex);
            return Array.Empty<BackfillItem>();
        }
    }

    // Runs under _gate. Each item keeps its original time, so the list shows when it was copied, not when recovered.
    private void RecordWindowsHistory(IReadOnlyList<BackfillItem> items, HistoryOptions options)
    {
        foreach (BackfillItem item in items)
        {
            try
            {
                CapturedEntry? entry = CaptureBuilder.Build(
                    new ClipboardSnapshot(0, null, false, false, false, null, item.Text, item.Files, Array.Empty<ClipFormat>()),
                    options,
                    item.CopiedAt);
                if (entry is not null)
                {
                    _store.Upsert(entry);
                    WriteHeartbeatFor(entry);
                }
            }
            catch (Exception ex)
            {
                Log.Error("HistoryService.Backfill.Item", ex);
            }
        }
    }

    // Runs under _gate. The shared content hash makes a copy that also appears in the system history a single row.
    private void RecordCurrentClipboard(HistoryOptions options)
    {
        ClipboardSnapshot? snapshot = _read();
        if (snapshot is null)
        {
            return;
        }

        string sequence = SequenceText(snapshot.Sequence);
        if (_store.GetMeta(MetaLastSequence) != sequence
            && !snapshot.IsOwnWrite
            && !CaptureFilter.ShouldIgnore(snapshot, options.ExcludedApps))
        {
            CapturedEntry? entry = CaptureBuilder.Build(snapshot, options, _clock.GetUtcNow());
            if (entry is not null)
            {
                _store.Upsert(entry);
                WriteHeartbeatFor(entry);
            }
        }

        _store.SetMeta(MetaLastSequence, sequence);
    }

    private void RecordLastSequence()
    {
        ClipboardSnapshot? snapshot = _read();
        if (snapshot is not null)
        {
            _store.SetMeta(MetaLastSequence, SequenceText(snapshot.Sequence));
        }
    }

    private void ExpireRetained()
    {
        // A null retention keeps everything, which is the user's choice to never expire.
        if (_options().Retention is { } retention)
        {
            _store.Expire(_clock.GetUtcNow() - retention);
        }
    }

    private void TrimToTotalLimit()
    {
        _store.EnforceTotalLimit(_options().MaxTotalBytes);
    }

    private void SweepUnreferencedFiles()
    {
        _store.SweepFiles(SweepMinAge);
    }

    private DateTimeOffset? ReadStoredHeartbeat()
    {
        try
        {
            string? text = _store.GetMeta(MetaHeartbeat);
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
        }
        catch (Exception ex)
        {
            Log.Error("HistoryService.Constructor.Heartbeat", ex);
            return null;
        }
    }

    private void WriteHeartbeatNow() => WriteHeartbeat(_clock.GetUtcNow());

    // An entry copied later than now (a backfilled item) still needs the heartbeat to cover it, so the window
    // of the next start does not start after the entry and let it come back as a new copy.
    private void WriteHeartbeatFor(CapturedEntry entry)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        WriteHeartbeat(entry.CopiedAt > now ? entry.CopiedAt : now);
    }

    // The heartbeat only ever moves forward. A clock that steps back must not lower the window, and an older write
    // that arrives late must not overwrite a newer one.
    private void WriteHeartbeat(DateTimeOffset at)
    {
        lock (_heartbeatGate)
        {
            long ms = at.ToUnixTimeMilliseconds();
            if (_heartbeatMs is { } stored && stored > ms)
            {
                ms = stored;
            }

            _store.SetMeta(MetaHeartbeat, ms.ToString(CultureInfo.InvariantCulture));
            _heartbeatMs = ms;
        }
    }

    private bool PendingJobs()
    {
        try
        {
            return _store.Jobs.HasPending();
        }
        catch (Exception ex)
        {
            Log.Error("HistoryService.PendingJobs", ex);
            return false;
        }
    }

    // One failing step must not stop the steps after it, so each one is isolated and only logged.
    private static void Guarded(string where, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(where, ex);
        }
    }

    private static string SequenceText(uint sequence) => sequence.ToString(CultureInfo.InvariantCulture);
}
