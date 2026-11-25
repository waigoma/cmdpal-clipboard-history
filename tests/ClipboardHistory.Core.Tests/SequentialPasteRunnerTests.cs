using Waigoma.ClipboardHistory.Paste;
using Waigoma.ClipboardHistory.Sequence;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class SequentialPasteRunnerTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void SentOutcome_AdvancesSoTheNextRunPastesTheNextEntry()
    {
        var h = new Harness(3, 2, 1);

        Assert.Null(h.Runner.Run());
        h.Complete(PasteOutcome.Sent);
        Assert.Null(h.Runner.Run());

        Assert.Equal(new[] { "entry-3", "entry-2" }, h.Attempts);
    }

    [Theory]
    [InlineData(PasteOutcome.FocusTimeout)]
    [InlineData(PasteOutcome.Elevated)]
    [InlineData(PasteOutcome.ModifiersHeld)]
    [InlineData(PasteOutcome.SendFailed)]
    public void OtherOutcomes_KeepTheSameEntryForTheNextRun(PasteOutcome outcome)
    {
        var h = new Harness(3, 2, 1);

        Assert.Null(h.Runner.Run());
        h.Complete(outcome);
        Assert.Null(h.Runner.Run());

        Assert.Equal(new[] { "entry-3", "entry-3" }, h.Attempts);
    }

    [Theory]
    [InlineData(PasteOutcome.Busy)]
    [InlineData(PasteOutcome.NoTarget)]
    [InlineData(PasteOutcome.ClipboardFailed)]
    public void ImmediateRefusal_IsReturnedAndDoesNotAdvance(PasteOutcome refusal)
    {
        var h = new Harness(3, 2, 1);
        h.BeginResult = refusal;

        Assert.Equal(refusal, h.Runner.Run());

        h.BeginResult = null;
        Assert.Null(h.Runner.Run());
        Assert.Equal(new[] { "entry-3", "entry-3" }, h.Attempts);
    }

    [Fact]
    public void EndOfList_ReturnsNullWithoutBeginning()
    {
        var h = new Harness(1);

        Assert.Null(h.Runner.Run());
        h.Complete(PasteOutcome.Sent);

        Assert.Null(h.Runner.Run());
        Assert.Equal(new[] { "entry-1" }, h.Attempts);
    }

    [Fact]
    public void EmptyHistory_ReturnsNullWithoutBeginning()
    {
        var h = new Harness();

        Assert.Null(h.Runner.Run());
        Assert.Empty(h.Attempts);
    }

    [Fact]
    public void DeletedEntry_IsSkippedAndTheNextOneIsPasted()
    {
        var h = new Harness(3, 2, 1);
        h.Deleted.Add(2);

        Assert.Null(h.Runner.Run());
        h.Complete(PasteOutcome.Sent);
        Assert.Null(h.Runner.Run());

        Assert.Equal(new[] { "entry-3", "entry-1" }, h.Attempts);
    }

    [Fact]
    public void UnreadableEntry_IsSkippedAndTheNextOneIsPasted()
    {
        var h = new Harness(3, 2, 1);
        h.Unreadable.Add(2);

        Assert.Null(h.Runner.Run());
        h.Complete(PasteOutcome.Sent);
        Assert.Null(h.Runner.Run());

        Assert.Equal(new[] { "entry-3", "entry-1" }, h.Attempts);
    }

    [Fact]
    public void UnbuildableEntry_IsSkippedAndTheNextOneIsPasted()
    {
        var h = new Harness(3, 2, 1);
        h.Unbuildable.Add(2);

        Assert.Null(h.Runner.Run());
        h.Complete(PasteOutcome.Sent);
        Assert.Null(h.Runner.Run());

        Assert.Equal(new[] { "entry-3", "entry-1" }, h.Attempts);
    }

    [Fact]
    public void BeginThrowing_ReturnsSendFailedAndDoesNotAdvance()
    {
        var h = new Harness(3, 2, 1);
        h.BeginThrows = true;

        Assert.Equal(PasteOutcome.SendFailed, h.Runner.Run());

        h.BeginThrows = false;
        Assert.Null(h.Runner.Run());
        Assert.Equal(new[] { "entry-3", "entry-3" }, h.Attempts);
    }

    [Fact]
    public void ResetBetweenBeginAndDone_DoneIsHarmless_AndTheNextRunStartsFromTheNewestEntry()
    {
        var h = new Harness(3, 2, 1);

        Assert.Null(h.Runner.Run());
        h.Sequence.Reset();
        h.Complete(PasteOutcome.Sent);

        // A new capture put entry 4 on top, so the next run must start from it, not from the position of the old run.
        h.Ids.Insert(0, 4);
        Assert.Null(h.Runner.Run());

        Assert.Equal(new[] { "entry-3", "entry-4" }, h.Attempts);
    }

    [Fact]
    public void NewlineSetting_IsPassedToTheBuilder()
    {
        var h = new Harness(1);
        h.Newline = false;

        Assert.Null(h.Runner.Run());

        Assert.False(h.LastAppendNewline);
    }

    [Fact]
    public void SecondRunBeforeDone_ReturnsBusy_AndBeginIsCalledOnce()
    {
        var h = new Harness(3, 2, 1);

        Assert.Null(h.Runner.Run());
        Assert.Equal(PasteOutcome.Busy, h.Runner.Run());

        Assert.Single(h.Attempts);
    }

    [Fact]
    public void AfterDone_RunWorksAgain()
    {
        var h = new Harness(3, 2, 1);

        Assert.Null(h.Runner.Run());
        Assert.Equal(PasteOutcome.Busy, h.Runner.Run());
        h.Complete(PasteOutcome.FocusTimeout);

        Assert.Null(h.Runner.Run());
        Assert.Equal(new[] { "entry-3", "entry-3" }, h.Attempts);
    }

    [Fact]
    public void NothingToBegin_ClearsTheInFlightFlag()
    {
        var h = new Harness();

        Assert.Null(h.Runner.Run());
        h.Ids.Add(1);

        Assert.Null(h.Runner.Run());
        Assert.Equal(new[] { "entry-1" }, h.Attempts);
    }

    private sealed class Harness
    {
        private Action<PasteOutcome>? _pending;

        public Harness(params long[] ids)
        {
            Ids = new List<long>(ids);
            Sequence = new SequentialPaste(new FakeClock(), () => OneMinute);
            Runner = new SequentialPasteRunner(
                Sequence,
                () => Ids.ToArray(),
                id => !Deleted.Contains(id),
                id => Unreadable.Contains(id) ? null : Detail(id),
                (entry, appendNewline) =>
                {
                    LastAppendNewline = appendNewline;
                    return Unbuildable.Contains(entry.Summary.Id) ? null : new ClipboardPayload { Text = entry.Body };
                },
                Begin,
                () => Newline);
        }

        public List<long> Ids { get; }

        public HashSet<long> Deleted { get; } = new();

        public HashSet<long> Unreadable { get; } = new();

        public HashSet<long> Unbuildable { get; } = new();

        public SequentialPaste Sequence { get; }

        public SequentialPasteRunner Runner { get; }

        public PasteOutcome? BeginResult { get; set; }

        public bool BeginThrows { get; set; }

        public bool Newline { get; set; } = true;

        public bool LastAppendNewline { get; private set; }

        // The text of each payload handed to begin, in order. Entry bodies are fixed strings, so no user content is involved.
        public List<string> Attempts { get; } = new();

        public void Complete(PasteOutcome outcome)
        {
            Action<PasteOutcome>? done = _pending;
            Assert.NotNull(done);
            _pending = null;
            done(outcome);
        }

        private PasteOutcome? Begin(ClipboardPayload payload, Action<PasteOutcome> done)
        {
            Attempts.Add(payload.Text ?? string.Empty);
            if (BeginThrows)
            {
                throw new InvalidOperationException("begin failed");
            }

            if (BeginResult is { } immediate)
            {
                return immediate;
            }

            _pending = done;
            return null;
        }

        private static EntryDetail Detail(long id)
        {
            string body = "entry-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var summary = new EntrySummary(
                id, EntryKind.Text, body, null, null, false,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                0, 0, 0, body.Length, 1, ImageState.None, false, false, null);
            return new EntryDetail(summary, body, Array.Empty<StoredFormat>(), null, null);
        }
    }
}

public sealed class SequentialPayloadTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    // Every test writes into its own folder so none of them can touch the user's data.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardHistoryTests", "seqpayload-" + Guid.NewGuid().ToString("N"));

    public SequentialPayloadTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Cleanup is best effort, as in TempStore.
        }
    }

    [Theory]
    [InlineData(EntryKind.Text, "hello")]
    [InlineData(EntryKind.Link, "https://example.com")]
    [InlineData(EntryKind.Email, "a@example.com")]
    [InlineData(EntryKind.Color, "#3A7BD5")]
    public void TextKinds_WithNewline_CarryOnlyTextWithTrailingCrLf(EntryKind kind, string body)
    {
        string html = WriteFile("html.bin", "<b>x</b>");
        string rtf = WriteFile("rtf.bin", "{\\rtf1 x}");
        EntryDetail entry = Detail(kind, body, Stored(FormatNames.Html, html), Stored(FormatNames.Rtf, rtf));

        ClipboardPayload? payload = SequentialPayload.Build(entry, appendNewline: true, new NoDib());

        Assert.NotNull(payload);
        Assert.Equal(body + "\r\n", payload.Text);
        Assert.Null(payload.Html);
        Assert.Null(payload.Rtf);
        Assert.Null(payload.PngPath);
        Assert.Null(payload.DibPath);
        Assert.Null(payload.Files);
    }

    [Fact]
    public void TextKind_WithoutNewline_KeepsAllStoredFormats()
    {
        byte[] html = [1, 2, 3];
        byte[] rtf = [4, 5, 6];
        string htmlPath = WriteFile("html.bin", html);
        string rtfPath = WriteFile("rtf.bin", rtf);
        EntryDetail entry = Detail(EntryKind.Text, "hello", Stored(FormatNames.Html, htmlPath), Stored(FormatNames.Rtf, rtfPath));

        ClipboardPayload? payload = SequentialPayload.Build(entry, appendNewline: false, new NoDib());

        Assert.NotNull(payload);
        Assert.Equal("hello", payload.Text);
        Assert.Equal(html, payload.Html);
        Assert.Equal(rtf, payload.Rtf);
    }

    [Fact]
    public void ImageKind_WithNewline_IsBuiltLikeFullPaste()
    {
        string png = WriteFile("shot.png", [9, 9, 9]);
        EntryDetail entry = Detail(EntryKind.Image, null, Stored(FormatNames.Png, png));
        var dib = new NoDib();

        ClipboardPayload? payload = SequentialPayload.Build(entry, appendNewline: true, dib);

        Assert.Equal(PayloadFactory.Full(entry, dib), payload);
        Assert.Null(payload!.Text);
        Assert.Equal(png, payload.PngPath);
    }

    [Fact]
    public void FilesKind_WithNewline_IsBuiltLikeFullPaste()
    {
        string file = WriteFile("notes.txt", [1]);
        EntryDetail entry = Detail(EntryKind.Files, file);
        var dib = new NoDib();

        ClipboardPayload? payload = SequentialPayload.Build(entry, appendNewline: true, dib);

        Assert.NotNull(payload);
        Assert.Null(payload.Text);
        Assert.Equal(new[] { file }, payload.Files);
    }

    [Fact]
    public void TextKind_WithoutBody_IsNull()
    {
        EntryDetail entry = Detail(EntryKind.Text, null);

        Assert.Null(SequentialPayload.Build(entry, appendNewline: true, new NoDib()));
    }

    private string WriteFile(string name, string content) => WriteFile(name, System.Text.Encoding.UTF8.GetBytes(content));

    private string WriteFile(string name, byte[] content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static StoredFormat Stored(string name, string path) => new(name, path, new FileInfo(path).Length);

    private static EntryDetail Detail(EntryKind kind, string? body, params StoredFormat[] formats)
    {
        var summary = new EntrySummary(
            1, kind, body ?? string.Empty, null, null, false, T0, T0,
            0, 0, 0, body?.Length ?? 0, 1, ImageState.None, false, false, null);
        return new EntryDetail(summary, body, formats, null, null);
    }

    private sealed class NoDib : IDibProvider
    {
        public string? TryCreate(string pngPath) => null;
    }
}
