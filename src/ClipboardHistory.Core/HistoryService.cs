using System.Globalization;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Sequence;
using Waigoma.ClipboardHistory.Store;

namespace Waigoma.ClipboardHistory;

// Decides what each clipboard change becomes: stored, skipped, or a reset of the paste sequence. Clipboard callbacks,
// timers and startup can run on different threads, so decisions that touch shared state run under one lock. The
// worker is started outside that lock, because starting it must never wait on this service.
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
    private readonly Lock _gate = new();
    private bool _paused;
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

        // Pause is stored so that it survives a restart; a store that cannot answer leaves capture on.
        Guarded("HistoryService.Constructor.Paused", () => _paused = _store.GetMeta(MetaPaused) == "1");
    }

    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }

        set
        {
            lock (_gate)
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
        // Order matters: the heartbeat is written last, so the backfill still sees the time the app last ran.
        Guarded("HistoryService.Startup.ResetRunning", () => _store.Jobs.ResetRunning());
        Guarded("HistoryService.Startup.Expire", ExpireRetained);
        Guarded("HistoryService.Startup.EnforceTotalLimit", TrimToTotalLimit);
        Guarded("HistoryService.Startup.Sweep", SweepUnreferencedFiles);
        Guarded("HistoryService.Startup.Backfill", Backfill);
        Guarded("HistoryService.Startup.Heartbeat", WriteHeartbeat);
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
        Guarded("HistoryService.Tick.Heartbeat", WriteHeartbeat);

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
        Guarded("HistoryService.Dispose.Heartbeat", WriteHeartbeat);
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
        WriteHeartbeat();
        Guarded("HistoryService.Capture.EnforceTotalLimit", TrimToTotalLimit);
        return PendingJobs();
    }

    // Runs at startup only. Copies made while the app was closed are recovered from the system history, and the
    // current clipboard is recorded if it changed since the last sequence seen.
    private void Backfill()
    {
        bool paused;
        lock (_gate)
        {
            paused = _paused;
        }

        if (paused)
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

    // The system history is only trusted when the heartbeat exists and nothing is excluded. Without a heartbeat
    // the app has never run here, so old history could flood the list. Excluded apps need filtering the system
    // history cannot provide, so it is not read at all.
    private IReadOnlyList<BackfillItem> ReadWindowsHistory(HistoryOptions options)
    {
        try
        {
            string? heartbeat = _store.GetMeta(MetaHeartbeat);
            if (heartbeat is null || options.ExcludedApps.Count > 0)
            {
                return Array.Empty<BackfillItem>();
            }

            if (!long.TryParse(heartbeat, NumberStyles.Integer, CultureInfo.InvariantCulture, out long heartbeatMs))
            {
                return Array.Empty<BackfillItem>();
            }

            DateTimeOffset since = DateTimeOffset.FromUnixTimeMilliseconds(heartbeatMs);

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
        if (_store.GetMeta(MetaLastSequence) != sequence && !CaptureFilter.ShouldIgnore(snapshot, options.ExcludedApps))
        {
            CapturedEntry? entry = CaptureBuilder.Build(snapshot, options, _clock.GetUtcNow());
            if (entry is not null)
            {
                _store.Upsert(entry);
                WriteHeartbeat();
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

    private void WriteHeartbeat()
    {
        _store.SetMeta(MetaHeartbeat, _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
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
