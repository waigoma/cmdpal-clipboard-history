using System.Diagnostics;
using System.Text;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Store;
using Xunit;
using Xunit.Abstractions;

namespace Waigoma.ClipboardHistory.Tests;

// Spec 2.6: a 5,000-entry history must list and search within 200 ms (median). Run it in Release for the real figure.
[Trait("Category", "Performance")]
public sealed class SearchPerformanceTests
{
    private const int EntryCount = 5000;
    private const int ImageCount = 200;
    private const int Runs = 5;
    private const double BudgetMs = 200;
    private const long SeedBudgetMs = 60_000;

    // None of these words can join into "日本語", so the unmatched search really scans every row.
    private static readonly string[] Words =
    {
        "今日", "会議", "資料", "データ", "メモ", "作業", "確認", "の", "は", "です", "ます", "を",
        "project", "build", "error", "config", "value", "http", "item", "data", "test", "log", "x", " ",
    };

    private readonly ITestOutputHelper _output;

    public SearchPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Query_FirstPageAndUnmatchedSearch_MedianUnderBudget()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        var random = new Random(20261125);
        var seed = Stopwatch.StartNew();
        long totalBytes = Seed(store, random);
        seed.Stop();
        Assert.True(seed.ElapsedMilliseconds < SeedBudgetMs, $"seeding took {seed.ElapsedMilliseconds} ms");

        var firstPage = new double[Runs];
        var unmatched = new double[Runs];
        for (int run = 0; run < Runs; run++)
        {
            firstPage[run] = Time(() => store.Query(new HistoryQuery("", null, 0, 50)), out _);
            unmatched[run] = Time(() => store.Query(new HistoryQuery("日本語", null, 0, 50)), out IReadOnlyList<EntrySummary> none);
            Assert.Empty(none);
        }

        double firstPageMedian = Median(firstPage);
        double unmatchedMedian = Median(unmatched);
        _output.WriteLine($"seeded {EntryCount} entries ({ImageCount} images), avg {totalBytes / EntryCount} bytes, seed {seed.ElapsedMilliseconds} ms");
        _output.WriteLine($"first page median {firstPageMedian:F2} ms, runs {string.Join(", ", firstPage.Select(v => v.ToString("F2")))}");
        _output.WriteLine($"unmatched search median {unmatchedMedian:F2} ms, runs {string.Join(", ", unmatched.Select(v => v.ToString("F2")))}");
        Assert.True(firstPageMedian < BudgetMs, $"first page median {firstPageMedian:F2} ms");
        Assert.True(unmatchedMedian < BudgetMs, $"unmatched search median {unmatchedMedian:F2} ms");
    }

    // Returns the total UTF-8 bytes seeded, so the report can show the real average size.
    private static long Seed(HistoryStore store, Random random)
    {
        var start = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
        long bytes = 0;
        for (int i = 0; i < EntryCount; i++)
        {
            DateTimeOffset copied = start + TimeSpan.FromSeconds(i);
            if (i % (EntryCount / ImageCount) == 0)
            {
                byte[] png = { 0x89, 0x50, 0x4E, 0x47, (byte)(i >> 8), (byte)i };
                store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Png, png) },
                    ContentHasher.ForBytes(png), null, copied));
                bytes += png.Length;
                continue;
            }

            string body = MixedText(random, 2048, i);
            bytes += Encoding.UTF8.GetByteCount(body);
            store.Upsert(new CapturedEntry(EntryKind.Text, body, Array.Empty<ClipFormat>(), ContentHasher.ForText(body),
                "app.exe", copied));
        }

        return bytes;
    }

    // Roughly 2 KB of UTF-8 per entry, mixing Japanese and ASCII words; the index suffix keeps every hash distinct.
    private static string MixedText(Random random, int targetBytes, int index)
    {
        var text = new StringBuilder();
        text.Append("entry ").Append(index).Append(' ');
        int bytes = text.Length;
        while (bytes < targetBytes)
        {
            string word = Words[random.Next(Words.Length)];
            text.Append(word);
            bytes += Encoding.UTF8.GetByteCount(word);
        }

        return text.ToString();
    }

    private static double Time<T>(Func<T> action, out T result)
    {
        var watch = Stopwatch.StartNew();
        result = action();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Median(double[] values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }
}
