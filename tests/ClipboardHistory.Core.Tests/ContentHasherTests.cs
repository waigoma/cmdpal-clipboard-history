using Waigoma.ClipboardHistory.Capture;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class ContentHasherTests
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void ForBytes_ReturnsLowercaseSha256Hex()
    {
        Assert.Equal(EmptySha256, ContentHasher.ForBytes(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ForText_TreatsCrLfCrAndLfAsEqual()
    {
        string lf = ContentHasher.ForText("a\nb");

        Assert.Equal(lf, ContentHasher.ForText("a\r\nb"));
        Assert.Equal(lf, ContentHasher.ForText("a\rb"));
    }

    [Fact]
    public void ForText_DistinguishesLeadingWhitespace()
    {
        Assert.NotEqual(ContentHasher.ForText("a"), ContentHasher.ForText(" a"));
    }

    [Fact]
    public void ForText_HashesLoneSurrogatesWithoutThrowing()
    {
        string high = ContentHasher.ForText("\ud800");
        string other = ContentHasher.ForText("\ud801");

        Assert.Matches("^[0-9a-f]{64}$", high);
        Assert.NotEqual(high, other);
    }

    [Fact]
    public void ForText_HashesUtf16LittleEndianCodeUnits()
    {
        Assert.Equal(ContentHasher.ForBytes(new byte[] { 0x61, 0x00 }), ContentHasher.ForText("a"));
    }

    [Fact]
    public void ForText_OfEmptyString_EqualsHashOfNoBytes()
    {
        Assert.Equal(EmptySha256, ContentHasher.ForText(""));
    }

    [Fact]
    public void ForFiles_IgnoresPathCase()
    {
        Assert.Equal(ContentHasher.ForFiles(new[] { @"C:\A\X.txt" }), ContentHasher.ForFiles(new[] { @"c:\a\x.txt" }));
    }

    [Fact]
    public void ForFiles_DependsOnOrder()
    {
        Assert.NotEqual(ContentHasher.ForFiles(new[] { "a", "b" }), ContentHasher.ForFiles(new[] { "b", "a" }));
    }
}
