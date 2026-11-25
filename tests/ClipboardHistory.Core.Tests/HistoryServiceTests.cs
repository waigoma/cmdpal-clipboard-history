using System.Globalization;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Sequence;
using Waigoma.ClipboardHistory.Store;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class HistoryServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnClipboardChanged_NewText_RecordsOneEntry_AndResetsSequence()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create();
        int loads = 0;
        Func<IReadOnlyList<long>> load = () => { loads++; return new long[] { 1 }; };
        f.Sequence.Peek(load, _ => true);

        f.Current = TextCopy(2, "first copy");
        service.OnClipboardChanged();
        f.Sequence.Peek(load, _ => true);

        Assert.Equal(1, f.Rows());
        Assert.Equal(2, loads);
        Assert.NotNull(f.Store.GetMeta("heartbeat"));
    }

    [Fact]
    public void OnClipboardChanged_OwnWrite_RecordsNothing_AndDoesNotReset()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create();
        int loads = 0;
        Func<IReadOnlyList<long>> load = () => { loads++; return new long[] { 1 }; };
        f.Sequence.Peek(load, _ => true);

        f.Current = OwnWrite(2, "echo of our own write");
        service.OnClipboardChanged();
        f.Sequence.Peek(load, _ => true);

        Assert.Equal(0, f.Rows());
        Assert.Equal(1, loads);
        Assert.Equal("2", f.Store.GetMeta("last_sequence"));
    }

    [Fact]
    public void OnClipboardChanged_WhenPaused_RecordsNothing_ResetsSequence_AndStoresSequence()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create();
        int loads = 0;
        Func<IReadOnlyList<long>> load = () => { loads++; return new long[] { 1 }; };
        f.Sequence.Peek(load, _ => true);
        service.Paused = true;

        f.Current = TextCopy(2, "private");
        service.OnClipboardChanged();
        f.Sequence.Peek(load, _ => true);

        Assert.Equal(0, f.Rows());
        Assert.Equal(2, loads);
        Assert.Equal("2", f.Store.GetMeta("last_sequence"));
    }

    [Fact]
    public void PausedCopy_ThenUncleanExit_DoesNotBackfillThatCopy()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromHours(1)));
        // Not disposed on purpose: the process is assumed to die right after the copy, so no Dispose heartbeat is written.
        HistoryService paused = f.Create();
        paused.Paused = true;
        f.Current = TextCopy(2, "private");
        paused.OnClipboardChanged();

        f.Current = null;
        f.Backfill.Items.Add(new BackfillItem("private", null, T0));
        using HistoryService restarted = f.Create();
        restarted.Paused = false;
        restarted.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void UpsertFailure_DoesNotStoreLastSequence()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create();
        f.Store.SetMeta("last_sequence", "1");
        // Dropping the table makes Upsert throw, which is the failure this checks.
        RawDb.Execute(f.Paths, "DROP TABLE entries");

        f.Current = TextCopy(2, "cannot be stored");
        service.OnClipboardChanged();

        Assert.Equal("1", f.Store.GetMeta("last_sequence"));
    }

    [Fact]
    public void OnClipboardChanged_ExcludedApp_RecordsNothing()
    {
        using var f = new ServiceFixture();
        f.Options = f.Options with { ExcludedApps = new HashSet<string>(StringComparer.Ordinal) { "keepass.exe" } };
        using HistoryService service = f.Create();

        f.Current = TextCopy(2, "password", @"C:\Apps\KeePass.exe");
        service.OnClipboardChanged();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void OnClipboardChanged_NullRead_RecordsNothing_AndDoesNotThrow()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create(read: () => null);

        service.OnClipboardChanged();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void OnClipboardChanged_ThrowingRead_RecordsNothing_AndDoesNotThrow()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create(read: () => throw new IOException("clipboard busy"));

        service.OnClipboardChanged();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void OnClipboardChanged_Image_StartsWorkerOnce()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create();

        f.Current = ImageCopy(2);
        service.OnClipboardChanged();

        Assert.Equal(1, f.Rows());
        Assert.Equal(1, f.WorkerStarts);
    }

    [Fact]
    public void OnClipboardChanged_Text_DoesNotStartWorker()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create();

        f.Current = TextCopy(2, "plain text");
        service.OnClipboardChanged();

        Assert.Equal(0, f.WorkerStarts);
    }

    [Fact]
    public void OnClipboardChanged_OverTotalLimit_RemovesOldestUnpinned()
    {
        using var f = new ServiceFixture();
        f.Options = f.Options with { MaxTotalBytes = 10 };
        using HistoryService service = f.Create();

        f.Current = TextCopy(1, "aaaa");
        service.OnClipboardChanged();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Current = TextCopy(2, "bbbbbbbb");
        service.OnClipboardChanged();

        HistoryQuery all = new(string.Empty, null, 0, 100);
        EntrySummary[] rows = f.Store.Query(all).ToArray();
        Assert.Single(rows);
        Assert.Equal("bbbbbbbb", rows[0].Preview);
    }

    [Fact]
    public void Startup_SameLastSequence_DoesNotRecordCurrentClipboard()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("last_sequence", "7");
        f.Current = TextCopy(7, "already seen");
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Startup_DifferentSequence_RecordsCurrentClipboard()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("last_sequence", "6");
        f.Current = TextCopy(7, "copied while closed");
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(1, f.Rows());
        Assert.Equal("7", f.Store.GetMeta("last_sequence"));
    }

    [Fact]
    public void Startup_WithoutHeartbeat_DoesNotReadWindowsHistory()
    {
        using var f = new ServiceFixture();
        f.Backfill.Items.Add(new BackfillItem("from history", null, T0 - TimeSpan.FromMinutes(5)));
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Backfill.Calls);
        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Startup_WithExcludedApps_DoesNotReadWindowsHistory()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromHours(1)));
        f.Options = f.Options with { ExcludedApps = new HashSet<string>(StringComparer.Ordinal) { "keepass.exe" } };
        f.Backfill.Items.Add(new BackfillItem("from history", null, T0));
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Backfill.Calls);
        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Startup_WhenPaused_DoesNotReadWindowsHistory_AndStoresCurrentSequence()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromHours(1)));
        f.Store.SetMeta("paused", "1");
        f.Backfill.Items.Add(new BackfillItem("from history", null, T0));
        f.Current = TextCopy(9, "copied while paused");
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Backfill.Calls);
        Assert.Equal(0, f.Rows());
        Assert.Equal("9", f.Store.GetMeta("last_sequence"));
    }

    [Fact]
    public void Startup_WindowsHistory_RecordsOnlyNewerThanHeartbeat_OldestFirst_WithOriginalCopiedAt()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 + TimeSpan.FromMinutes(10)));
        // The source is trusted to filter by time, but the service filters again so a lax source cannot revive old items.
        f.Backfill.Items.Add(new BackfillItem("old", null, T0 + TimeSpan.FromMinutes(5)));
        f.Backfill.Items.Add(new BackfillItem("newer two", null, T0 + TimeSpan.FromMinutes(30)));
        f.Backfill.Items.Add(new BackfillItem("newer one", null, T0 + TimeSpan.FromMinutes(20)));
        using HistoryService service = f.Create();

        service.Startup();

        EntrySummary[] rows = f.Store.Query(new HistoryQuery(string.Empty, null, 0, 100)).OrderBy(r => r.Id).ToArray();
        Assert.Equal(new[] { "newer one", "newer two" }, rows.Select(r => r.Preview).ToArray());
        Assert.Equal(new[] { T0 + TimeSpan.FromMinutes(20), T0 + TimeSpan.FromMinutes(30) }, rows.Select(r => r.LastCopiedAt).ToArray());
    }

    [Fact]
    public void Startup_WindowsHistory_ExistingContent_IsNotDuplicated_AndLastCopiedAtNeverMovesBack()
    {
        using var f = new ServiceFixture();
        f.Store.Upsert(new CapturedEntry(EntryKind.Text, "same", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("same"), null, T0 + TimeSpan.FromHours(2)));
        f.Store.SetMeta("heartbeat", Heartbeat(T0 + TimeSpan.FromHours(1)));
        f.Backfill.Items.Add(new BackfillItem("same", null, T0 + TimeSpan.FromMinutes(90)));
        using HistoryService service = f.Create();

        service.Startup();

        EntrySummary row = Assert.Single(f.Store.Query(new HistoryQuery(string.Empty, null, 0, 100)));
        Assert.Equal(T0 + TimeSpan.FromHours(2), row.LastCopiedAt);
    }

    [Fact]
    public void Startup_HeartbeatOlderThanADay_SkipsExpiry_BecauseTheBootClockMayBeWrong()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromDays(2)));
        f.Store.Upsert(new CapturedEntry(EntryKind.Text, "ancient", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("ancient"), null, T0 - TimeSpan.FromDays(100)));
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(1, f.Rows());
    }

    [Fact]
    public void Startup_HeartbeatWithinADay_StillExpires()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromHours(23)));
        f.Store.Upsert(new CapturedEntry(EntryKind.Text, "ancient", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("ancient"), null, T0 - TimeSpan.FromDays(100)));
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Startup_ExpiresEntriesOlderThanRetention()
    {
        using var f = new ServiceFixture();
        f.Store.Upsert(new CapturedEntry(EntryKind.Text, "ancient", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("ancient"), null, T0));
        f.Clock.Advance(TimeSpan.FromDays(91));
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void DeletedItem_DoesNotReturnAfterRestart()
    {
        using var f = new ServiceFixture();
        f.Current = TextCopy(5, "secret note");
        using (HistoryService first = f.Create())
        {
            first.OnClipboardChanged();
            long id = f.Store.Query(new HistoryQuery(string.Empty, null, 0, 100)).Single().Id;
            f.Store.Delete(id);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            first.Tick();
        }

        // The source still reports the original copy, with its original time, which is older than the heartbeat.
        f.Backfill.Items.Add(new BackfillItem("secret note", null, T0));
        using HistoryService restarted = f.Create();
        restarted.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Tick_RunsMaintenanceOnceEverySixtyCalls()
    {
        using var f = new ServiceFixture();
        f.Store.Upsert(new CapturedEntry(EntryKind.Text, "old", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("old"), null, T0));
        f.Clock.Advance(TimeSpan.FromDays(91));
        using HistoryService service = f.Create();

        for (int i = 0; i < 59; i++)
        {
            service.Tick();
        }

        Assert.Equal(1, f.Rows());

        service.Tick();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Tick_WithoutRetention_DoesNotExpire()
    {
        using var f = new ServiceFixture();
        f.Options = f.Options with { Retention = null };
        f.Store.Upsert(new CapturedEntry(EntryKind.Text, "old", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("old"), null, T0));
        f.Clock.Advance(TimeSpan.FromDays(400));
        using HistoryService service = f.Create();

        for (int i = 0; i < 60; i++)
        {
            service.Tick();
        }

        Assert.Equal(1, f.Rows());
    }

    [Fact]
    public void Paused_IsPersisted_AndIsReadByNewInstance()
    {
        using var f = new ServiceFixture();
        using (HistoryService first = f.Create())
        {
            first.Paused = true;
            first.Paused = true;
        }

        Assert.Equal("1", f.Store.GetMeta("paused"));

        using HistoryService second = f.Create();
        Assert.True(second.Paused);
        second.Paused = false;
        Assert.Null(f.Store.GetMeta("paused"));
    }

    [Fact]
    public void Heartbeat_NeverGoesBackwards_StaleWriteDoesNotLowerStoredValue()
    {
        using var f = new ServiceFixture();
        using HistoryService service = f.Create();
        f.Clock.UtcNow = T0 + TimeSpan.FromMinutes(10);
        service.Tick();

        f.Clock.UtcNow = T0;
        service.Tick();

        Assert.Equal(Heartbeat(T0 + TimeSpan.FromMinutes(10)), f.Store.GetMeta("heartbeat"));
    }

    [Fact]
    public void ClockStepsBack_DeletedItem_DoesNotReturnAfterRestart()
    {
        using var f = new ServiceFixture();
        f.Current = TextCopy(5, "secret note");
        using (HistoryService first = f.Create())
        {
            first.OnClipboardChanged();
            long id = f.Store.Query(new HistoryQuery(string.Empty, null, 0, 100)).Single().Id;
            f.Store.Delete(id);
            f.Clock.UtcNow = T0 - TimeSpan.FromHours(1);
            first.Tick();
        }

        f.Backfill.Items.Add(new BackfillItem("secret note", null, T0));
        using HistoryService restarted = f.Create();
        restarted.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Startup_UsesHeartbeatReadAtConstruction_SoEarlierCaptureDoesNotHideOfflineCopies()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromMinutes(10)));
        using HistoryService service = f.Create();
        f.Current = TextCopy(5, "captured before startup");
        service.OnClipboardChanged();
        f.Current = null;
        f.Backfill.Items.Add(new BackfillItem("offline copy", null, T0 - TimeSpan.FromMinutes(5)));

        service.Startup();

        string[] previews = f.Store.Query(new HistoryQuery(string.Empty, null, 0, 100)).Select(r => r.Preview).ToArray();
        Assert.Contains("offline copy", previews);
    }

    [Fact]
    public void Backfill_UpsertRaisesHeartbeatToCopiedAt()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromHours(1)));
        f.Backfill.Items.Add(new BackfillItem("later copy", null, T0 + TimeSpan.FromMinutes(30)));
        using HistoryService service = f.Create();

        service.Startup();

        long stored = long.Parse(f.Store.GetMeta("heartbeat")!, CultureInfo.InvariantCulture);
        Assert.True(stored >= (T0 + TimeSpan.FromMinutes(30)).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Startup_ExpiresAgain_AfterBackfillAddsOldItems()
    {
        using var f = new ServiceFixture();
        // The heartbeat must be under a day old, or the clock-skew guard (A10) skips expiry. Backfill only returns items
        // newer than the heartbeat, so a short retention is what makes the backfilled item expire.
        f.Options = f.Options with { Retention = TimeSpan.FromHours(1) };
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromHours(2)));
        f.Backfill.Items.Add(new BackfillItem("old copy", null, T0 - TimeSpan.FromMinutes(90)));
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Startup_EnforcesTotalLimitAgain_AfterBackfillAddsEntries()
    {
        using var f = new ServiceFixture();
        f.Options = f.Options with { MaxTotalBytes = 10 };
        f.Store.Upsert(new CapturedEntry(EntryKind.Text, "aaaa", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("aaaa"), null, T0));
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromHours(1)));
        f.Backfill.Items.Add(new BackfillItem("bbbbbbbbb", null, T0 + TimeSpan.FromMinutes(1)));
        using HistoryService service = f.Create();

        service.Startup();

        EntrySummary row = Assert.Single(f.Store.Query(new HistoryQuery(string.Empty, null, 0, 100)));
        Assert.Equal("bbbbbbbbb", row.Preview);
    }

    [Fact]
    public void Startup_WithPendingImageJob_StartsWorker()
    {
        using var f = new ServiceFixture();
        byte[] png = { 1, 2, 3, 4 };
        f.Store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Png, png) },
            ContentHasher.ForBytes(png), null, T0));
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(1, f.WorkerStarts);
    }

    [Fact]
    public void Startup_CurrentClipboardWithExcludeMarker_IsNotStored()
    {
        using var f = new ServiceFixture();
        f.Current = new ClipboardSnapshot(5, null, false, true, false, null, "marked secret", null, Array.Empty<ClipFormat>());
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Startup_CurrentClipboardFromExcludedApp_IsNotStored()
    {
        using var f = new ServiceFixture();
        f.Options = f.Options with { ExcludedApps = new HashSet<string>(StringComparer.Ordinal) { "keepass.exe" } };
        f.Current = TextCopy(5, "from keepass", @"C:\Apps\KeePass.exe");
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Rows());
    }

    [Fact]
    public void Startup_CurrentOwnWrite_IsNotStored_ButSequenceIsStored()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("last_sequence", "4");
        f.Current = OwnWrite(5, "echo of our paste");
        using HistoryService service = f.Create();

        service.Startup();

        Assert.Equal(0, f.Rows());
        Assert.Equal("5", f.Store.GetMeta("last_sequence"));
    }

    [Fact]
    public void Dispose_WritesHeartbeat()
    {
        using var f = new ServiceFixture();
        f.Clock.UtcNow = T0 + TimeSpan.FromMinutes(5);

        using (HistoryService service = f.Create())
        {
        }

        Assert.Equal(Heartbeat(T0 + TimeSpan.FromMinutes(5)), f.Store.GetMeta("heartbeat"));
    }

    [Fact]
    public void Backfill_FilesOnlyItem_IsRecordedAsFiles()
    {
        using var f = new ServiceFixture();
        f.Store.SetMeta("heartbeat", Heartbeat(T0 - TimeSpan.FromHours(1)));
        f.Backfill.Items.Add(new BackfillItem(null, new[] { @"C:\data\note.txt" }, T0 - TimeSpan.FromMinutes(10)));
        using HistoryService service = f.Create();

        service.Startup();

        EntrySummary row = Assert.Single(f.Store.Query(new HistoryQuery(string.Empty, null, 0, 100)));
        Assert.Equal(EntryKind.Files, row.Kind);
        Assert.Equal("note.txt", row.Preview);
    }

    private static ClipboardSnapshot TextCopy(uint sequence, string text, string? exe = null) =>
        new(sequence, exe, false, false, false, null, text, null, Array.Empty<ClipFormat>());

    private static ClipboardSnapshot OwnWrite(uint sequence, string text) =>
        new(sequence, null, true, false, false, null, text, null, Array.Empty<ClipFormat>());

    private static ClipboardSnapshot ImageCopy(uint sequence) =>
        new(sequence, null, false, false, false, null, null, null, new[] { new ClipFormat(FormatNames.Png, new byte[] { 1, 2, 3, 4 }) });

    private static string Heartbeat(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private sealed class FakeBackfill : IBackfillSource
    {
        public List<BackfillItem> Items { get; } = new();

        public int Calls { get; private set; }

        // Ignores the bound on purpose: the service must apply it itself.
        public IReadOnlyList<BackfillItem> ReadNewerThan(DateTimeOffset since)
        {
            Calls++;
            return Items.ToArray();
        }
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly TempStore _temp = new();

        public ServiceFixture()
        {
            Clock = new FakeClock { UtcNow = T0 };
            Store = new HistoryStore(_temp.Paths, Clock);
            Sequence = new SequentialPaste(Clock, () => TimeSpan.FromMinutes(1));
        }

        public FakeClock Clock { get; }

        public HistoryStore Store { get; }

        public SequentialPaste Sequence { get; }

        public FakeBackfill Backfill { get; } = new();

        public StorePaths Paths => _temp.Paths;

        public HistoryOptions Options { get; set; } = new();

        public ClipboardSnapshot? Current { get; set; }

        public int WorkerStarts { get; private set; }

        public HistoryService Create(Func<ClipboardSnapshot?>? read = null)
        {
            Func<ClipboardSnapshot?> source = read is null ? () => Current : read;
            return new HistoryService(Store, () => Options, source, Backfill,
                () => { WorkerStarts++; }, Sequence, Clock);
        }

        public int Rows() => Store.Query(new HistoryQuery(string.Empty, null, 0, 1000)).Count;

        public void Dispose()
        {
            Store.Dispose();
            _temp.Dispose();
        }
    }
}
