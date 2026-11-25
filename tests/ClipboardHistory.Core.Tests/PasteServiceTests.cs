using Waigoma.ClipboardHistory.Paste;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class PasteServiceTests
{
    private const nint Target = 0x10;
    private const nint SameProcessWindow = 0x11;
    private const nint Other = 0x20;
    private const nint Third = 0x30;
    private const nint CommandPalette = 0x40;

    private static readonly ClipboardPayload Sample = new() { Text = "hello" };

    [Fact]
    public void Send_RestoresFocusThenSendsCtrlVOnce_AndReportsSentAfterSettle()
    {
        var h = new Harness();
        h.Windows.Pids[Target] = 100;
        h.Windows.Pids[Other] = 200;
        h.Windows.Foreground = Other;
        h.Windows.LastTarget = Target;
        h.Delay.At(30, () => h.Windows.Foreground = Target);

        PasteOutcome? immediate = null;
        PasteOutcome? outcome = null;
        long doneAt = -1;
        immediate = h.Service.Begin(Sample, false, o =>
        {
            outcome = o;
            doneAt = h.Delay.NowMs;
        });

        Assert.Null(immediate);
        Assert.Equal(PasteOutcome.Sent, outcome);
        Assert.Equal(1, h.Writer.Writes);
        // Focus returns at 30 ms, then 50 ms settle before the single Ctrl+V at 80 ms, then 150 ms settle.
        Assert.Equal(new long[] { 80 }, h.Windows.SendTimes);
        Assert.Equal(230, doneAt);
    }

    [Fact]
    public void Send_AcceptsAnotherWindowOfTheSameProcess()
    {
        var h = new Harness();
        h.Windows.Pids[Target] = 100;
        h.Windows.Pids[SameProcessWindow] = 100;
        h.Windows.Foreground = SameProcessWindow;
        h.Windows.LastTarget = Target;

        PasteOutcome? outcome = null;
        h.Service.Begin(Sample, false, o => outcome = o);

        Assert.Equal(PasteOutcome.Sent, outcome);
        Assert.Equal(new long[] { 50 }, h.Windows.SendTimes);
    }

    [Fact]
    public void NoTarget_WhenLastTargetIsZero_WritesButDoesNotStartWorker()
    {
        var h = new Harness();
        h.Windows.LastTarget = 0;

        PasteOutcome? immediate = h.Service.Begin(Sample, false, _ => Assert.Fail("done must not run"));

        Assert.Equal(PasteOutcome.NoTarget, immediate);
        Assert.Equal(1, h.Writer.Writes);
        Assert.Equal(0, h.BackgroundRuns);
        Assert.Empty(h.Windows.SendTimes);
    }

    [Fact]
    public void FocusTimeout_WaitsFull800Ms_WhenAnotherWindowIsForeground()
    {
        var h = new Harness();
        h.Windows.Pids[Target] = 100;
        h.Windows.Pids[Third] = 300;
        h.Windows.Foreground = Third;
        h.Windows.LastTarget = Target;

        PasteOutcome? outcome = null;
        long doneAt = -1;
        h.Service.Begin(Sample, false, o =>
        {
            outcome = o;
            doneAt = h.Delay.NowMs;
        });

        Assert.Equal(PasteOutcome.FocusTimeout, outcome);
        Assert.Equal(800, doneAt);
        Assert.Empty(h.Windows.SendTimes);
    }

    [Fact]
    public void Elevated_WhenActualForegroundIsElevated_DoesNotSend()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.Windows.Elevated.Add(Target);

        PasteOutcome? outcome = null;
        h.Service.Begin(Sample, false, o => outcome = o);

        Assert.Equal(PasteOutcome.Elevated, outcome);
        Assert.Empty(h.Windows.SendTimes);
    }

    [Fact]
    public void ModifiersHeld_WhenStillDownAfter2000Ms_DoesNotSend()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.Windows.ModifierDownUntilMs = 60_000;

        PasteOutcome? outcome = null;
        h.Service.Begin(Sample, false, o => outcome = o);

        Assert.Equal(PasteOutcome.ModifiersHeld, outcome);
        Assert.Empty(h.Windows.SendTimes);
    }

    [Fact]
    public void ModifierReleasedEarly_SendsAfterTheShortWait()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.Windows.ModifierDownUntilMs = 500;

        PasteOutcome? outcome = null;
        h.Service.Begin(Sample, false, o => outcome = o);

        Assert.Equal(PasteOutcome.Sent, outcome);
        Assert.Equal(new long[] { 550 }, h.Windows.SendTimes);
    }

    [Fact]
    public void FocusChangedWhileWaitingForModifiers_IsFocusTimeoutAndDoesNotSend()
    {
        var h = new Harness();
        h.Windows.Pids[Target] = 100;
        h.Windows.Pids[Other] = 200;
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.Windows.ModifierDownUntilMs = 60_000;
        h.Delay.At(300, () => h.Windows.Foreground = Other);

        PasteOutcome? outcome = null;
        h.Service.Begin(Sample, false, o => outcome = o);

        Assert.Equal(PasteOutcome.FocusTimeout, outcome);
        Assert.Empty(h.Windows.SendTimes);
    }

    [Fact]
    public void Hotkey_UsesForegroundAtCallTime_WithoutFocusWait()
    {
        var h = new Harness();
        h.Windows.Pids[Target] = 100;
        h.Windows.Pids[Other] = 200;
        h.Windows.Foreground = Other;
        h.Windows.LastTarget = Target;

        PasteOutcome? outcome = null;
        h.Service.Begin(Sample, true, o => outcome = o);

        Assert.Equal(PasteOutcome.Sent, outcome);
        Assert.Equal(new long[] { 50 }, h.Windows.SendTimes);
    }

    [Fact]
    public void Hotkey_ForegroundIsCommandPalette_ReturnsNoTargetWithoutWriting()
    {
        var h = new Harness();
        h.Windows.Foreground = CommandPalette;
        h.Windows.CommandPalettes.Add(CommandPalette);

        PasteOutcome? immediate = h.Service.Begin(Sample, true, _ => Assert.Fail("done must not run"));

        Assert.Equal(PasteOutcome.NoTarget, immediate);
        Assert.Equal(0, h.Writer.Writes);
        Assert.Equal(0, h.BackgroundRuns);
    }

    [Fact]
    public void Hotkey_NoForegroundWindow_ReturnsNoTargetWithoutWriting()
    {
        var h = new Harness();
        h.Windows.Foreground = 0;

        PasteOutcome? immediate = h.Service.Begin(Sample, true, _ => Assert.Fail("done must not run"));

        Assert.Equal(PasteOutcome.NoTarget, immediate);
        Assert.Equal(0, h.Writer.Writes);
    }

    [Fact]
    public void SecondBegin_WhileRunning_ReturnsBusyAndFirstRunIsUnaffected()
    {
        var h = new Harness(runInline: false);
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;

        PasteOutcome? first = null;
        Assert.Null(h.Service.Begin(Sample, false, o => first = o));

        PasteOutcome? second = h.Service.Begin(Sample, false, _ => Assert.Fail("done must not run"));

        Assert.Equal(PasteOutcome.Busy, second);
        Assert.Equal(1, h.Writer.Writes);
        Assert.Equal(1, h.Queued);

        h.RunQueued();

        Assert.Equal(PasteOutcome.Sent, first);
        Assert.Single(h.Windows.SendTimes);
    }

    [Fact]
    public void CanBeginAgain_AfterPreviousRunFinished()
    {
        var h = new Harness(runInline: false);
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;

        Assert.Null(h.Service.Begin(Sample, false, _ => { }));
        h.RunQueued();

        PasteOutcome? immediate = h.Service.Begin(Sample, false, _ => { });
        Assert.Null(immediate);
        Assert.Equal(1, h.Queued);
    }

    [Fact]
    public void DoneRunsAfterFlagIsReleased()
    {
        var h = new Harness(runInline: false);
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;

        PasteOutcome? nested = null;
        Assert.Null(h.Service.Begin(Sample, false, _ =>
        {
            nested = h.Service.Begin(Sample, false, _ => { });
        }));

        h.RunQueued();

        Assert.False(nested == PasteOutcome.Busy);
    }

    [Fact]
    public void WriteFalse_ReturnsClipboardFailedWithoutSending_AndRecovers()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.Writer.Result = false;

        PasteOutcome? immediate = h.Service.Begin(Sample, false, _ => Assert.Fail("done must not run"));

        Assert.Equal(PasteOutcome.ClipboardFailed, immediate);
        Assert.Equal(0, h.BackgroundRuns);
        Assert.Empty(h.Windows.SendTimes);

        h.Writer.Result = true;
        PasteOutcome? outcome = null;
        Assert.Null(h.Service.Begin(Sample, false, o => outcome = o));
        Assert.Equal(PasteOutcome.Sent, outcome);
    }

    [Fact]
    public void WriteThrows_ReturnsClipboardFailed_AndRecovers()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.Writer.ThrowOnWrite = new InvalidOperationException("write");

        PasteOutcome? immediate = h.Service.Begin(Sample, false, _ => { });
        Assert.Equal(PasteOutcome.ClipboardFailed, immediate);

        h.Writer.ThrowOnWrite = null;
        PasteOutcome? outcome = null;
        Assert.Null(h.Service.Begin(Sample, false, o => outcome = o));
        Assert.Equal(PasteOutcome.Sent, outcome);
    }

    [Fact]
    public void RunInBackgroundThrows_ReturnsClipboardFailed_AndRecovers()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.ThrowOnRun = true;

        PasteOutcome? immediate = h.Service.Begin(Sample, false, _ => { });
        Assert.Equal(PasteOutcome.ClipboardFailed, immediate);

        h.ThrowOnRun = false;
        PasteOutcome? outcome = null;
        Assert.Null(h.Service.Begin(Sample, false, o => outcome = o));
        Assert.Equal(PasteOutcome.Sent, outcome);
    }

    [Fact]
    public void SendCtrlVReturnsFalse_ReportsSendFailed()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.Windows.SendResult = false;

        var outcomes = new List<PasteOutcome>();
        Assert.Null(h.Service.Begin(Sample, false, outcomes.Add));

        Assert.Equal(new[] { PasteOutcome.SendFailed }, outcomes);
        Assert.Single(h.Windows.SendTimes);
    }

    [Fact]
    public void SendCtrlVThrows_ReportsSendFailed_ReleasesFlagAndRecovers()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;
        h.Windows.SendException = new InvalidOperationException("send");

        var outcomes = new List<PasteOutcome>();
        Assert.Null(h.Service.Begin(Sample, false, outcomes.Add));
        Assert.Equal(new[] { PasteOutcome.SendFailed }, outcomes);

        h.Windows.SendException = null;
        PasteOutcome? outcome = null;
        Assert.Null(h.Service.Begin(Sample, false, o => outcome = o));
        Assert.Equal(PasteOutcome.Sent, outcome);
    }

    [Fact]
    public void DoneThrows_IsContained_AndFlagIsReleased()
    {
        var h = new Harness();
        h.Windows.Foreground = Target;
        h.Windows.LastTarget = Target;

        Exception? escaped = Record.Exception(() =>
            h.Service.Begin(Sample, false, _ => throw new InvalidOperationException("done")));

        Assert.Null(escaped);

        PasteOutcome? outcome = null;
        Assert.Null(h.Service.Begin(Sample, false, o => outcome = o));
        Assert.Equal(PasteOutcome.Sent, outcome);
    }

    private sealed class Harness
    {
        private readonly List<Action> _queued = new();
        private readonly bool _runInline;

        public Harness(bool runInline = true)
        {
            _runInline = runInline;
            Delay = new VirtualDelay();
            Windows = new FakeWindows(Delay);
            Writer = new FakeWriter();
            Service = new PasteService(Windows, Writer, Delay, RunInBackground);
        }

        public VirtualDelay Delay { get; }

        public FakeWindows Windows { get; }

        public FakeWriter Writer { get; }

        public PasteService Service { get; }

        public int BackgroundRuns { get; private set; }

        public bool ThrowOnRun { get; set; }

        public int Queued => _queued.Count;

        public void RunQueued()
        {
            List<Action> batch = _queued.ToList();
            _queued.Clear();
            foreach (Action work in batch)
            {
                work();
            }
        }

        private void RunInBackground(Action work)
        {
            BackgroundRuns++;
            if (ThrowOnRun)
            {
                throw new InvalidOperationException("background");
            }

            if (_runInline)
            {
                work();
            }
            else
            {
                _queued.Add(work);
            }
        }
    }

    private sealed class FakeWindows : IWindowSystem
    {
        private readonly VirtualDelay _delay;

        public FakeWindows(VirtualDelay delay)
        {
            _delay = delay;
        }

        public nint Foreground { get; set; }

        public nint LastTarget { get; set; }

        public Dictionary<nint, uint> Pids { get; } = new();

        public HashSet<nint> Elevated { get; } = new();

        public HashSet<nint> CommandPalettes { get; } = new();

        public long ModifierDownUntilMs { get; set; }

        public bool SendResult { get; set; } = true;

        public Exception? SendException { get; set; }

        public List<long> SendTimes { get; } = new();

        public uint PidOf(nint hwnd) => Pids.TryGetValue(hwnd, out uint pid) ? pid : 0;

        public bool IsCommandPalette(nint hwnd) => CommandPalettes.Contains(hwnd);

        public bool IsElevatedOrUnknown(nint hwnd) => hwnd == 0 || Elevated.Contains(hwnd);

        public bool AnyModifierDown() => _delay.NowMs < ModifierDownUntilMs;

        public bool SendCtrlV()
        {
            SendTimes.Add(_delay.NowMs);
            if (SendException is not null)
            {
                throw SendException;
            }

            return SendResult;
        }
    }

    private sealed class FakeWriter : IClipboardWriter
    {
        public int Writes { get; private set; }

        public bool Result { get; set; } = true;

        public Exception? ThrowOnWrite { get; set; }

        public bool Write(ClipboardPayload payload)
        {
            Writes++;
            if (ThrowOnWrite is not null)
            {
                throw ThrowOnWrite;
            }

            return Result;
        }
    }

    // Sleep advances virtual time and fires scripted window changes that fall due, so no test waits in real time.
    private sealed class VirtualDelay : IDelay
    {
        private readonly List<ScheduledEvent> _events = new();

        public long NowMs { get; private set; }

        public void At(long ms, Action action) => _events.Add(new ScheduledEvent(ms, action));

        public void Sleep(int ms)
        {
            NowMs += ms;
            foreach (ScheduledEvent due in _events.Where(e => !e.Fired && e.AtMs <= NowMs).ToList())
            {
                due.Fired = true;
                due.Action();
            }
        }

        private sealed class ScheduledEvent
        {
            public ScheduledEvent(long atMs, Action action)
            {
                AtMs = atMs;
                Action = action;
            }

            public long AtMs { get; }

            public Action Action { get; }

            public bool Fired { get; set; }
        }
    }
}
