using System.Text;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Store;
using Waigoma.ClipboardHistory.Worker;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class WorkerRunnerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RunAll_ProcessesEveryQueuedImage_StoresSizeAndCleanedOcr_AndEmptiesJobs()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long first = AddDibImage(store, "one");
        long second = AddDibImage(store, "two");
        var processor = new FakeProcessor { Result = new ImageResult(640, 480, "日 本 語", "") };
        var runner = new WorkerRunner(store, temp.Paths, processor, () => true);

        int processed = runner.RunAll();

        Assert.Equal(2, processed);
        foreach (long id in new[] { first, second })
        {
            EntryDetail detail = Get(store, id);
            Assert.Equal(ImageState.Done, detail.Summary.ImageState);
            Assert.Equal(640, detail.Summary.Width);
            Assert.Equal(480, detail.Summary.Height);
            Assert.Equal("日本語", detail.Ocr);
        }

        Assert.False(store.Jobs.HasPending());
        Assert.Equal(0, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM jobs"));
        Assert.Empty(Directory.GetFiles(temp.Paths.Temp));
    }

    [Fact]
    public void RunAll_DibInput_IsReplacedByPngFormat_AndTheDibRowIsGone()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddDibImage(store, "one");
        var processor = new FakeProcessor();
        var runner = new WorkerRunner(store, temp.Paths, processor, () => true);

        runner.RunAll();

        EntryDetail detail = Get(store, id);
        Assert.Equal(new[] { FormatNames.Png }, detail.Formats.Select(f => f.Name));
        Assert.True(File.Exists(detail.Formats.Single().FilePath));
        Assert.Equal(0, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM formats WHERE name = '" + FormatNames.Dib + "'"));
        Assert.True(processor.Calls.Single().InputIsDib);
    }

    [Fact]
    public void RunAll_ThumbnailIsNamedAfterThePngStemWithSuffix()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddDibImage(store, "one");
        var runner = new WorkerRunner(store, temp.Paths, new FakeProcessor(), () => true);

        runner.RunAll();

        EntryDetail detail = Get(store, id);
        string pngName = Path.GetFileName(detail.Formats.Single().FilePath);
        string expected = Path.Combine(temp.Paths.Thumbs, Path.GetFileNameWithoutExtension(pngName) + "_t.png");
        Assert.Equal(expected, detail.Summary.ThumbPath);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void RunAll_FirstJobThrows_SecondIsStillProcessed()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long failing = AddDibImage(store, "one");
        long healthy = AddDibImage(store, "two");
        var processor = new FakeProcessor { ShouldFail = call => call == 0 };
        var runner = new WorkerRunner(store, temp.Paths, processor, () => true);

        int processed = runner.RunAll();

        Assert.Equal(2, processed);
        Assert.Equal(ImageState.Pending, Get(store, failing).Summary.ImageState);
        Assert.Equal(ImageState.Done, Get(store, healthy).Summary.ImageState);
        Assert.True(store.Jobs.HasPending());
    }

    [Fact]
    public void RunAll_ThreeFailedPasses_MarkTheEntryFailed_AndLaterPassesSkipIt()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddDibImage(store, "one");
        var processor = new FakeProcessor { ShouldFail = _ => true };
        var runner = new WorkerRunner(store, temp.Paths, processor, () => true);

        Assert.Equal(1, runner.RunAll());
        Assert.Equal(1, runner.RunAll());
        Assert.Equal(1, runner.RunAll());
        Assert.Equal(0, runner.RunAll());

        Assert.Equal(ImageState.Failed, Get(store, id).Summary.ImageState);
        Assert.Equal(2, RawDb.Scalar(temp.Paths, "SELECT state FROM jobs WHERE entry_id = " + id));
        Assert.Equal(3, processor.Calls.Count);
    }

    [Fact]
    public void RunAll_NonImageEntryCarryingPng_IsNotGivenOcrOrQr()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] png = Encoding.UTF8.GetBytes("office picture");
        long id = store.Upsert(new CapturedEntry(EntryKind.Text, "word", new[] { new ClipFormat(FormatNames.Png, png) },
            ContentHasher.ForBytes(png), null, T0)).Id;
        var processor = new FakeProcessor();
        var runner = new WorkerRunner(store, temp.Paths, processor, () => true);

        runner.RunAll();

        Assert.False(processor.Calls.Single().Ocr);
        Assert.False(processor.Calls.Single().Qr);
        Assert.Equal(ImageState.Done, Get(store, id).Summary.ImageState);
    }

    [Fact]
    public void RunAll_OcrDisabled_StoresNullOcr_AndEmptyQrWhenRunWithNoCode()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddDibImage(store, "one");
        var processor = new FakeProcessor { Result = new ImageResult(10, 10, null, "") };
        var runner = new WorkerRunner(store, temp.Paths, processor, () => false);

        runner.RunAll();

        EntryDetail detail = Get(store, id);
        Assert.Null(detail.Ocr);
        Assert.Equal("", detail.Qr);
        Assert.False(processor.Calls.Single().Ocr);
        Assert.True(processor.Calls.Single().Qr);
    }

    [Fact]
    public void RunAll_OcrRunWithNoText_StoresEmptyString()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddDibImage(store, "one");
        var processor = new FakeProcessor { Result = new ImageResult(10, 10, "", null) };
        var runner = new WorkerRunner(store, temp.Paths, processor, () => true);

        runner.RunAll();

        Assert.Equal("", Get(store, id).Ocr);
        Assert.False(Get(store, id).Summary.HasOcr);
    }

    [Fact]
    public void RunAll_PngInput_KeepsItsOriginalBlob_AndDiscardsTheReEncodedOutput()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        byte[] png = Encoding.UTF8.GetBytes("original png");
        long id = store.Upsert(new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Png, png) },
            ContentHasher.ForBytes(png), null, T0)).Id;
        string before = Get(store, id).Formats.Single().FilePath;
        var processor = new FakeProcessor();
        var runner = new WorkerRunner(store, temp.Paths, processor, () => true);

        runner.RunAll();

        EntryDetail detail = Get(store, id);
        Assert.Equal(before, detail.Formats.Single().FilePath);
        Assert.Equal(new[] { FormatNames.Png }, detail.Formats.Select(f => f.Name));
        Assert.False(processor.Calls.Single().InputIsDib);
        Assert.Single(Directory.GetFiles(temp.Paths.Files));
        Assert.Empty(Directory.GetFiles(temp.Paths.Temp));
    }

    [Fact]
    public void RunAll_EntryDeletedWhileProcessing_DoesNotThrow_AndCompletesTheJob()
    {
        using var temp = new TempStore();
        using var store = new HistoryStore(temp.Paths, new FakeClock());
        long id = AddDibImage(store, "one");
        var processor = new FakeProcessor { BeforeReturn = () => store.Delete(id) };
        var runner = new WorkerRunner(store, temp.Paths, processor, () => true);

        int processed = runner.RunAll();

        Assert.Equal(1, processed);
        Assert.False(store.Exists(id));
        Assert.False(store.Jobs.HasPending());
        Assert.Equal(0, RawDb.Scalar(temp.Paths, "SELECT COUNT(*) FROM jobs"));
    }

    private static long AddDibImage(HistoryStore store, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        var entry = new CapturedEntry(EntryKind.Image, null, new[] { new ClipFormat(FormatNames.Dib, bytes) },
            ContentHasher.ForBytes(bytes), null, T0);
        return store.Upsert(entry).Id;
    }

    private static EntryDetail Get(HistoryStore store, long id) =>
        store.Get(id) ?? throw new InvalidOperationException("entry was not stored");

    private sealed record Call(string InputPath, bool InputIsDib, bool Ocr, bool Qr);

    // Stands in for the WinRT processor so the runner's bookkeeping can be tested without image decoding.
    private sealed class FakeProcessor : IImageProcessor
    {
        public ImageResult Result { get; init; } = new(1, 1, null, null);

        public Func<int, bool> ShouldFail { get; init; } = _ => false;

        public Action? BeforeReturn { get; init; }

        public List<Call> Calls { get; } = new();

        public ImageResult Process(string inputPath, bool inputIsDib, string pngOutPath, string thumbOutPath, bool ocr, bool qr)
        {
            int call = Calls.Count;
            Calls.Add(new Call(inputPath, inputIsDib, ocr, qr));
            if (ShouldFail(call))
            {
                throw new InvalidOperationException("processor failed");
            }

            BeforeReturn?.Invoke();
            File.WriteAllBytes(pngOutPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            File.WriteAllBytes(thumbOutPath, new byte[] { 1 });
            return Result;
        }

        public void WriteDib(string pngPath, string dibOutPath) => throw new NotSupportedException();
    }
}
