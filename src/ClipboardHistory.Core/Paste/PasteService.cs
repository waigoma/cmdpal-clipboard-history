using System.Threading;

namespace Waigoma.ClipboardHistory.Paste;

// Only one paste runs at a time: a second request would overwrite the clipboard while the first is still
// waiting for its target to regain focus.
public sealed class PasteService
{
    private const int FocusTimeoutMs = 800;
    private const int FocusPollMs = 10;
    private const int ModifierWaitMs = 2000;
    private const int ModifierPollMs = 10;
    private const int SettleBeforeSendMs = 50;
    private const int SettleAfterSendMs = 150;

    private readonly IWindowSystem _windows;
    private readonly IClipboardWriter _writer;
    private readonly IDelay _delay;
    private readonly Action<Action> _runInBackground;
    private int _busy;

    public PasteService(IWindowSystem windows, IClipboardWriter writer, IDelay delay, Action<Action> runInBackground)
    {
        _windows = windows;
        _writer = writer;
        _delay = delay;
        _runInBackground = runInBackground;
    }

    // Returns an immediate result when the paste cannot start. Otherwise returns null and done runs exactly once
    // from the background worker. done runs after the flag is released, so it may start the next paste.
    public PasteOutcome? Begin(ClipboardPayload payload, bool fromHotkey, Action<PasteOutcome> done)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return PasteOutcome.Busy;
        }

        try
        {
            nint target = fromHotkey ? _windows.Foreground : _windows.LastTarget;

            // Ctrl+V sent into the command palette or a shell surface would land in the wrong place, so refuse first.
            if (fromHotkey && !_windows.IsPasteTarget(target))
            {
                Release();
                return PasteOutcome.NoTarget;
            }

            if (!_writer.Write(payload))
            {
                Release();
                return PasteOutcome.ClipboardFailed;
            }

            // Without a target the content is still written, so the user can paste it manually.
            if (!fromHotkey && target == 0)
            {
                Release();
                return PasteOutcome.NoTarget;
            }

            _runInBackground(() => RunWorker(target, fromHotkey, done));
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("paste.begin", ex);
            Release();
            return PasteOutcome.ClipboardFailed;
        }
    }

    // Writes the payload without pasting. It shares the single-flight flag, because a write during a paste would replace
    // the content that the paste is about to send. Returns null when the clipboard now holds the payload.
    public PasteOutcome? Copy(ClipboardPayload payload)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return PasteOutcome.Busy;
        }

        try
        {
            return _writer.Write(payload) ? null : PasteOutcome.ClipboardFailed;
        }
        catch (Exception ex)
        {
            Log.Error("paste.copy", ex);
            return PasteOutcome.ClipboardFailed;
        }
        finally
        {
            Release();
        }
    }

    private void RunWorker(nint target, bool fromHotkey, Action<PasteOutcome> done)
    {
        PasteOutcome outcome;
        try
        {
            outcome = Deliver(target, fromHotkey);
        }
        catch (Exception ex)
        {
            Log.Error("paste.worker", ex);
            outcome = PasteOutcome.SendFailed;
        }

        Release();

        try
        {
            done(outcome);
        }
        catch (Exception ex)
        {
            Log.Error("paste.done", ex);
        }
    }

    private PasteOutcome Deliver(nint target, bool fromHotkey)
    {
        // The hotkey target is the window that was foreground when the key was pressed, so there is nothing to wait for.
        if (!fromHotkey && !WaitForFocus(target))
        {
            return PasteOutcome.FocusTimeout;
        }

        if (_windows.IsElevatedOrUnknown(_windows.Foreground))
        {
            return PasteOutcome.Elevated;
        }

        WaitForModifiers();
        _delay.Sleep(SettleBeforeSendMs);

        // Focus and modifiers are checked again right before sending, since either can change during the waits.
        if (!IsTargetForeground(target))
        {
            return PasteOutcome.FocusTimeout;
        }

        if (_windows.AnyModifierDown())
        {
            return PasteOutcome.ModifiersHeld;
        }

        if (!_windows.SendCtrlV())
        {
            return PasteOutcome.SendFailed;
        }

        _delay.Sleep(SettleAfterSendMs);
        return PasteOutcome.Sent;
    }

    private bool WaitForFocus(nint target)
    {
        long start = _delay.NowMs;
        while (!IsTargetForeground(target))
        {
            if (_delay.NowMs - start >= FocusTimeoutMs)
            {
                return false;
            }

            _delay.Sleep(FocusPollMs);
        }

        return true;
    }

    private void WaitForModifiers()
    {
        long start = _delay.NowMs;
        while (_windows.AnyModifierDown() && _delay.NowMs - start < ModifierWaitMs)
        {
            _delay.Sleep(ModifierPollMs);
        }
    }

    // Apps often move focus between their own windows, so the window system decides which of them count as the target.
    private bool IsTargetForeground(nint target)
    {
        nint foreground = _windows.Foreground;
        if (target == 0 || foreground == 0)
        {
            return false;
        }

        return _windows.IsSameTarget(target, foreground);
    }

    private void Release()
    {
        Interlocked.Exchange(ref _busy, 0);
    }
}
