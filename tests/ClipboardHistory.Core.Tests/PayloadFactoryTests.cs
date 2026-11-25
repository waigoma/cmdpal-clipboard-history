using System.Text;
using Waigoma.ClipboardHistory.Paste;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class PayloadFactoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Full_TextEntry_CarriesTextHtmlAndRtf()
    {
        using var dir = new PasteTempDir();
        string html = dir.WriteText("html.bin", "<b>hi</b>");
        string rtf = dir.WriteText("rtf.bin", "{\\rtf1 hi}");
        EntryDetail entry = Detail(EntryKind.Text, "hi", Stored(FormatNames.Html, html), Stored(FormatNames.Rtf, rtf));

        ClipboardPayload? payload = PayloadFactory.Full(entry, new FakeDib());

        Assert.NotNull(payload);
        Assert.Equal("hi", payload.Text);
        Assert.Equal(File.ReadAllBytes(html), payload.Html);
        Assert.Equal(File.ReadAllBytes(rtf), payload.Rtf);
        Assert.Null(payload.Files);
        Assert.Null(payload.PngPath);
        Assert.Null(payload.DibPath);
    }

    [Fact]
    public void Full_TextEntryWithMissingHtmlBlob_KeepsTextOnly()
    {
        using var dir = new PasteTempDir();
        EntryDetail entry = Detail(EntryKind.Text, "hi", Stored(FormatNames.Html, dir.Missing("gone.bin")));

        ClipboardPayload? payload = PayloadFactory.Full(entry, new FakeDib());

        Assert.NotNull(payload);
        Assert.Equal("hi", payload.Text);
        Assert.Null(payload.Html);
    }

    [Fact]
    public void Full_TextEntryWithImage_IncludesPngOnlyNoDib()
    {
        using var dir = new PasteTempDir();
        string png = dir.WriteText("image.png", "png");
        var dib = new FakeDib { Result = "image.dib" };
        EntryDetail entry = Detail(EntryKind.Text, "caption", Stored(FormatNames.Png, png));

        ClipboardPayload? payload = PayloadFactory.Full(entry, dib);

        Assert.NotNull(payload);
        Assert.Equal("caption", payload.Text);
        Assert.Equal(png, payload.PngPath);
        Assert.Null(payload.DibPath);
        Assert.Equal(0, dib.Calls);
    }

    [Fact]
    public void Full_ImageWithPng_TakesDibPathFromProvider()
    {
        using var dir = new PasteTempDir();
        string png = dir.WriteText("image.png", "png");
        var dib = new FakeDib { Result = "image.dib" };
        EntryDetail entry = Detail(EntryKind.Image, null, Stored(FormatNames.Png, png));

        ClipboardPayload? payload = PayloadFactory.Full(entry, dib);

        Assert.NotNull(payload);
        Assert.Equal(png, payload.PngPath);
        Assert.Equal("image.dib", payload.DibPath);
        Assert.Equal(png, dib.LastPngPath);
    }

    [Fact]
    public void Full_ImageWithPngAndNoDibFromProvider_KeepsPngOnly()
    {
        using var dir = new PasteTempDir();
        string png = dir.WriteText("image.png", "png");
        EntryDetail entry = Detail(EntryKind.Image, null, Stored(FormatNames.Png, png));

        ClipboardPayload? payload = PayloadFactory.Full(entry, new FakeDib { Result = null });

        Assert.NotNull(payload);
        Assert.Equal(png, payload.PngPath);
        Assert.Null(payload.DibPath);
    }

    [Fact]
    public void Full_ImageWithDibOnly_IsDibPathOnly()
    {
        using var dir = new PasteTempDir();
        string dibV5 = dir.WriteText("image.dibv5", "dib");
        var dib = new FakeDib { Result = "unused.dib" };
        EntryDetail entry = Detail(EntryKind.Image, null, Stored(FormatNames.DibV5, dibV5));

        ClipboardPayload? payload = PayloadFactory.Full(entry, dib);

        Assert.NotNull(payload);
        Assert.Equal(dibV5, payload.DibPath);
        Assert.Null(payload.PngPath);
        Assert.Equal(0, dib.Calls);
    }

    [Fact]
    public void Full_ImageWhoseBlobsAreGone_IsNull()
    {
        using var dir = new PasteTempDir();
        EntryDetail entry = Detail(
            EntryKind.Image,
            null,
            Stored(FormatNames.Png, dir.Missing("gone.png")),
            Stored(FormatNames.Dib, dir.Missing("gone.dib")));

        Assert.Null(PayloadFactory.Full(entry, new FakeDib()));
    }

    [Fact]
    public void Full_FilesEntry_ListsOnePathPerLine()
    {
        EntryDetail entry = Detail(EntryKind.Files, "C:\\a.txt\nC:\\b.txt");

        ClipboardPayload? payload = PayloadFactory.Full(entry, new FakeDib());

        Assert.NotNull(payload);
        Assert.Equal(new[] { "C:\\a.txt", "C:\\b.txt" }, payload.Files);
    }

    [Fact]
    public void Full_EmptyEntry_IsNull()
    {
        Assert.Null(PayloadFactory.Full(Detail(EntryKind.Text, null), new FakeDib()));
    }

    [Fact]
    public void Plain_TextEntry_IsTextOnly()
    {
        using var dir = new PasteTempDir();
        string html = dir.WriteText("html.bin", "<b>hi</b>");
        EntryDetail entry = Detail(EntryKind.Text, "hi", Stored(FormatNames.Html, html));

        ClipboardPayload? payload = PayloadFactory.Plain(entry);

        Assert.NotNull(payload);
        Assert.Equal("hi", payload.Text);
        Assert.Null(payload.Html);
        Assert.Null(payload.Rtf);
    }

    [Fact]
    public void Plain_FilesEntry_IsNewlineSeparatedPathsAsText()
    {
        EntryDetail entry = Detail(EntryKind.Files, "C:\\a.txt\nC:\\b.txt");

        ClipboardPayload? payload = PayloadFactory.Plain(entry);

        Assert.NotNull(payload);
        Assert.Equal("C:\\a.txt\nC:\\b.txt", payload.Text);
        Assert.Null(payload.Files);
    }

    [Fact]
    public void Plain_ImageEntry_IsNull()
    {
        using var dir = new PasteTempDir();
        EntryDetail entry = Detail(EntryKind.Image, null, Stored(FormatNames.Png, dir.WriteText("image.png", "png")));

        Assert.Null(PayloadFactory.Plain(entry));
    }

    [Fact]
    public void Plain_EmptyText_IsNull()
    {
        Assert.Null(PayloadFactory.Plain(Detail(EntryKind.Text, string.Empty)));
    }

    [Fact]
    public void AvailableFormats_TextEntryWithAllData_ListsInDisplayOrder()
    {
        using var dir = new PasteTempDir();
        EntryDetail entry = Detail(
            EntryKind.Text,
            "hi",
            Stored(FormatNames.Rtf, dir.WriteText("rtf.bin", "rtf")),
            Stored(FormatNames.Html, dir.WriteText("html.bin", "html")),
            Stored(FormatNames.Png, dir.WriteText("image.png", "png")));

        IReadOnlyList<string> formats = PayloadFactory.AvailableFormats(entry);

        Assert.Equal(new[] { "Text", "HTML", "RTF", "画像" }, formats);
    }

    [Fact]
    public void AvailableFormats_FilesEntry_ListsFileAndPath()
    {
        EntryDetail entry = Detail(EntryKind.Files, "C:\\a.txt");

        Assert.Equal(new[] { "ファイル", "パス" }, PayloadFactory.AvailableFormats(entry));
    }

    [Fact]
    public void AvailableFormats_ImageWithDibOnly_ListsImage()
    {
        using var dir = new PasteTempDir();
        EntryDetail entry = Detail(EntryKind.Image, null, Stored(FormatNames.Dib, dir.WriteText("image.dib", "dib")));

        Assert.Equal(new[] { "画像" }, PayloadFactory.AvailableFormats(entry));
    }

    [Fact]
    public void AvailableFormats_BlobsGone_ListsNothing()
    {
        using var dir = new PasteTempDir();
        EntryDetail entry = Detail(EntryKind.Image, null, Stored(FormatNames.Png, dir.Missing("gone.png")));

        Assert.Empty(PayloadFactory.AvailableFormats(entry));
    }

    [Fact]
    public void Single_Html_CarriesOnlyTheHtmlBytes()
    {
        using var dir = new PasteTempDir();
        string html = dir.WriteText("html.bin", "<b>hi</b>");
        string rtf = dir.WriteText("rtf.bin", "{\\rtf1 hi}");
        EntryDetail entry = Detail(EntryKind.Text, "hi", Stored(FormatNames.Html, html), Stored(FormatNames.Rtf, rtf));

        ClipboardPayload? payload = PayloadFactory.Single(entry, "HTML", new FakeDib());

        Assert.NotNull(payload);
        Assert.Equal(File.ReadAllBytes(html), payload.Html);
        Assert.Null(payload.Text);
        Assert.Null(payload.Rtf);
    }

    [Fact]
    public void Single_PathFormatOfFilesEntry_IsTheJoinedPathsAsText()
    {
        EntryDetail entry = Detail(EntryKind.Files, "C:\\a.txt\nC:\\b.txt");

        ClipboardPayload? payload = PayloadFactory.Single(entry, "パス", new FakeDib());

        Assert.NotNull(payload);
        Assert.Equal("C:\\a.txt\nC:\\b.txt", payload.Text);
        Assert.Null(payload.Files);
    }

    [Fact]
    public void Single_FileFormatOfFilesEntry_IsTheFileList()
    {
        EntryDetail entry = Detail(EntryKind.Files, "C:\\a.txt\nC:\\b.txt");

        ClipboardPayload? payload = PayloadFactory.Single(entry, "ファイル", new FakeDib());

        Assert.NotNull(payload);
        Assert.Equal(new[] { "C:\\a.txt", "C:\\b.txt" }, payload.Files);
    }

    [Fact]
    public void Single_ImageFormat_UsesProviderDibForImageEntries()
    {
        using var dir = new PasteTempDir();
        string png = dir.WriteText("image.png", "png");
        var dib = new FakeDib { Result = "image.dib" };
        EntryDetail entry = Detail(EntryKind.Image, null, Stored(FormatNames.Png, png));

        ClipboardPayload? payload = PayloadFactory.Single(entry, "画像", dib);

        Assert.NotNull(payload);
        Assert.Equal(png, payload.PngPath);
        Assert.Equal("image.dib", payload.DibPath);
    }

    [Fact]
    public void Single_UnavailableOrUnknownFormat_IsNull()
    {
        EntryDetail text = Detail(EntryKind.Text, "hi");
        EntryDetail files = Detail(EntryKind.Files, "C:\\a.txt");

        Assert.Null(PayloadFactory.Single(text, "ファイル", new FakeDib()));
        Assert.Null(PayloadFactory.Single(files, "Text", new FakeDib()));
        Assert.Null(PayloadFactory.Single(text, "HTML", new FakeDib()));
        Assert.Null(PayloadFactory.Single(text, "unknown", new FakeDib()));
    }

    private static EntryDetail Detail(EntryKind kind, string? body, params StoredFormat[] formats)
    {
        var summary = new EntrySummary(
            Id: 1,
            Kind: kind,
            Preview: "preview",
            Name: null,
            SourceApp: null,
            Pinned: false,
            CreatedAt: T0,
            LastCopiedAt: T0,
            Width: 0,
            Height: 0,
            ByteSize: 0,
            CharCount: 0,
            LineCount: 0,
            ImageState: ImageState.None,
            HasOcr: false,
            HasQr: false,
            ThumbPath: null);
        return new EntryDetail(summary, body, formats, null, null);
    }

    private static StoredFormat Stored(string name, string path) => new(name, path, 0);

    private sealed class FakeDib : IDibProvider
    {
        public string? Result { get; set; }

        public int Calls { get; private set; }

        public string? LastPngPath { get; private set; }

        public string? TryCreate(string pngPath)
        {
            Calls++;
            LastPngPath = pngPath;
            return Result;
        }
    }

    // Each test gets its own folder under the temp directory, removed on dispose, so no real user data is touched.
    private sealed class PasteTempDir : IDisposable
    {
        public PasteTempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "ClipboardHistoryPasteTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string WriteText(string name, string content)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            return path;
        }

        public string Missing(string name) => Path.Combine(Root, name);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
