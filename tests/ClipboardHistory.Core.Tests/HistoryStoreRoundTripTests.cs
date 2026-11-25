using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Store;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class HistoryStoreRoundTripTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private const int FiveMegabytes = 5 * 1024 * 1024;

    [Fact]
    public void Upsert_LoneSurrogate_IsStoredWithoutThrowing()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string body = "a\uD800b";

        long id = AddText(store, body);

        // UTF-8 cannot carry an unpaired surrogate, so it reads back as the replacement character.
        Assert.Equal("a�b", store.Get(id)?.Body);
    }

    [Fact]
    public void Upsert_FiveMegabyteBody_RoundTripsExactly()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string body = new('x', FiveMegabytes);

        long id = AddText(store, body);

        EntryDetail? detail = store.Get(id);
        Assert.Equal(FiveMegabytes, detail?.Summary.ByteSize);
        Assert.Equal(body, detail?.Body);
    }

    [Fact]
    public void GetForDisplay_CutsBodyToTheLimit_AndKeepsTheFullSize()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddText(store, new string('y', FiveMegabytes));

        EntryDetail? display = store.GetForDisplay(id, 1000);

        Assert.NotNull(display);
        Assert.Equal(1000, display.Body?.Length);
        Assert.Equal(FiveMegabytes, display.Summary.ByteSize);
        Assert.Equal(FiveMegabytes, store.Get(id)?.Body?.Length);
    }

    [Fact]
    public void GetForDisplay_CutsOcrTextTheSameWay()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddText(store, "short");
        RawDb.Execute(temp.Paths, "UPDATE entries SET ocr = 'abcdef' WHERE id = " + id);

        EntryDetail? display = store.GetForDisplay(id, 3);

        Assert.Equal("abc", display?.Ocr);
        Assert.True(display!.Summary.HasOcr);
        Assert.Null(display?.Qr);
    }

    [Fact]
    public void GetForDisplay_MissingId_ReturnsNull()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());

        Assert.Null(store.GetForDisplay(999, 10));
    }

    private static long AddText(HistoryStore store, string body) =>
        store.Upsert(new CapturedEntry(EntryKind.Text, body, Array.Empty<ClipFormat>(), ContentHasher.ForText(body),
            "app.exe", T0)).Id;
}
