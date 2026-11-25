using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Store;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class HistoryStoreQueryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Search_PercentIsLiteral()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "100%", T0);
        AddText(store, "1000", T0);

        Assert.Equal(new[] { "100%" }, Previews(store, "0%"));
    }

    [Fact]
    public void Search_UnderscoreIsLiteral()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "a_b", T0);
        AddText(store, "axb", T0);

        Assert.Equal(new[] { "a_b" }, Previews(store, "a_b"));
    }

    [Fact]
    public void Search_BackslashIsLiteral()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, @"a\b", T0);
        AddText(store, "ab", T0);

        Assert.Equal(new[] { @"a\b" }, Previews(store, @"\"));
    }

    [Fact]
    public void Search_SplitsTermsAndRequiresAll_InAnyOrder()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "git main", T0);
        AddText(store, "main git", T0);
        AddText(store, "git only", T0);

        Assert.Equal(new[] { "git main", "main git" }, Previews(store, "git main"));
    }

    [Fact]
    public void Search_FullWidthAsciiMatchesHalfWidth()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "abc", T0);
        AddText(store, "xyz", T0);

        Assert.Equal(new[] { "abc" }, Previews(store, "ＡＢＣ"));
    }

    [Fact]
    public void Search_DashVariantsMatch()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "データ", T0);

        Assert.Equal(new[] { "データ" }, Previews(store, "デ-タ"));
    }

    [Fact]
    public void Search_WhitespaceOnly_ReturnsEverything()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "one", T0);
        AddText(store, "two", T0);
        AddText(store, "three", T0);

        Assert.Equal(3, store.Query(new HistoryQuery("   　 ", null, 0, 100)).Count);
    }

    [Fact]
    public void Search_MatchesName_AndRenameReplacesOldName()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddText(store, "body text", T0);

        store.Rename(id, "Invoice");
        Assert.Single(store.Query(new HistoryQuery("invoice", null, 0, 10)));

        store.Rename(id, "Other");
        Assert.Empty(store.Query(new HistoryQuery("invoice", null, 0, 10)));
        Assert.Single(store.Query(new HistoryQuery("other", null, 0, 10)));
    }

    [Fact]
    public void Search_MatchesFileNames_OfFilesEntry()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        string body = @"C:\docs\Quarterly-Report.xlsx" + "\n" + @"C:\docs\notes.txt";
        store.Upsert(new CapturedEntry(EntryKind.Files, body, Array.Empty<ClipFormat>(),
            ContentHasher.ForFiles(body.Split('\n')), null, T0));

        IReadOnlyList<EntrySummary> found = store.Query(new HistoryQuery("quarterly", null, 0, 10));

        EntrySummary summary = Assert.Single(found);
        Assert.Equal(EntryKind.Files, summary.Kind);
    }

    [Fact]
    public void Search_MatchesOcrAndQr_AndFlagsReportThem()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] dib = { 1, 2, 3 };
        long id = store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, dib) },
            ContentHasher.ForBytes(dib), null, T0)).Id;
        store.CompleteImage(id, 1, 1, null, null, "領収書", "https://qr.example/x");

        EntrySummary byOcr = Assert.Single(store.Query(new HistoryQuery("領収書", null, 0, 10)));
        Assert.Equal(id, byOcr.Id);
        Assert.Single(store.Query(new HistoryQuery("qr.example", null, 0, 10)));
        Assert.True(byOcr.HasOcr);
        Assert.True(byOcr.HasQr);
    }

    [Fact]
    public void Query_KindFilter_NullMeansAll()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        AddText(store, "hello", T0);
        store.Upsert(new CapturedEntry(EntryKind.Link, "https://a.com", Array.Empty<ClipFormat>(),
            ContentHasher.ForText("https://a.com"), null, T0));

        Assert.Equal(new[] { "https://a.com" }, Previews(store, "", EntryKind.Link));
        Assert.Equal(2, store.Query(new HistoryQuery("", null, 0, 100)).Count);
    }

    [Fact]
    public void Query_OffsetAndLimit_SplitFiftyOneIntoFiftyAndOne()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        for (int i = 0; i <= 50; i++)
        {
            AddText(store, $"item-{i:00}", T0 + TimeSpan.FromMinutes(i));
        }

        IReadOnlyList<EntrySummary> first = store.Query(new HistoryQuery("", null, 0, 50));
        IReadOnlyList<EntrySummary> second = store.Query(new HistoryQuery("", null, 50, 50));

        Assert.Equal(50, first.Count);
        Assert.Single(second);
        Assert.Equal("item-50", first[0].Preview);
        Assert.Equal("item-00", second[0].Preview);
        Assert.Equal(51, first.Select(s => s.Id).Concat(second.Select(s => s.Id)).Distinct().Count());
    }

    [Fact]
    public void Query_OrdersPinnedFirst_ThenLastCopiedDesc_ThenIdDesc()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long a = AddText(store, "a", T0);
        AddText(store, "b", T0 + TimeSpan.FromMinutes(1));
        AddText(store, "c", T0 + TimeSpan.FromMinutes(1));
        store.SetPinned(a, true);

        Assert.Equal(new[] { "a", "c", "b" }, store.Query(new HistoryQuery("", null, 0, 10)).Select(s => s.Preview));
    }

    [Fact]
    public void IdsNewestFirst_IgnoresPins_AndRespectsLimit()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long a = AddText(store, "a", T0);
        long b = AddText(store, "b", T0 + TimeSpan.FromMinutes(1));
        store.SetPinned(a, true);

        Assert.Equal(new[] { b, a }, store.IdsNewestFirst(10));
        Assert.Equal(new[] { b }, store.IdsNewestFirst(1));
    }

    [Fact]
    public void Exists_ReportsPresence()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddText(store, "x", T0);

        Assert.True(store.Exists(id));
        Assert.False(store.Exists(999));
    }

    private static long AddText(HistoryStore store, string body, DateTimeOffset copiedAt) =>
        store.Upsert(new CapturedEntry(EntryKind.Text, body, Array.Empty<ClipFormat>(), ContentHasher.ForText(body),
            "app.exe", copiedAt)).Id;

    private static IReadOnlyList<string> Previews(HistoryStore store, string search, EntryKind? kind = null) =>
        store.Query(new HistoryQuery(search, kind, 0, 100)).Select(s => s.Preview).OrderBy(p => p, StringComparer.Ordinal).ToList();
}
