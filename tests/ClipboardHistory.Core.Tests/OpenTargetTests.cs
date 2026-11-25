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
    [InlineData("https://example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("http://example.com/a", "http://example.com/a")]
    [InlineData("  https://example.com  ", "https://example.com/")]
    public void Link_HttpAndHttps_AreOpenedAsAbsoluteUri(string body, string expected)
    {
        Assert.Equal(expected, OpenTarget.For(Detail(EntryKind.Link, body)));
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
    [InlineData("a?subject=x@example.com")]
    [InlineData("a&cc=b@example.com")]
    [InlineData("a%20b@example.com")]
    [InlineData("a;b@example.com")]
    [InlineData("a,b@example.com")]
    [InlineData("a<b@example.com")]
    [InlineData("a>b@example.com")]
    [InlineData("a\"b@example.com")]
    [InlineData("a\\b@example.com")]
    [InlineData("a b@example.com")]
    [InlineData("a\tb@example.com")]
    public void Email_WithReservedCharacters_IsNotOpened(string body)
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

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("A.PDF")]
    [InlineData("photo.JPEG")]
    [InlineData("sheet.xlsx")]
    [InlineData("archive.zip")]
    [InlineData("readme.md")]
    public void Files_AllowedExtension_IsOpened(string name)
    {
        string path = WriteFile(name);

        Assert.Equal(path, OpenTarget.For(Detail(EntryKind.Files, path)));
        Assert.False(OpenTarget.IsRefusedFile(Detail(EntryKind.Files, path)));
    }

    [Fact]
    public void Files_OpensTheFirstPath_WhenItIsAllowed()
    {
        string first = WriteFile("notes.txt");
        string second = WriteFile("other.exe");

        Assert.Equal(first, OpenTarget.For(Detail(EntryKind.Files, first + "\n" + second)));
    }

    [Fact]
    public void Files_Directory_IsOpened()
    {
        string dir = Path.Combine(_root, "folder");
        Directory.CreateDirectory(dir);

        Assert.Equal(dir, OpenTarget.For(Detail(EntryKind.Files, dir)));
        Assert.Equal(dir, OpenTarget.LocationFor(Detail(EntryKind.Files, dir)));
    }

    [Theory]
    [InlineData("a.exe")]
    [InlineData("A.EXE")]
    [InlineData("a.url")]
    [InlineData("a.lnk")]
    [InlineData("a.vbe")]
    [InlineData("a.msi")]
    [InlineData("a.ps1")]
    [InlineData("a.dll")]
    [InlineData("noextension")]
    public void Files_ExistingButDisallowedExtension_IsRefusedButLocated(string name)
    {
        string path = WriteFile(name);
        EntryDetail detail = Detail(EntryKind.Files, path);

        Assert.Null(OpenTarget.For(detail));
        Assert.True(OpenTarget.IsRefusedFile(detail));
        Assert.Equal(path, OpenTarget.LocationFor(detail));
    }

    [Theory]
    [InlineData("a.exe.")]
    [InlineData("a.exe ")]
    [InlineData("a.exe:s")]
    [InlineData("a.txt:stream")]
    [InlineData("//h/s/a.txt")]
    [InlineData(@"\\h\s\a.txt")]
    [InlineData(@"\\?\C:\a.txt")]
    [InlineData(@"C:\a\b:c.txt")]
    [InlineData(@"C:\a""b.txt")]
    public void MalformedPaths_AreNotWellFormed(string path)
    {
        Assert.False(OpenTarget.IsWellFormedPath(path));
    }

    [Theory]
    [InlineData(@"C:\a\b.txt")]
    [InlineData(@"D:\")]
    [InlineData("relative.txt")]
    public void WellFormedPaths_AreAccepted(string path)
    {
        Assert.True(OpenTarget.IsWellFormedPath(path));
    }

    [Theory]
    [InlineData("a.exe.")]
    [InlineData("a.exe ")]
    [InlineData("a.exe:s")]
    [InlineData("//h/s/a.txt")]
    [InlineData(@"\\h\s\a.txt")]
    public void MalformedFirstPath_IsNeitherOpenedNorLocated(string path)
    {
        EntryDetail detail = Detail(EntryKind.Files, path);

        Assert.Null(OpenTarget.For(detail));
        Assert.Null(OpenTarget.LocationFor(detail));
        Assert.False(OpenTarget.IsRefusedFile(detail));
    }

    [Fact]
    public void Files_MissingFirstPath_IsNullAndNotRefused()
    {
        EntryDetail detail = Detail(EntryKind.Files, Path.Combine(_root, "gone.txt"));

        Assert.Null(OpenTarget.For(detail));
        Assert.Null(OpenTarget.LocationFor(detail));
        Assert.False(OpenTarget.IsRefusedFile(detail));
    }

    [Fact]
    public void Files_TextEntry_IsNeverRefused()
    {
        string exe = WriteFile("tool.exe");

        Assert.False(OpenTarget.IsRefusedFile(Detail(EntryKind.Text, exe)));
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
