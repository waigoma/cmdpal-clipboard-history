using System.Text;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Store;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class JobQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Claim_ReturnsOldestPendingEntryFirst()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long first = AddImage(store, "one");
        long second = AddImage(store, "two");

        Assert.Equal(first, store.Jobs.Claim()?.EntryId);
        Assert.Equal(second, store.Jobs.Claim()?.EntryId);
        Assert.Null(store.Jobs.Claim());
    }

    [Fact]
    public void Claim_FromSecondConnection_NeverReturnsAJobAlreadyClaimed()
    {
        // The worker is another process, so it reaches the same jobs through its own connection.
        using var temp = new TempStore();
        using var app = new HistoryStore(temp.Paths, new FakeClock());
        using var worker = new HistoryStore(temp.Paths, new FakeClock());
        long first = AddImage(app, "one");
        long second = AddImage(app, "two");

        Job? fromApp = app.Jobs.Claim();
        Job? fromWorker = worker.Jobs.Claim();

        Assert.Equal(first, fromApp?.EntryId);
        Assert.Equal(second, fromWorker?.EntryId);
        Assert.Null(worker.Jobs.Claim());
    }

    [Fact]
    public void Claim_AfterId_SkipsJobsAtOrBelowIt()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long first = AddImage(store, "one");
        long second = AddImage(store, "two");
        long third = AddImage(store, "three");

        Assert.Equal(second, store.Jobs.Claim(first)?.EntryId);
        Assert.Equal(third, store.Jobs.Claim(second)?.EntryId);
        Assert.Null(store.Jobs.Claim(third));
        // The job below the cut-off stays pending for a later pass.
        Assert.Equal(0, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM jobs WHERE state = 0 AND entry_id > " + first));
        Assert.True(store.Jobs.HasPending());
    }

    [Fact]
    public void Claim_AfterLastClaimedId_DoesNotReturnAFailedJobAgainInTheSamePass()
    {
        // A failed job goes back to pending with a smaller id, so the pass must move past it rather than spin on it.
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long failing = AddImage(store, "one");
        long next = AddImage(store, "two");

        Job? claimed = store.Jobs.Claim(0);
        store.Jobs.Fail(failing);
        Job? following = store.Jobs.Claim(claimed!.EntryId);

        Assert.Equal(failing, claimed.EntryId);
        Assert.Equal(next, following?.EntryId);
        Assert.Null(store.Jobs.Claim(following!.EntryId));
    }

    [Fact]
    public void Complete_RemovesTheJob()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddImage(store, "one");
        Job? job = store.Jobs.Claim();

        store.Jobs.Complete(id);

        Assert.Equal(id, job?.EntryId);
        Assert.False(store.Jobs.HasPending());
        Assert.Equal(0, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM jobs"));
    }

    [Fact]
    public void Fail_ThreeTimes_MarksJobFailedAndEntryFailed_AndClaimReturnsNull()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddImage(store, "one");
        var attempts = new List<int>();

        for (int i = 0; i < 3; i++)
        {
            Job? job = store.Jobs.Claim();
            Assert.NotNull(job);
            attempts.Add(job.Attempts);
            store.Jobs.Fail(id);
        }

        Assert.Equal(new[] { 0, 1, 2 }, attempts);
        Assert.Equal(2, RawDb.Scalar(temp.Paths, "SELECT state FROM jobs WHERE entry_id = " + id));
        Assert.Null(store.Jobs.Claim());
        Assert.False(store.Jobs.HasPending());
        Assert.Equal(ImageState.Failed, store.Get(id)?.Summary.ImageState);
    }

    [Fact]
    public void Fail_AtTheLimit_RaisesChangedOnce()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddImage(store, "one");
        int raised = 0;
        store.Changed += () => raised++;

        store.Jobs.Claim();
        store.Jobs.Fail(id);
        Assert.Equal(0, raised);

        store.Jobs.Claim();
        store.Jobs.Fail(id);
        store.Jobs.Claim();
        store.Jobs.Fail(id);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ResetRunning_ReturnsRunningJobsToPending()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddImage(store, "one");
        store.Jobs.Claim();

        Assert.Equal(1, store.Jobs.ResetRunning());
        Assert.Equal(0, store.Jobs.ResetRunning());
        Assert.True(store.Jobs.HasPending());
        Assert.Equal(id, store.Jobs.Claim()?.EntryId);
    }

    [Fact]
    public void Enqueue_ResetsAttemptsAndState()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddImage(store, "one");
        store.Jobs.Claim();
        store.Jobs.Fail(id);

        store.Jobs.Enqueue(id);

        Assert.Equal(0, store.Jobs.Claim()?.Attempts);
    }

    private static long AddImage(HistoryStore store, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        var entry = new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, bytes) },
            ContentHasher.ForBytes(bytes), null, T0);
        return store.Upsert(entry).Id;
    }
}
