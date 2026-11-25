using System.Text;
using Waigoma.ClipboardHistory.Capture;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class CaptureBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly HistoryOptions Defaults = new();

    private static readonly HistoryOptions TenByteLimit = new() { MaxFormatBytes = 10 };

    [Fact]
    public void Build_PrefersFiles_OverText()
    {
        ClipboardSnapshot snapshot = Snapshot(text: "hello", files: new[] { @"C:\a.txt" });

        CapturedEntry? entry = CaptureBuilder.Build(snapshot, Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Files, entry.Kind);
    }

    [Fact]
    public void Build_FilesBody_JoinsOriginalPathsWithLf_AndHashesLowercased()
    {
        string[] files = { @"C:\A\One.txt", @"C:\b\Two.txt" };

        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(files: files), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(@"C:\A\One.txt" + "\n" + @"C:\b\Two.txt", entry.Body);
        Assert.Equal(ContentHasher.ForFiles(files), entry.Hash);
        Assert.Empty(entry.Formats);
    }

    [Fact]
    public void Build_TreatsEmptyFileList_AsNoFiles()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "hello", files: Array.Empty<string>()), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Text, entry.Kind);
    }

    [Fact]
    public void Build_KeepsPng_AlongsideText()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "hello", formats: new[] { Format(FormatNames.Png, 3) }), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Text, entry.Kind);
        Assert.Equal("hello", entry.Body);
        Assert.Equal(new[] { FormatNames.Png }, entry.Formats.Select(f => f.Name));
    }

    [Fact]
    public void Build_UsesImage_WhenTextIsEmpty()
    {
        byte[] png = { 1, 2, 3 };

        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "", formats: new[] { new ClipFormat(FormatNames.Png, png) }), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Image, entry.Kind);
        Assert.Null(entry.Body);
        Assert.Equal(ContentHasher.ForBytes(png), entry.Hash);
        Assert.Equal(new[] { FormatNames.Png }, entry.Formats.Select(f => f.Name));
    }

    [Fact]
    public void Build_RecordsSingleNewline()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "\n"), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Text, entry.Kind);
        Assert.Equal("\n", entry.Body);
    }

    [Fact]
    public void Build_ReturnsNull_WhenNothingIsCaptured()
    {
        Assert.Null(CaptureBuilder.Build(Snapshot(), Defaults, Now));
    }

    [Fact]
    public void Build_ReturnsNull_WhenOnlyHtmlIsPresent()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(formats: new[] { Format(FormatNames.Html, 3) }), Defaults, Now);

        Assert.Null(entry);
    }

    [Theory]
    [InlineData(FormatNames.Png, FormatNames.DibV5, FormatNames.Dib, FormatNames.Png)]
    [InlineData(FormatNames.Dib, FormatNames.DibV5, null, FormatNames.DibV5)]
    [InlineData(FormatNames.Dib, null, null, FormatNames.Dib)]
    public void Build_KeepsOnlyHighestPriorityImageFormat(string? first, string? second, string? third, string expected)
    {
        var formats = new List<ClipFormat>();
        foreach (string? name in new[] { first, second, third })
        {
            if (name is not null)
            {
                formats.Add(Format(name, 3));
            }
        }

        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(formats: formats), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Image, entry.Kind);
        Assert.Equal(new[] { expected }, entry.Formats.Select(f => f.Name));
    }

    [Fact]
    public void Build_KeepsHtmlAndRtf_ForText()
    {
        var formats = new[] { Format(FormatNames.Html, 3), Format(FormatNames.Rtf, 3), Format(FormatNames.Png, 3), Format(FormatNames.Dib, 3) };

        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "x", formats: formats), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(3, entry.Formats.Count);
        Assert.True(entry.Formats.Select(f => f.Name).ToHashSet().SetEquals(new[] { FormatNames.Png, FormatNames.Html, FormatNames.Rtf }));
    }

    [Fact]
    public void Build_DropsHtml_WhenItExceedsMaxFormatBytes_AndKeepsText()
    {
        var formats = new[] { Format(FormatNames.Html, 11) };

        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "x", formats: formats), TenByteLimit, Now);

        Assert.NotNull(entry);
        Assert.Equal("x", entry.Body);
        Assert.Empty(entry.Formats);
    }

    [Fact]
    public void Build_KeepsHtml_WhenItIsExactlyMaxFormatBytes()
    {
        var formats = new[] { Format(FormatNames.Html, 10) };

        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "x", formats: formats), TenByteLimit, Now);

        Assert.NotNull(entry);
        Assert.Equal(new[] { FormatNames.Html }, entry.Formats.Select(f => f.Name));
    }

    [Fact]
    public void Build_ReturnsNull_WhenTextUtf8ExceedsMaxFormatBytes()
    {
        Assert.Null(CaptureBuilder.Build(Snapshot(text: "abcdefghijk"), TenByteLimit, Now));
    }

    [Fact]
    public void Build_KeepsText_WhenUtf8IsExactlyMaxFormatBytes()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "abcdefghij"), TenByteLimit, Now);

        Assert.NotNull(entry);
        Assert.Equal("abcdefghij", entry.Body);
    }

    [Fact]
    public void Build_MeasuresTextAsUtf8Bytes_NotCharacters()
    {
        // Four characters but twelve UTF-8 bytes, so it exceeds a ten-byte limit.
        Assert.Null(CaptureBuilder.Build(Snapshot(text: "あいうえ"), TenByteLimit, Now));
    }

    [Fact]
    public void Build_ReturnsNull_WhenOnlyPngExceedsMaxFormatBytes()
    {
        Assert.Null(CaptureBuilder.Build(Snapshot(formats: new[] { Format(FormatNames.Png, 11) }), TenByteLimit, Now));
    }

    [Fact]
    public void Build_KeepsPng_WhenItIsExactlyMaxFormatBytes()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(formats: new[] { Format(FormatNames.Png, 10) }), TenByteLimit, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Image, entry.Kind);
    }

    [Fact]
    public void Build_FallsBackToText_WhenFilesBodyExceedsMaxFormatBytes()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "x", files: new[] { @"C:\long\path\file.txt" }), TenByteLimit, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Text, entry.Kind);
    }

    [Fact]
    public void Build_SourceAppIsFileNameOnly()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "x", sourceExePath: @"C:\Apps\Code.exe"), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal("Code.exe", entry.SourceApp);
    }

    [Fact]
    public void Build_SourceAppIsNull_WhenSourceUnknown()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "x"), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Null(entry.SourceApp);
    }

    [Fact]
    public void Build_SetsCopiedAtFromNow()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "x"), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(Now, entry.CopiedAt);
    }

    [Fact]
    public void Build_TextBodyIsOriginal_AndHashIsNormalized()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "a\r\nb"), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal("a\r\nb", entry.Body);
        Assert.Equal(ContentHasher.ForText("a\nb"), entry.Hash);
    }

    [Fact]
    public void Build_ClassifiesTextKind()
    {
        CapturedEntry? entry = CaptureBuilder.Build(Snapshot(text: "https://example.com/a"), Defaults, Now);

        Assert.NotNull(entry);
        Assert.Equal(EntryKind.Link, entry.Kind);
    }

    private static ClipboardSnapshot Snapshot(
        string? text = null,
        IReadOnlyList<string>? files = null,
        IReadOnlyList<ClipFormat>? formats = null,
        string? sourceExePath = null)
    {
        return new ClipboardSnapshot(1, sourceExePath, false, false, false, null, text, files,
            formats ?? Array.Empty<ClipFormat>());
    }

    private static ClipFormat Format(string name, int size) => new(name, new byte[size]);
}
