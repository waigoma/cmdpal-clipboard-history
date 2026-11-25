using System.Diagnostics;
using System.Threading;
using Windows.Storage;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Interop;
using Waigoma.ClipboardHistory.Paste;
using Waigoma.ClipboardHistory.Sequence;
using Waigoma.ClipboardHistory.Store;

namespace Waigoma.ClipboardHistory.Extension;

// The only place that wires the core together. Everything the pages and commands need is reached through here.
internal sealed partial class AppHost : IDisposable
{
    public const string RecorderMutexName = @"Local\Waigoma.ClipboardHistory.Recorder";
    public const string WorkerMutexName = @"Local\Waigoma.ClipboardHistory.Worker";

    private static readonly TimeSpan MaintenancePeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MemoryTrimDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WorkerMaxRuntime = TimeSpan.FromMinutes(5);

    // The worker's exit code is its processed count, and 255 is reserved for a fatal error, so anything else is a count.
    private const int FatalWorkerExitCode = 255;

    private readonly Lock _workerGate = new();
    private bool _workerRunning;
    private bool _workerPending;
    private bool _workerForceOcr;

    private Mutex? _recorderMutex;
    private ClipboardMonitor? _monitor;
    private Timer? _maintenanceTimer;
    private Timer? _memoryTimer;
    // Guarded by _workerGate together with the worker state, so Dispose and StartWorker cannot race a new child.
    private bool _isDisposed;
    private Process? _workerProcess;

    // Only the recorder owns the heartbeat, startup backfill and maintenance; other hosts just show the store.
    private bool _isRecorder;

    public AppHost()
    {
        string localFolder = ApplicationData.Current.LocalFolder.Path;
        try
        {
            Log.Initialize(localFolder);

            Settings = new SettingsManager(localFolder);

            StorePaths paths = new(Settings.DataRoot);
            paths.EnsureCreated();
            Store = new HistoryStore(paths, TimeProvider.System);

            Options = () => Settings.Current;
            Sequence = new SequentialPaste(TimeProvider.System, () => Settings.Current.SequenceTimeout);
            Notice = new PasteNotice();
            Dib = new DibProvider(paths.Temp);

            Target = new WindowSystem();

            // The monitor is created before its reader and writer, because both need its window handle.
            ClipboardMonitor monitor = new(OnClipboardChanged, OnForegroundChanged);
            _monitor = monitor;
            ClipboardReader reader = new(() => monitor.WindowHandle, () => Target.ExeOf(Target.Foreground));
            ClipboardWriter writer = new(() => monitor.WindowHandle);

            // Paste waits for focus and key timing, so it runs on the thread pool, not on the caller's thread.
            Paste = new PasteService(Target, writer, new SystemDelay(), static action => Task.Run(action));

            Service = new HistoryService(
                Store,
                Options,
                () => reader.Read(Settings.Current.MaxFormatBytes),
                new WindowsHistorySource(),
                () => StartWorker(),
                Sequence,
                TimeProvider.System);

            // A second process that already holds the recorder name only shows the list; one recorder is enough.
            _recorderMutex = new Mutex(initiallyOwned: false, RecorderMutexName, out bool createdNew);
            _isRecorder = createdNew;
            if (createdNew)
            {
                try
                {
                    monitor.Start();
                }
                catch (Exception ex)
                {
                    // The palette still works without capture; the user sees the history that was already stored.
                    Log.Error("AppHost.MonitorStart", ex);
                }
            }
            else
            {
                Log.Info("recorder already running in another process; capture is off here");
            }

            if (createdNew)
            {
                Task.Run(() =>
                {
                    try
                    {
                        Service.Startup();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("AppHost.Startup", ex);
                    }
                });

                _maintenanceTimer = new Timer(_ => Maintain(), null, MaintenancePeriod, MaintenancePeriod);
            }

            _memoryTimer = new Timer(_ => TrimMemory(), null, Timeout.Infinite, Timeout.Infinite);
            Store.Changed += NoteActivity;
        }
        catch (Exception)
        {
            Dispose();
            throw;
        }
    }

    public HistoryStore Store { get; }

    public HistoryService Service { get; }

    public PasteService Paste { get; }

    public SequentialPaste Sequence { get; }

    public Func<HistoryOptions> Options { get; }

    public IDibProvider Dib { get; }

    public SettingsManager Settings { get; }

    public PasteNotice Notice { get; }

    public WindowSystem Target { get; }

    // Raised on the worker's thread after the worker has finished and the store has been told about it.
    public event Action? WorkerFinished;

    // Starts the image worker unless one is running; a request that arrives during a run runs once more afterwards.
    public void StartWorker(bool forceOcr = false)
    {
        lock (_workerGate)
        {
            if (_isDisposed)
            {
                return;
            }

            if (_workerRunning)
            {
                _workerPending = true;
                _workerForceOcr |= forceOcr;
                return;
            }

            _workerRunning = true;
            _workerForceOcr = forceOcr;
        }

        Task.Run(RunWorkerLoop);
    }

    // Called by pages on user activity. The memory trim runs once, a minute after the last activity.
    public void NoteActivity()
    {
        try
        {
            _memoryTimer?.Change(MemoryTrimDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposal is racing with activity; nothing is left to trim.
        }
    }

    public void Dispose()
    {
        Process? child;
        lock (_workerGate)
        {
            if (_isDisposed)
            {
                return;
            }

            // Set under the gate, so no StartWorker or RunWorkerProcess can start a child after this point.
            _isDisposed = true;
            child = _workerProcess;
        }

        // Order matters: stop producers first, then write the heartbeat, then release the store and the name.
        StepGuard("AppHost.Dispose.Timers", () =>
        {
            _maintenanceTimer?.Dispose();
            _memoryTimer?.Dispose();
        });
        if (child is not null)
        {
            StepGuard("AppHost.Dispose.Worker", () => KillQuietly(child));
        }

        StepGuard("AppHost.Dispose.Monitor", () => _monitor?.Dispose());
        if (_isRecorder)
        {
            StepGuard("AppHost.Dispose.Service", () => Service?.Dispose());
        }

        StepGuard("AppHost.Dispose.Store", () => Store?.Dispose());
        StepGuard("AppHost.Dispose.Mutex", () => _recorderMutex?.Dispose());
    }

    private void OnClipboardChanged()
    {
        try
        {
            Service.OnClipboardChanged();
        }
        catch (Exception ex)
        {
            Log.Error("AppHost.OnClipboardChanged", ex);
        }
    }

    private void OnForegroundChanged(nint hwnd)
    {
        try
        {
            Target.OnForegroundChanged(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("AppHost.OnForegroundChanged", ex);
        }
    }

    private void Maintain()
    {
        try
        {
            Service.Tick();
        }
        catch (Exception ex)
        {
            Log.Error("AppHost.Maintain", ex);
        }
    }

    // Returns the freed pages to the OS after a quiet minute, which keeps the resident size near its idle value.
    private void TrimMemory()
    {
        try
        {
            Store.ReleaseMemory();
        }
        catch (Exception ex)
        {
            Log.Error("AppHost.TrimMemory", ex);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    // Runs until no request is pending, the last pass made no progress, or the time limit is reached.
    private void RunWorkerLoop()
    {
        try
        {
            // Each window has its own 5-minute limit. A window ends early only at the limit, and a new one
            // starts only when a request was made meanwhile, so requests are never dropped by the limit.
            while (RunWorkerWindow(Stopwatch.GetTimestamp()))
            {
            }
        }
        catch (Exception ex)
        {
            Log.Error("AppHost.RunWorkerLoop", ex);
            lock (_workerGate)
            {
                _workerRunning = false;
                _workerPending = false;
                _workerForceOcr = false;
            }
        }

        StepGuard("AppHost.RunWorkerLoop.Notify", () => Store.NotifyChanged());
        StepGuard("AppHost.RunWorkerLoop.Finished", () => WorkerFinished?.Invoke());
    }

    // Returns true when the window ended at its limit with a request pending, so the caller starts a new window.
    private bool RunWorkerWindow(long started)
    {
        while (true)
        {
            TimeSpan remaining = WorkerMaxRuntime - Stopwatch.GetElapsedTime(started);
            bool forceOcr;
            lock (_workerGate)
            {
                if (_isDisposed)
                {
                    FinishWorkerLocked();
                    return false;
                }

                // A request still pending at the limit is kept, so the caller opens a fresh window for it.
                if (remaining <= TimeSpan.Zero)
                {
                    if (!_workerPending)
                    {
                        FinishWorkerLocked();
                        return false;
                    }

                    return true;
                }

                // A pass serves every request made before it starts, so the flag is cleared at this point.
                _workerPending = false;
                forceOcr = _workerForceOcr;
            }

            int processed = RunWorkerProcess(forceOcr, remaining);

            lock (_workerGate)
            {
                // A pass that processed nothing is not repeated unless a request arrived meanwhile, so this cannot spin.
                if (!_workerPending && !(processed > 0 && HasPendingJobs()))
                {
                    FinishWorkerLocked();
                    return false;
                }
            }
        }
    }

    // Runs under _workerGate.
    private void FinishWorkerLocked()
    {
        _workerRunning = false;
        _workerPending = false;
        _workerForceOcr = false;
    }

    // Returns the number of jobs the worker processed; 0 when it did not start, was killed, or failed.
    private int RunWorkerProcess(bool forceOcr, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return 0;
        }

        bool ocr = Settings.Current.Ocr || forceOcr;
        Process? process = null;
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("process path is unknown");
            ProcessStartInfo info = new()
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("--worker");
            info.ArgumentList.Add(Settings.DataRoot);
            info.ArgumentList.Add(ocr ? "1" : "0");

            // Started under the gate so that Dispose either sees this child or prevents its start.
            lock (_workerGate)
            {
                if (_isDisposed)
                {
                    return 0;
                }

                process = Process.Start(info) ?? throw new InvalidOperationException("worker did not start");
                _workerProcess = process;
            }

            // WaitForExit rejects negative values and treats -1 as infinite, so the timeout is clamped to at least 1 ms.
            int timeoutMs = (int)Math.Max(1, Math.Min(int.MaxValue, remaining.TotalMilliseconds));
            if (!process.WaitForExit(timeoutMs))
            {
                Log.Info("worker stopped at the time limit");
                KillQuietly(process);
                return 0;
            }

            int code = process.ExitCode;
            return code is >= 0 and < FatalWorkerExitCode ? code : 0;
        }
        catch (Exception ex)
        {
            Log.Error("AppHost.RunWorkerProcess", ex);
            if (process is not null)
            {
                KillQuietly(process);
            }

            return 0;
        }
        finally
        {
            if (process is not null)
            {
                lock (_workerGate)
                {
                    if (ReferenceEquals(_workerProcess, process))
                    {
                        _workerProcess = null;
                    }
                }

                process.Dispose();
            }
        }
    }

    private bool HasPendingJobs()
    {
        try
        {
            return Store.Jobs.HasPending();
        }
        catch (Exception ex)
        {
            Log.Error("AppHost.HasPendingJobs", ex);
            return false;
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            Log.Error("AppHost.KillWorker", ex);
        }
    }

    // One failing dispose step must not skip the steps after it, because each one releases a different resource.
    private static void StepGuard(string where, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Error(where, ex);
        }
    }
}

// Writes the DIB form of a PNG in a child process, so a hung image codec can be killed without taking the palette down.
internal sealed class DibProvider : IDibProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private const string OutputPattern = "dib-*.dib";

    private readonly string _tempFolder;

    public DibProvider(string tempFolder)
    {
        _tempFolder = tempFolder;
    }

    public string? TryCreate(string pngPath)
    {
        try
        {
            Directory.CreateDirectory(_tempFolder);
            RemovePrevious();

            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("process path is unknown");
            string outPath = Path.Combine(_tempFolder, "dib-" + Guid.NewGuid().ToString("N") + ".dib");

            ProcessStartInfo info = new()
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("--dib");
            info.ArgumentList.Add(pngPath);
            info.ArgumentList.Add(outPath);

            using Process process = Process.Start(info) ?? throw new InvalidOperationException("dib writer did not start");
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Error("DibProvider.Kill", ex);
                }

                TryDelete(outPath);
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(outPath))
            {
                TryDelete(outPath);
                return null;
            }

            return outPath;
        }
        catch (Exception ex)
        {
            Log.Error("DibProvider.TryCreate", ex);
            return null;
        }
    }

    // Previous outputs are removed here, not after use, because the paste that read them may still be running.
    private void RemovePrevious()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_tempFolder, OutputPattern))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Error("DibProvider.RemovePrevious", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover that cannot be removed now is picked up by the next run.
        }
    }
}

// Maps a paste outcome to the line shown on the page. Sent and Busy are silent by design.
internal sealed class PasteNotice
{
    private volatile string? _current;

    public string? Current => _current;

    public void Set(PasteOutcome outcome)
    {
        if (outcome == PasteOutcome.Busy)
        {
            return;
        }

        _current = outcome switch
        {
            PasteOutcome.Elevated => "管理者権限のアプリには貼り付けられません。クリップボードにコピーしました。",
            PasteOutcome.FocusTimeout or PasteOutcome.NoTarget or PasteOutcome.SendFailed
                => "貼り付け先を確認できませんでした。クリップボードにコピーしました。",
            PasteOutcome.ModifiersHeld => "キーが押されたままのため貼り付けませんでした。",
            PasteOutcome.ClipboardFailed => "クリップボードを使用できませんでした。",
            PasteOutcome.SourceMissing => "元のデータが見つかりません。",
            _ => null,
        };
    }

    public void Clear() => _current = null;
}
