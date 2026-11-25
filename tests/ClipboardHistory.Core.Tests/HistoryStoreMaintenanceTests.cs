using System.Text;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Store;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class HistoryStoreMaintenanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeleteSince_BoundaryIsInclusive_AndKeepsPinned()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        DateTimeOffset now = T0;
        long exact = AddText(store, "exact", now - TimeSpan.FromMinutes(5));
        long newer = AddText(store, "newer", now - TimeSpan.FromMinutes(1));
        long older = AddText(store, "older", now - TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        long pinned = AddText(store, "pinned", now - TimeSpan.FromMinutes(1));
        store.SetPinned(pinned, true);

        int removed = store.DeleteSince(now - TimeSpan.FromMinutes(5));

        Assert.Equal(2, removed);
        Assert.False(store.Exists(exact));
        Assert.False(store.Exists(newer));
        Assert.True(store.Exists(older));
        Assert.True(store.Exists(pinned));
    }

    [Fact]
    public void Expire_CutoffIsExclusive_AndKeepsPinned()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        DateTimeOffset cutoff = T0 - TimeSpan.FromHours(1);
        long older = AddText(store, "older", cutoff - TimeSpan.FromMilliseconds(1));
        long exact = AddText(store, "exact", cutoff);
        long pinnedOld = AddText(store, "pinned", cutoff - TimeSpan.FromDays(1));
        store.SetPinned(pinnedOld, true);

        int removed = store.Expire(cutoff);

        Assert.Equal(1, removed);
        Assert.False(store.Exists(older));
        Assert.True(store.Exists(exact));
        Assert.True(store.Exists(pinnedOld));
    }

    [Fact]
    public void DeleteAll_KeepsPinned()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "one", T0);
        AddText(store, "two", T0);
        long pinned = AddText(store, "pinned", T0);
        store.SetPinned(pinned, true);

        Assert.Equal(2, store.DeleteAll());

        Assert.True(store.Exists(pinned));
        Assert.Equal(1, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM entries"));
    }

    [Fact]
    public void EnforceTotalLimit_DeletesOldestUnpinned_UntilUnderLimit()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long oldest = AddText(store, new string('a', 100), T0 - TimeSpan.FromMinutes(3));
        long middle = AddText(store, new string('b', 100), T0 - TimeSpan.FromMinutes(2));
        long newest = AddText(store, new string('c', 100), T0 - TimeSpan.FromMinutes(1));
        Assert.Equal(300, store.TotalBytes());

        int removed = store.EnforceTotalLimit(250);

        Assert.Equal(1, removed);
        Assert.False(store.Exists(oldest));
        Assert.True(store.Exists(middle));
        Assert.True(store.Exists(newest));
        Assert.Equal(200, store.TotalBytes());
    }

    [Fact]
    public void EnforceTotalLimit_SkipsPinnedOldest()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long pinnedOldest = AddText(store, new string('a', 100), T0 - TimeSpan.FromMinutes(3));
        store.SetPinned(pinnedOldest, true);
        long middle = AddText(store, new string('b', 100), T0 - TimeSpan.FromMinutes(2));
        AddText(store, new string('c', 100), T0 - TimeSpan.FromMinutes(1));

        Assert.Equal(1, store.EnforceTotalLimit(250));

        Assert.True(store.Exists(pinnedOldest));
        Assert.False(store.Exists(middle));
    }

    [Fact]
    public void EnforceTotalLimit_OnlyPinnedOverLimit_DeletesNothing()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        for (int i = 0; i < 3; i++)
        {
            long id = AddText(store, new string((char)('a' + i), 100), T0 - TimeSpan.FromMinutes(i));
            store.SetPinned(id, true);
        }

        Assert.Equal(0, store.EnforceTotalLimit(250));
        Assert.Equal(300, store.TotalBytes());
    }

    [Fact]
    public void EnforceTotalLimit_PinsAtOrOverLimit_DeletesNothingEvenWithUnpinnedRows()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        for (int i = 0; i < 3; i++)
        {
            long pinned = AddText(store, new string((char)('a' + i), 100), T0 - TimeSpan.FromMinutes(10 + i));
            store.SetPinned(pinned, true);
        }
        long unpinnedOld = AddText(store, new string('x', 50), T0 - TimeSpan.FromMinutes(2));
        long unpinnedNew = AddText(store, new string('y', 50), T0 - TimeSpan.FromMinutes(1));

        Assert.Equal(0, store.EnforceTotalLimit(250));

        Assert.True(store.Exists(unpinnedOld));
        Assert.True(store.Exists(unpinnedNew));
        Assert.Equal(400, store.TotalBytes());
    }

    [Fact]
    public void EnforceTotalLimit_NewestRowOverLimitAlone_DeletesNothing()
    {
        // Trimming the other rows could never bring the total under the limit, so they are kept.
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long older = AddText(store, new string('a', 100), T0 - TimeSpan.FromMinutes(2));
        long huge = AddText(store, new string('b', 500), T0 - TimeSpan.FromMinutes(1));

        Assert.Equal(0, store.EnforceTotalLimit(250));

        Assert.True(store.Exists(older));
        Assert.True(store.Exists(huge));
    }

    [Fact]
    public void EnforceTotalLimit_PinnedPlusNewestOverLimit_DeletesNothing_EvenWithOtherUnpinnedRows()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long pinned = AddText(store, new string('p', 100), T0 - TimeSpan.FromMinutes(5));
        store.SetPinned(pinned, true);
        long older = AddText(store, new string('o', 50), T0 - TimeSpan.FromMinutes(2));
        long newest = AddText(store, new string('n', 300), T0 - TimeSpan.FromMinutes(1));

        Assert.Equal(0, store.EnforceTotalLimit(250));

        Assert.True(store.Exists(older));
        Assert.True(store.Exists(newest));
        Assert.Equal(450, store.TotalBytes());
    }

    [Fact]
    public void EnforceTotalLimit_NewestRowIsNeverDeleted_EvenWhenItIsTheOnlyUnpinnedRow()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long pinned = AddText(store, new string('p', 100), T0 - TimeSpan.FromMinutes(5));
        store.SetPinned(pinned, true);
        long newest = AddText(store, new string('n', 300), T0);

        Assert.Equal(0, store.EnforceTotalLimit(250));

        Assert.True(store.Exists(pinned));
        Assert.True(store.Exists(newest));
    }

    [Fact]
    public void Id_IsNotReusedAfterTheNewestEntryIsDeleted()
    {
        // Ids are handed to the UI and to the worker, so a deleted id must not come back for a different entry.
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "one", T0 - TimeSpan.FromMinutes(2));
        long newest = AddText(store, "two", T0 - TimeSpan.FromMinutes(1));

        store.Delete(newest);
        long next = AddText(store, "three", T0);

        Assert.True(next > newest);
    }

    [Fact]
    public void Touch_MovesLastCopiedAtToNow_AndMissingIdIsNoOp()
    {
        using var temp = new TempStore();
        var clock = new FakeClock();
        using var store = new HistoryStore(temp.Paths, clock);
        long id = AddText(store, "old", T0 - TimeSpan.FromHours(1));

        store.Touch(id);
        store.Touch(999);

        Assert.Equal(T0, store.Get(id)!.Summary.LastCopiedAt);
    }

    [Fact]
    public void Delete_Missing_DoesNotThrow()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());

        store.Delete(999);
        store.Rename(999, "x");
        store.SetPinned(999, true);

        Assert.Equal(0, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM entries"));
    }

    [Fact]
    public void Changed_RaisedOnlyWhenSomethingChanged()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddText(store, "x", T0);
        int raised = 0;
        store.Changed += () => raised++;

        store.Delete(999);
        store.Touch(999);
        store.Rename(999, "x");
        store.SetPinned(999, true);
        store.SetPinned(id, false);
        store.Rename(id, "   ");
        store.DeleteSince(T0 + TimeSpan.FromDays(1));
        store.Expire(T0 - TimeSpan.FromDays(1));
        Assert.Equal(0, raised);

        store.SetPinned(id, true);
        Assert.Equal(1, raised);
        store.Delete(id);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Rename_TrimsNames_AndBlankOrNullClearsThem()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddText(store, "body", T0);

        store.Rename(id, "  padded  ");
        Assert.Equal("padded", store.Get(id)!.Summary.Name);

        store.Rename(id, "   ");
        Assert.Null(store.Get(id)!.Summary.Name);

        store.Rename(id, "named");
        store.Rename(id, null);
        Assert.Null(store.Get(id)!.Summary.Name);
    }

    [Fact]
    public void EditText_ReplacesBodyKindHashAndCounts_AndDropsFormatsAndOldSearch()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = store.Upsert(new CapturedEntry(EntryKind.Text, "abc",
            new[] { new ClipFormat(FormatNames.Html, Encoding.UTF8.GetBytes("<b>abc</b>")) },
            ContentHasher.ForText("abc"), "app.exe", T0)).Id;

        long kept = store.EditText(id, "https://a.com");

        Assert.Equal(id, kept);
        EntryDetail detail = Get(store, id);
        Assert.Equal(EntryKind.Link, detail.Summary.Kind);
        Assert.Equal("https://a.com", detail.Body);
        Assert.Equal("https://a.com", detail.Summary.Preview);
        Assert.Equal(13, detail.Summary.CharCount);
        Assert.Equal(1, detail.Summary.LineCount);
        Assert.Equal(13L, detail.Summary.ByteSize);
        Assert.Empty(detail.Formats);
        Assert.Equal(ImageState.None, detail.Summary.ImageState);
        Assert.Empty(store.Query(new HistoryQuery("abc", null, 0, 10)));
        Assert.Single(store.Query(new HistoryQuery("a.com", null, 0, 10)));
        // The hash is the dedupe key, so copying the same link again must land on the edited row.
        Assert.Equal((id, false), store.Upsert(new CapturedEntry(EntryKind.Link, "https://a.com", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("https://a.com"), null, T0)));
    }

    [Fact]
    public void EditText_IntoExistingItem_MergesRows_KeepsEditedId_AndTakesNewerTimeAndPin()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long other = AddText(store, "target", T0);
        store.Rename(other, "theirs");
        store.SetPinned(other, true);
        long edited = AddText(store, "source", T0 - TimeSpan.FromMinutes(1));

        long kept = store.EditText(edited, "target");

        Assert.Equal(edited, kept);
        Assert.False(store.Exists(other));
        Assert.Equal(1, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM entries"));
        EntrySummary summary = Get(store, edited).Summary;
        Assert.Equal("theirs", summary.Name);
        Assert.True(summary.Pinned);
        Assert.Equal(T0, summary.LastCopiedAt);
    }

    [Fact]
    public void EditText_IntoExistingItem_KeepsEditedName()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long other = AddText(store, "target", T0);
        store.Rename(other, "theirs");
        long edited = AddText(store, "source", T0);
        store.Rename(edited, "mine");

        store.EditText(edited, "target");

        Assert.Equal("mine", Get(store, edited).Summary.Name);
    }

    [Fact]
    public void EditText_UnknownId_ThrowsKeyNotFound()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());

        Assert.Throws<KeyNotFoundException>(() => store.EditText(999, "text"));
    }

    [Fact]
    public void EditText_EmptyText_ThrowsArgumentException()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddText(store, "body", T0);

        Assert.Throws<ArgumentException>(() => store.EditText(id, ""));
    }

    [Fact]
    public void EditText_OfImageOrFiles_ThrowsInvalidOperation()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] dib = { 1 };
        long image = store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, dib) },
            ContentHasher.ForBytes(dib), null, T0)).Id;
        string files = @"C:\a\One.txt";
        long fileEntry = store.Upsert(new CapturedEntry(EntryKind.Files, files, Array.Empty<ClipFormat>(),
            ContentHasher.ForFiles(new[] { files }), null, T0)).Id;

        Assert.Throws<InvalidOperationException>(() => store.EditText(image, "text"));
        Assert.Throws<InvalidOperationException>(() => store.EditText(fileEntry, "text"));
    }

    [Fact]
    public void TotalBytes_IsSumOfByteSizes()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        Assert.Equal(0, store.TotalBytes());

        AddText(store, new string('a', 10), T0);
        AddText(store, new string('b', 20), T0);

        Assert.Equal(30, store.TotalBytes());
    }

    [Fact]
    public void SweepFiles_InFilesFolder_DeletesOldUnreferenced_KeepsReferencedAndRecent()
    {
        using var temp = new TempStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var store = new HistoryStore(temp.Paths, new FakeClock { UtcNow = now });
        long id = store.Upsert(new CapturedEntry(EntryKind.Text, "with html",
            new[] { new ClipFormat(FormatNames.Html, Encoding.UTF8.GetBytes("<b>ref</b>")) },
            ContentHasher.ForText("with html"), null, now)).Id;
        string referenced = Get(store, id).Formats.Single().FilePath;
        File.SetLastWriteTimeUtc(referenced, now.AddHours(-2).UtcDateTime);
        string stale = store.Blobs.FullPath(WriteBlob(store, new byte[] { 1 }, now.AddHours(-2)));
        string fresh = store.Blobs.FullPath(WriteBlob(store, new byte[] { 2 }, now.AddMinutes(-10)));
        int raised = 0;
        store.Changed += () => raised++;

        int removed = store.SweepFiles(TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void SweepFiles_InThumbsFolder_DeletesOnlyUnreferencedOldThumbs()
    {
        using var temp = new TempStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var store = new HistoryStore(temp.Paths, new FakeClock { UtcNow = now });
        byte[] dib = { 4, 4 };
        long id = store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, dib) },
            ContentHasher.ForBytes(dib), null, now)).Id;
        string referenced = Path.Combine(temp.Paths.Thumbs, "thumb-ref.png");
        string stale = Path.Combine(temp.Paths.Thumbs, "thumb-stale.png");
        File.WriteAllBytes(referenced, new byte[] { 1 });
        File.WriteAllBytes(stale, new byte[] { 2 });
        File.SetLastWriteTimeUtc(referenced, now.AddHours(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(stale, now.AddHours(-2).UtcDateTime);
        store.CompleteImage(id, 1, 1, null, "thumb-ref.png", null, null);

        int removed = store.SweepFiles(TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void SweepFiles_TmpFolder_DeletesOldFilesAndKeepsRecentOnes()
    {
        using var temp = new TempStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var store = new HistoryStore(temp.Paths, new FakeClock { UtcNow = now });
        string old = Path.Combine(temp.Paths.Temp, "old.part");
        string recent = Path.Combine(temp.Paths.Temp, "recent.part");
        File.WriteAllBytes(old, new byte[] { 1 });
        File.WriteAllBytes(recent, new byte[] { 2 });
        File.SetLastWriteTimeUtc(old, now.AddHours(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, now.AddMinutes(-10).UtcDateTime);

        int removed = store.SweepFiles(TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void SweepFiles_SharedBlob_SurvivesDeletingOneOwner()
    {
        using var temp = new TempStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var store = new HistoryStore(temp.Paths, new FakeClock { UtcNow = now });
        byte[] html = Encoding.UTF8.GetBytes("<i>shared</i>");
        long one = store.Upsert(new CapturedEntry(EntryKind.Text, "one", new[] { new ClipFormat(FormatNames.Html, html) },
            ContentHasher.ForText("one"), null, now)).Id;
        long two = store.Upsert(new CapturedEntry(EntryKind.Text, "two", new[] { new ClipFormat(FormatNames.Html, html) },
            ContentHasher.ForText("two"), null, now)).Id;
        string file = Get(store, one).Formats.Single().FilePath;
        Assert.Equal(file, Get(store, two).Formats.Single().FilePath);
        File.SetLastWriteTimeUtc(file, now.AddHours(-2).UtcDateTime);

        store.Delete(one);
        Assert.Equal(0, store.SweepFiles(TimeSpan.FromHours(1)));
        Assert.True(File.Exists(file));

        store.Delete(two);
        Assert.Equal(1, store.SweepFiles(TimeSpan.FromHours(1)));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void SweepFiles_BlobResavedAfterGoingStale_IsKept()
    {
        using var temp = new TempStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var store = new HistoryStore(temp.Paths, new FakeClock { UtcNow = now });
        byte[] html = Encoding.UTF8.GetBytes("<u>resaved</u>");
        string name = store.Blobs.Save(html, "html");
        File.SetLastWriteTimeUtc(store.Blobs.FullPath(name), now.AddHours(-2).UtcDateTime);

        // A new row is about to reference this blob; saving it again must make it young before any sweep sees it unreferenced.
        store.Blobs.Save(html, "html");

        Assert.Equal(0, store.SweepFiles(TimeSpan.FromHours(1)));
        Assert.True(File.Exists(store.Blobs.FullPath(name)));
    }

    [Fact]
    public void EnforceTotalLimit_SubtractsEachDeletedRowsSize_NotAFixedAmount()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        // Deleting the 200-byte oldest row leaves 150 bytes, which is still over the limit, so one 50-byte row must go too.
        AddText(store, new string('a', 200), T0 - TimeSpan.FromMinutes(4));
        AddText(store, new string('b', 50), T0 - TimeSpan.FromMinutes(3));
        AddText(store, new string('c', 50), T0 - TimeSpan.FromMinutes(2));
        AddText(store, new string('d', 50), T0 - TimeSpan.FromMinutes(1));
        Assert.Equal(350, store.TotalBytes());

        int removed = store.EnforceTotalLimit(120);

        Assert.Equal(2, removed);
        Assert.Equal(100, store.TotalBytes());
    }

    private static string WriteBlob(HistoryStore store, byte[] data, DateTimeOffset writtenAt)
    {
        string name = store.Blobs.Save(data, "png");
        File.SetLastWriteTimeUtc(store.Blobs.FullPath(name), writtenAt.UtcDateTime);
        return name;
    }

    private static long AddText(HistoryStore store, string body, DateTimeOffset copiedAt) =>
        store.Upsert(new CapturedEntry(EntryKind.Text, body, Array.Empty<ClipFormat>(), ContentHasher.ForText(body),
            "app.exe", copiedAt)).Id;

    private static EntryDetail Get(HistoryStore store, long id) =>
        store.Get(id) ?? throw new InvalidOperationException("entry was not stored");
}
