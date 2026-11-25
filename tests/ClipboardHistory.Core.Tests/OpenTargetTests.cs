using Waigoma.ClipboardHistory.Presentation;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class OpenTargetTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    // Every test writes into its own folder so none of them can touch the user's files.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardHistoryTests", "open-" + Guid.NewGuid().ToString("N"));

    public OpenTargetTests()
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
    [InlineData("https://example.com/path?q=1")]
    [InlineData("http://example.com")]
    [InlineData("  https://example.com  ")]
    public void Link_HttpAndHttps_AreOpened(string body)
    {
        string? target = OpenTarget.For(Detail(EntryKind.Link, body));

        Assert.Equal(body.Trim(), target);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("not a url")]
    public void Link_OtherSchemes_AreNotOpened(string body)
    {
        Assert.Null(OpenTarget.For(Detail(EntryKind.Link, body)));
    }

    [Theory]
    [InlineData("a@example.com", "mailto:a@example.com")]
    [InlineData("mailto:a@example.com", "mailto:a@example.com")]
    [InlineData("MAILTO:a@example.com", "mailto:a@example.com")]
    [InlineData("  a@example.com  ", "mailto:a@example.com")]
    public void Email_GetsMailtoPrefixOnce(string body, string expected)
    {
        Assert.Equal(expected, OpenTarget.For(Detail(EntryKind.Email, body)));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("mailto:")]
    public void Email_Blank_IsNotOpened(string body)
    {
        Assert.Null(OpenTarget.For(Detail(EntryKind.Email, body)));
    }

    [Theory]
    [InlineData(EntryKind.Text)]
    [InlineData(EntryKind.Color)]
    public void TextAndColor_AreNeverOpened(EntryKind kind)
    {
        Assert.Null(OpenTarget.For(Detail(kind, "https://example.com")));
    }

    [Fact]
    public void Image_OpensItsPng_WhenTheFileExists()
    {
        string png = WriteFile("image.png");
        EntryDetail detail = Detail(EntryKind.Image, null, Stored(FormatNames.Png, png));

        Assert.Equal(png, OpenTarget.For(detail));
    }

    [Fact]
    public void Image_MissingPng_IsNull()
    {
        string gone = Path.Combine(_root, "gone.png");
        EntryDetail detail = Detail(EntryKind.Image, null, Stored(FormatNames.Png, gone));

        Assert.Null(OpenTarget.For(detail));
    }

    [Fact]
    public void Image_WithoutPng_IsNull()
    {
        EntryDetail detail = Detail(EntryKind.Image, null, Stored(FormatNames.Dib, WriteFile("image.dib")));

        Assert.Null(OpenTarget.For(detail));
    }

    [Fact]
    public void Files_OpensTheFirstPath_WhenItIsNotExecutable()
    {
        string first = WriteFile("notes.txt");
        string second = WriteFile("other.txt");
        EntryDetail detail = Detail(EntryKind.Files, first + "\n" + second);

        Assert.Equal(first, OpenTarget.For(detail));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".EXE")]
    [InlineData(".com")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    [InlineData(".vbs")]
    [InlineData(".js")]
    [InlineData(".msi")]
    [InlineData(".scr")]
    [InlineData(".lnk")]
    [InlineData(".reg")]
    [InlineData(".hta")]
    [InlineData(".wsf")]
    [InlineData(".jar")]
    [InlineData(".pif")]
    [InlineData(".cpl")]
    [InlineData(".msc")]
    public void Files_ExecutableFirstPath_IsNotOpened(string extension)
    {
        string exe = WriteFile("tool" + extension);
        EntryDetail detail = Detail(EntryKind.Files, exe);

        Assert.True(OpenTarget.IsExecutable(exe));
        Assert.Null(OpenTarget.For(detail));
        Assert.Equal(exe, OpenTarget.LocationFor(detail));
    }

    [Fact]
    public void IsBlockedExecutable_OnlyForFileListsStartingWithAnExecutable()
    {
        string exe = WriteFile("tool.exe");
        string txt = WriteFile("notes.txt");

        Assert.True(OpenTarget.IsBlockedExecutable(Detail(EntryKind.Files, exe + "\n" + txt)));
        Assert.False(OpenTarget.IsBlockedExecutable(Detail(EntryKind.Files, txt + "\n" + exe)));
        Assert.False(OpenTarget.IsBlockedExecutable(Detail(EntryKind.Text, exe)));
    }

    [Fact]
    public void IsExecutable_OrdinaryFileNames_AreFalse()
    {
        Assert.False(OpenTarget.IsExecutable("notes.txt"));
        Assert.False(OpenTarget.IsExecutable("archive.exe.zip"));
        Assert.False(OpenTarget.IsExecutable("no-extension"));
    }

    [Fact]
    public void Files_MissingFirstPath_IsNull()
    {
        EntryDetail detail = Detail(EntryKind.Files, Path.Combine(_root, "gone.txt"));

        Assert.Null(OpenTarget.For(detail));
        Assert.Null(OpenTarget.LocationFor(detail));
    }

    [Fact]
    public void Files_UncPath_IsNotOpened()
    {
        EntryDetail detail = Detail(EntryKind.Files, @"\\server\share\file.txt");

        Assert.Null(OpenTarget.For(detail));
    }

    [Fact]
    public void LocationFor_Image_IsItsPng()
    {
        string png = WriteFile("shot.png");

        Assert.Equal(png, OpenTarget.LocationFor(Detail(EntryKind.Image, null, Stored(FormatNames.Png, png))));
    }

    [Theory]
    [InlineData(EntryKind.Text)]
    [InlineData(EntryKind.Link)]
    [InlineData(EntryKind.Email)]
    [InlineData(EntryKind.Color)]
    public void LocationFor_NonFileKinds_IsNull(EntryKind kind)
    {
        Assert.Null(OpenTarget.LocationFor(Detail(kind, "https://example.com")));
    }

    [Fact]
    public void LocationFor_PathWithDoubleQuote_IsNull()
    {
        // Windows file names cannot hold a quote, so such a path never exists; the check must still refuse it
        // before the explorer argument is built.
        EntryDetail detail = Detail(EntryKind.Files, Path.Combine(_root, "a\"b.txt"));

        Assert.Null(OpenTarget.LocationFor(detail));
    }

    private string WriteFile(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static StoredFormat Stored(string name, string path) => new(name, path, 3);

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
            ByteSize: 1,
            CharCount: 1,
            LineCount: 1,
            ImageState: kind == EntryKind.Image ? ImageState.Done : ImageState.None,
            HasOcr: false,
            HasQr: false,
            ThumbPath: null);
        return new EntryDetail(summary, body, formats, Ocr: null, Qr: null);
    }
}
