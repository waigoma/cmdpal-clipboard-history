using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Waigoma.ClipboardHistory.Interop;

namespace Waigoma.ClipboardHistory.Capture;

// One STA thread owns a message-only window that receives clipboard updates and foreground changes.
// Callbacks run on that thread, so they must return quickly and must not block on the thread itself.
public sealed unsafe class ClipboardMonitor : IDisposable
{
    private const string WindowClassName = "Waigoma.ClipboardHistory.Monitor";
    private const int StartTimeoutMs = 5000;
    private const int JoinTimeoutMs = 2000;

    private static readonly object LiveGate = new();

    // Native callbacks are static because they cannot capture state, so they reach the single live instance here.
    private static ClipboardMonitor? _live;

    private readonly Action _onClipboardChanged;
    private readonly Action<nint> _onForegroundChanged;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private nint _window;
    private nint _hook;
    private uint? _lastSequence;
    private bool _started;
    private int _disposed;

    public ClipboardMonitor(Action onClipboardChanged, Action<nint> onForegroundChanged)
    {
        _onClipboardChanged = onClipboardChanged;
        _onForegroundChanged = onForegroundChanged;
    }

    public nint WindowHandle => Volatile.Read(ref _window);

    // Blocks until the window exists. Two monitors in one process would both claim the static callbacks, so a second is refused.
    public void Start()
    {
        lock (LiveGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(ClipboardMonitor));
            }

            if (_started)
            {
                throw new InvalidOperationException("ClipboardMonitor is already started.");
            }

            if (_live is not null)
            {
                throw new InvalidOperationException("Another ClipboardMonitor is already running.");
            }

            _started = true;
            _live = this;
        }

        _thread = new Thread(ThreadEntry) { IsBackground = true, Name = "ClipboardHistory.Monitor" };
        _thread.SetApartmentState(ApartmentState.STA);
        try
        {
            _thread.Start();
        }
        catch
        {
            // The thread never ran, so nothing else will clear the static reference.
            ReleaseLive();
            throw;
        }

        if (!_ready.Wait(StartTimeoutMs) || WindowHandle == 0)
        {
            Dispose();
            throw new InvalidOperationException("Clipboard monitor window did not start.");
        }
    }

    // Runs the action on the STA thread. Safe to call from any thread.
    public void Post(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _queue.Enqueue(action);
        nint window = WindowHandle;
        if (window != 0)
        {
            Native.PostMessageW(window, Native.WM_APP_QUEUE, 0, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        nint window = WindowHandle;
        if (window != 0)
        {
            Native.PostMessageW(window, Native.WM_CLOSE, 0, 0);
        }

        // Joining from the monitor thread would wait on itself, so the close message alone is enough there.
        // The static reference is cleared by the thread itself once it has released the window, so a timed-out
        // join leaves it in place for the close message still in flight.
        Thread? thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive)
        {
            thread.Join(JoinTimeoutMs);
        }
    }

    private void ThreadEntry()
    {
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            Log.Error("monitor.thread", ex);
        }
        finally
        {
            // Start waits on this event, so it must be set even when the window was never created.
            _ready.Set();
            ReleaseLive();
        }
    }

    private void Run()
    {
        nint instance = Native.GetModuleHandleW(null);
        fixed (char* className = WindowClassName)
        {
            var windowClass = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                hInstance = instance,
                lpszClassName = (nint)className,
            };

            // A class left over from an earlier monitor in this process is fine, so the failure is not checked.
            _ = Native.RegisterClassExW(&windowClass);
        }

        nint window = Native.CreateWindowExW(0, WindowClassName, "ClipboardHistory monitor", 0, 0, 0, 0, 0,
            Native.HWND_MESSAGE, 0, instance, 0);
        if (window == 0)
        {
            Log.Info("monitor.window create-failed");
            return;
        }

        Volatile.Write(ref _window, window);

        // Dispose sets _disposed before reading _window. The full fence orders this write before the read below,
        // so at least one side sees the other and the close message is never lost.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _disposed) != 0)
        {
            Native.PostMessageW(window, Native.WM_CLOSE, 0, 0);
        }

        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0,
            (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&WinEventProc,
            0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (_hook == 0)
        {
            // Without the hook the paste target cannot follow focus; clipboard capture still works.
            Log.Info("monitor.hook failed");
        }

        if (!Native.AddClipboardFormatListener(window))
        {
            Log.Info("monitor.listener failed");
            Release(window);
            Volatile.Write(ref _window, 0);
            return;
        }

        // The current foreground window is seeded through the same filter as later changes.
        SafeCall("monitor.seed", () => _onForegroundChanged(Native.GetForegroundWindow()));

        DrainQueue();
        _ready.Set();

        Native.MSG message = default;
        while (true)
        {
            int result = Native.GetMessageW(&message, 0, 0, 0);
            if (result <= 0)
            {
                if (result < 0)
                {
                    Log.Info("monitor.getmessage failed");
                }

                break;
            }

            Native.TranslateMessage(&message);
            Native.DispatchMessageW(&message);
        }
    }

    // Unhooks and destroys the window. Called once, on the monitor thread, when the close message arrives.
    private void Release(nint window)
    {
        nint hook = _hook;
        _hook = 0;
        if (hook != 0)
        {
            Native.UnhookWinEvent(hook);
        }

        Native.RemoveClipboardFormatListener(window);
        Native.DestroyWindow(window);
        Volatile.Write(ref _window, 0);
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out Action? action))
        {
            SafeCall("monitor.post", action);
        }
    }

    private void OnClipboardUpdate()
    {
        SafeCall("monitor.clipboard", () =>
        {
            // The same sequence number can arrive twice for one copy, and the second copy would be a duplicate.
            uint sequence = Native.GetClipboardSequenceNumber();
            if (_lastSequence == sequence)
            {
                return;
            }

            _lastSequence = sequence;
            _onClipboardChanged();
        });
    }

    private void ReleaseLive()
    {
        lock (LiveGate)
        {
            if (ReferenceEquals(_live, this))
            {
                _live = null;
            }
        }
    }

    private static void SafeCall(string where, Action action)
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

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        ClipboardMonitor? monitor = Volatile.Read(ref _live);
        try
        {
            switch (msg)
            {
                case Native.WM_CLIPBOARDUPDATE:
                    monitor?.OnClipboardUpdate();
                    return 0;
                case Native.WM_APP_QUEUE:
                    monitor?.DrainQueue();
                    return 0;
                case Native.WM_CLOSE:
                    if (monitor is not null)
                    {
                        monitor.Release(hwnd);
                    }
                    else
                    {
                        Native.DestroyWindow(hwnd);
                    }

                    Native.PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Log.Error("monitor.wndproc", ex);
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint timeMs)
    {
        try
        {
            if (eventType != Native.EVENT_SYSTEM_FOREGROUND || hwnd == 0)
            {
                return;
            }

            ClipboardMonitor? monitor = Volatile.Read(ref _live);
            if (monitor is not null)
            {
                monitor._onForegroundChanged(hwnd);
            }
        }
        catch (Exception ex)
        {
            Log.Error("monitor.winevent", ex);
        }
    }
}
