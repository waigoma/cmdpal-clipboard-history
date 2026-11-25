using System.Text;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Store;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class HistoryStoreWriteTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Upsert_SameKindAndHash_UpdatesInPlace_KeepsCreatedNameAndPin_AndReplacesFormats()
    {
        using var temp = new TempStore();
        var clock = new FakeClock();
        using var store = new HistoryStore(temp.Paths, clock);
        string hash = ContentHasher.ForText("hello");
        var first = store.Upsert(new CapturedEntry(EntryKind.Text, "hello",
            new[] { Format(FormatNames.Html, "<b>hello</b>"), Format(FormatNames.Rtf, "{\\rtf1 hello}") },
            hash, "app1.exe", T0));
        RawDb.Execute(temp.Paths, $"UPDATE entries SET name = 'keep', pinned = 1 WHERE id = {first.Id}");

        clock.Advance(TimeSpan.FromMinutes(5));
        var second = store.Upsert(new CapturedEntry(EntryKind.Text, "hello",
            new[] { Format(FormatNames.Html, "<i>hello</i>") }, hash, "app2.exe", clock.UtcNow));

        Assert.True(first.IsNew);
        Assert.Equal(first.Id, second.Id);
        Assert.False(second.IsNew);
        Assert.Equal(1, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM entries"));
        EntryDetail detail = Get(store, first.Id);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), detail.Summary.LastCopiedAt);
        Assert.Equal(T0, detail.Summary.CreatedAt);
        Assert.Equal("keep", detail.Summary.Name);
        Assert.True(detail.Summary.Pinned);
        Assert.Equal("app2.exe", detail.Summary.SourceApp);
        Assert.Equal(new[] { FormatNames.Html }, detail.Formats.Select(f => f.Name));
    }

    [Fact]
    public void Upsert_WithNullSource_KeepsPreviousSource()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string hash = ContentHasher.ForText("x");
        long id = store.Upsert(new CapturedEntry(EntryKind.Text, "x", Array.Empty<ClipFormat>(), hash, "app1.exe", T0)).Id;

        store.Upsert(new CapturedEntry(EntryKind.Text, "x", Array.Empty<ClipFormat>(), hash, null, T0));

        Assert.Equal("app1.exe", Get(store, id).Summary.SourceApp);
    }

    [Fact]
    public void Upsert_LastCopiedAtNeverMovesBackwards()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string hash = ContentHasher.ForText("x");
        long id = store.Upsert(new CapturedEntry(EntryKind.Text, "x", Array.Empty<ClipFormat>(), hash, null, T0 + TimeSpan.FromHours(1))).Id;

        store.Upsert(new CapturedEntry(EntryKind.Text, "x", Array.Empty<ClipFormat>(), hash, null, T0));

        Assert.Equal(T0 + TimeSpan.FromHours(1), Get(store, id).Summary.LastCopiedAt);
    }

    [Fact]
    public void Upsert_ProcessedImageRecopied_KeepsOcrThumbAndFormats_AndQueuesNothing()
    {
        using var temp = new TempStore();
        var clock = new FakeClock();
        using var store = new HistoryStore(temp.Paths, clock);
        byte[] png = { 0x89, 0x50, 0x4E, 0x47 };
        var entry = new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Png, png) },
            ContentHasher.ForBytes(png), "paint.exe", T0);
        long id = store.Upsert(entry).Id;
        Job? job = store.Jobs.Claim();
        Assert.NotNull(job);
        store.Jobs.Complete(job.EntryId);
        string pngFile = store.Blobs.Save(png, "png");
        store.CompleteImage(id, 640, 480, pngFile, "thumb.png", "文字", "");

        clock.Advance(TimeSpan.FromMinutes(1));
        var again = store.Upsert(entry with { CopiedAt = clock.UtcNow });

        Assert.False(again.IsNew);
        Assert.False(store.Jobs.HasPending());
        EntryDetail detail = Get(store, id);
        Assert.Equal(ImageState.Done, detail.Summary.ImageState);
        Assert.Equal("文字", detail.Ocr);
        Assert.True(detail.Summary.HasOcr);
        Assert.False(detail.Summary.HasQr);
        Assert.Equal(Path.Combine(temp.Paths.Thumbs, "thumb.png"), detail.Summary.ThumbPath);
        Assert.Equal(640, detail.Summary.Width);
        Assert.Equal(new[] { FormatNames.Png }, detail.Formats.Select(f => f.Name));
        Assert.Equal(T0 + TimeSpan.FromMinutes(1), detail.Summary.LastCopiedAt);
    }

    [Fact]
    public void Upsert_WithImageFormat_SetsPendingAndQueuesOneJob()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] dib = { 1, 2, 3 };

        long id = store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, dib) },
            ContentHasher.ForBytes(dib), null, T0)).Id;

        Assert.Equal(ImageState.Pending, Get(store, id).Summary.ImageState);
        Assert.True(store.Jobs.HasPending());
        Assert.Equal(id, store.Jobs.Claim()?.EntryId);
        Assert.Null(store.Jobs.Claim());
    }

    [Fact]
    public void Upsert_WithoutImageFormat_LeavesImageStateNone_AndQueuesNothing()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());

        long id = store.Upsert(Text("plain")).Id;

        Assert.Equal(ImageState.None, Get(store, id).Summary.ImageState);
        Assert.False(store.Jobs.HasPending());
    }

    [Fact]
    public void Upsert_ExistingUnprocessedImage_RequeuesAndResetsAttempts()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] dib = { 4, 5, 6 };
        var entry = new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, dib) },
            ContentHasher.ForBytes(dib), null, T0);
        long id = store.Upsert(entry).Id;
        store.Jobs.Claim();
        store.Jobs.Fail(id);
        store.Jobs.Claim();
        store.Jobs.Fail(id);

        store.Upsert(entry);

        Job? job = store.Jobs.Claim();
        Assert.Equal(id, job?.EntryId);
        Assert.Equal(0, job?.Attempts);
        Assert.Equal(ImageState.Pending, Get(store, id).Summary.ImageState);
    }

    [Fact]
    public void CompleteImage_WithPngFile_ReplacesDibRowsWithOnePngRow()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] dib = { 1, 1, 1 };
        byte[] dibV5 = { 2, 2, 2, 2 };
        byte[] png = { 9, 9, 9, 9, 9 };
        long id = store.Upsert(new CapturedEntry(EntryKind.Image, null,
            new[] { new ClipFormat(FormatNames.Dib, dib), new ClipFormat(FormatNames.DibV5, dibV5) },
            ContentHasher.ForBytes(dib), null, T0)).Id;
        string pngFile = store.Blobs.Save(png, "png");

        store.CompleteImage(id, 2, 2, pngFile, null, null, null);

        EntryDetail detail = Get(store, id);
        StoredFormat format = Assert.Single(detail.Formats);
        Assert.Equal(FormatNames.Png, format.Name);
        Assert.Equal(Path.Combine(temp.Paths.Files, pngFile), format.FilePath);
        Assert.Equal(png.Length, format.Size);
        Assert.Equal(ImageState.Done, detail.Summary.ImageState);
        Assert.Null(detail.Summary.ThumbPath);
        Assert.False(detail.Summary.HasOcr);
    }

    [Fact]
    public void CompleteImage_ForMissingId_IsNoOp_AndRaisesNothing()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        int raised = 0;
        store.Changed += () => raised++;

        store.CompleteImage(999, 1, 1, null, null, null, null);

        Assert.Equal(0, raised);
        Assert.Null(store.Get(999));
    }

    [Fact]
    public void Get_ReturnsFullBody_AndPreviewWithin200Units()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string body = new string('x', 100 * 1024);

        long id = store.Upsert(Text(body)).Id;

        EntryDetail detail = Get(store, id);
        Assert.Equal(body, detail.Body);
        Assert.True(detail.Summary.Preview.Length <= 200);
    }

    [Fact]
    public void Preview_CollapsesWhitespaceRuns_AndTrims()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());

        long id = store.Upsert(Text("  alpha\t\tbeta \r\n gamma  ")).Id;

        Assert.Equal("alpha beta gamma", Get(store, id).Summary.Preview);
    }

    [Fact]
    public void Preview_DoesNotSplitSurrogatePair()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string body = new string('a', 199) + "😀" + "bb";

        long id = store.Upsert(Text(body)).Id;

        Assert.Equal(new string('a', 199), Get(store, id).Summary.Preview);
    }

    [Fact]
    public void Preview_OfWhitespaceOnlyText_IsPlaceholder()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());

        long id = store.Upsert(Text("  \r\n\t ")).Id;

        Assert.Equal("(空白)", Get(store, id).Summary.Preview);
    }

    [Fact]
    public void Preview_OfFiles_NamesFirstFileAndCountsTheRest()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string body = @"C:\a\One.txt" + "\n" + @"C:\b\Two.txt" + "\n" + @"C:\c\Three.txt";

        long id = store.Upsert(new CapturedEntry(EntryKind.Files, body, Array.Empty<ClipFormat>(),
            ContentHasher.ForFiles(body.Split('\n')), null, T0)).Id;

        EntrySummary summary = Get(store, id).Summary;
        Assert.Equal("One.txt ほか 2 件", summary.Preview);
        Assert.Equal(3, summary.LineCount);
    }

    [Fact]
    public void Preview_OfImage_IsPlaceholder()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] dib = { 7 };

        long id = store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, dib) },
            ContentHasher.ForBytes(dib), null, T0)).Id;

        Assert.Equal("画像", Get(store, id).Summary.Preview);
    }

    [Fact]
    public void Upsert_CountsBytesCharsAndLines_ByteSizeIncludesFormats()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] html = new byte[10];

        long id = store.Upsert(new CapturedEntry(EntryKind.Text, "ab\r\ncdé",
            new[] { new ClipFormat(FormatNames.Html, html) }, ContentHasher.ForText("ab\r\ncdé"), null, T0)).Id;

        EntrySummary summary = Get(store, id).Summary;
        Assert.Equal(8 + 10, summary.ByteSize);
        Assert.Equal(7, summary.CharCount);
        Assert.Equal(2, summary.LineCount);
    }

    [Fact]
    public void Upsert_RaisesChangedOnce_AndHandlerExceptionsDoNotEscape()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        int raised = 0;
        bool secondRan = false;
        store.Changed += () => throw new InvalidOperationException("handler failure");
        store.Changed += () => secondRan = true;
        store.Changed += () => raised++;

        store.Upsert(Text("x"));

        Assert.Equal(1, raised);
        Assert.True(secondRan);
    }

    [Fact]
    public void Meta_RoundTrips_AndNullRemoves()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());

        store.SetMeta("paused", "1");
        Assert.Equal("1", store.GetMeta("paused"));

        store.SetMeta("paused", null);
        Assert.Null(store.GetMeta("paused"));
    }

    [Fact]
    public void Reopen_KeepsEntriesAndBodies()
    {
        using var temp = new TempStore();
        long id;
        using (var first = new HistoryStore(temp.Paths, new FakeClock()))
        {
            id = first.Upsert(Text("kept")).Id;
        }

        using var second = new HistoryStore(temp.Paths, new FakeClock());

        Assert.Equal("kept", Get(second, id).Body);
    }

    [Fact]
    public void Constructor_RejectsSchemaNewerThanSupported_AndReleasesTheDatabase()
    {
        using var temp = new TempStore();
        new HistoryStore(temp.Paths, new FakeClock()).Dispose();
        RawDb.Execute(temp.Paths, "PRAGMA user_version = 2");

        Assert.Throws<InvalidDataException>(() => new HistoryStore(temp.Paths, new FakeClock()));

        // An open connection keeps the file locked on Windows, so deleting it proves the constructor closed it.
        File.Delete(temp.Paths.Db);
        Assert.False(File.Exists(temp.Paths.Db));
    }

    [Fact]
    public void CompleteImage_RecomputesByteSize_FromBodyAndCurrentFormats()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] dib = { 1, 1, 1 };
        long id = store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, dib) },
            ContentHasher.ForBytes(dib), null, T0)).Id;
        Assert.Equal(3, Get(store, id).Summary.ByteSize);
        byte[] png = { 9, 9, 9, 9, 9 };
        string pngFile = store.Blobs.Save(png, "png");

        store.CompleteImage(id, 2, 2, pngFile, null, null, null);

        Assert.Equal(png.Length, Get(store, id).Summary.ByteSize);
    }

    [Fact]
    public void Upsert_RecopyOfNonImageEntryWithoutImageFormats_ClearsImageStateJobAndThumb()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        CapturedEntry entry = Text("plain");
        long id = store.Upsert(entry).Id;
        RawDb.Execute(temp.Paths, $"UPDATE entries SET image_state = 1, thumb = 'stale.png' WHERE id = {id}");
        RawDb.Execute(temp.Paths, $"INSERT INTO jobs(entry_id, attempts, state) VALUES({id}, 0, 0)");

        store.Upsert(entry with { CopiedAt = T0 + TimeSpan.FromMinutes(1) });

        EntrySummary summary = Get(store, id).Summary;
        Assert.Equal(ImageState.None, summary.ImageState);
        Assert.Null(summary.ThumbPath);
        Assert.Null(store.Jobs.Claim());
        Assert.Equal(0, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM jobs"));
    }

    [Fact]
    public void Upsert_NewEntryCreatedAt_IsCapturedCopiedAt_NotClockNow()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        DateTimeOffset copied = T0 - TimeSpan.FromHours(1);
        CapturedEntry entry = Text("old copy");

        long id = store.Upsert(entry with { CopiedAt = copied }).Id;

        EntrySummary summary = Get(store, id).Summary;
        Assert.Equal(copied, summary.CreatedAt);
        Assert.Equal(copied, summary.LastCopiedAt);
    }

    [Fact]
    public void Upsert_WhenBlobWriteFails_LeavesNoRow_AndStoreStillWorks()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        // A file where the temp folder should be makes the blob write fail before any row is written.
        Directory.Delete(temp.Paths.Temp);
        File.WriteAllText(temp.Paths.Temp, "blocker");
        var entry = new CapturedEntry(EntryKind.Text, "with html", new[] { Format(FormatNames.Html, "<b>x</b>") },
            ContentHasher.ForText("with html"), null, T0);

        Assert.ThrowsAny<IOException>(() => store.Upsert(entry));

        Assert.Equal(0, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM entries"));
        File.Delete(temp.Paths.Temp);
        Directory.CreateDirectory(temp.Paths.Temp);
        store.Upsert(Text("after failure"));
        Assert.Equal(1, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM entries"));
    }

    [Fact]
    public async Task Changed_HandlerMayCallGet_WithoutDeadlock()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string? seenBody = null;
        // The first row of a fresh database is id 1, so the handler can read it while the write is still being announced.
        store.Changed += () => seenBody = store.Get(1)?.Body;

        // A timeout turns a deadlock into a failure instead of a hung test run.
        await Task.Run(() => store.Upsert(Text("reentrant"))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("reentrant", seenBody);
    }

    private static EntryDetail Get(HistoryStore store, long id) =>
        store.Get(id) ?? throw new InvalidOperationException("entry was not stored");

    private static ClipFormat Format(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    private static CapturedEntry Text(string body) =>
        new(EntryKind.Text, body, Array.Empty<ClipFormat>(), ContentHasher.ForText(body), "app.exe", T0);
}
