using System.Diagnostics;
using Waigoma.ClipboardHistory.Capture;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class ContentClassifierTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("\t https://example.com/a?b=1 \r\n")]
    [InlineData("http://localhost:3000")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void ClassifyText_ReturnsLink_ForAbsoluteHttpUrl(string text)
    {
        Assert.Equal(EntryKind.Link, ContentClassifier.ClassifyText(text));
    }

    [Theory]
    [InlineData("https://a.com https://b.com")]
    [InlineData("see https://a.com")]
    [InlineData("ftp://a.com")]
    [InlineData("https://")]
    [InlineData("javascript:alert(1)")]
    public void ClassifyText_ReturnsText_ForNonLinkUrlShapes(string text)
    {
        Assert.Equal(EntryKind.Text, ContentClassifier.ClassifyText(text));
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("mailto:user@example.com")]
    public void ClassifyText_ReturnsEmail_ForSingleAddress(string text)
    {
        Assert.Equal(EntryKind.Email, ContentClassifier.ClassifyText(text));
    }

    [Theory]
    [InlineData("user@")]
    [InlineData("a b@example.com")]
    [InlineData("user@example.com, b@example.com")]
    [InlineData("@user")]
    public void ClassifyText_ReturnsText_ForNonEmailShapes(string text)
    {
        Assert.Equal(EntryKind.Text, ContentClassifier.ClassifyText(text));
    }

    [Theory]
    [InlineData("#fff")]
    [InlineData("#3A7BD5")]
    [InlineData("#3A7BD580")]
    [InlineData("rgb(1, 2, 3)")]
    [InlineData("rgba(1,2,3,0.5)")]
    [InlineData("rgb(1 2 3 / 50%)")]
    [InlineData("hsl(120, 50%, 50%)")]
    [InlineData("hsla(120,50%,50%,0.3)")]
    public void ClassifyText_ReturnsColor_ForSupportedColorSyntax(string text)
    {
        Assert.Equal(EntryKind.Color, ContentClassifier.ClassifyText(text));
    }

    [Theory]
    [InlineData("#ff")]
    [InlineData("#12345")]
    [InlineData("#ggg")]
    [InlineData("rgb(256,0,0)")]
    [InlineData("rgb(1,2)")]
    [InlineData("hsl(120,50,50)")]
    public void ClassifyText_ReturnsText_ForInvalidColorSyntax(string text)
    {
        Assert.Equal(EntryKind.Text, ContentClassifier.ClassifyText(text));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("abc")]
    public void ClassifyText_ReturnsText_ForBlankOrPlainText(string text)
    {
        Assert.Equal(EntryKind.Text, ContentClassifier.ClassifyText(text));
    }

    [Fact]
    public void ClassifyText_ReturnsText_WithoutScanningLongInput()
    {
        string text = new('a', 1_000_000);
        ContentClassifier.ClassifyText("warm up");

        var watch = Stopwatch.StartNew();
        EntryKind kind = ContentClassifier.ClassifyText(text);
        watch.Stop();

        Assert.Equal(EntryKind.Text, kind);
        Assert.True(watch.Elapsed.TotalMilliseconds < 10, $"took {watch.Elapsed.TotalMilliseconds} ms");
    }
}

public sealed class ColorParserTests
{
    [Fact]
    public void TryParse_ExpandsThreeDigitHex()
    {
        Assert.True(ColorParser.TryParse("#f00", out Rgba color));
        Assert.Equal(new Rgba(255, 0, 0, 255), color);
    }

    [Fact]
    public void TryParse_ReadsEightDigitHexAlpha()
    {
        Assert.True(ColorParser.TryParse("#3A7BD580", out Rgba color));
        Assert.Equal(new Rgba(0x3A, 0x7B, 0xD5, 0x80), color);
    }

    [Fact]
    public void TryParse_RoundsFractionalAlphaAwayFromZero()
    {
        Assert.True(ColorParser.TryParse("rgba(1,2,3,0.5)", out Rgba color));
        Assert.Equal(new Rgba(1, 2, 3, 128), color);
    }

    [Fact]
    public void TryParse_ConvertsHslToRgb()
    {
        Assert.True(ColorParser.TryParse("hsl(120,100%,50%)", out Rgba color));
        Assert.Equal(new Rgba(0, 255, 0, 255), color);
    }

    [Fact]
    public void TryParse_IsCaseInsensitiveForFunctionNames()
    {
        Assert.True(ColorParser.TryParse("RGB(1, 2, 3)", out Rgba color));
        Assert.Equal(new Rgba(1, 2, 3, 255), color);
    }

    [Theory]
    [InlineData("#ff")]
    [InlineData("rgb(256,0,0)")]
    [InlineData("hsl(120,50,50)")]
    public void TryParse_ReturnsFalse_ForInvalidInput(string text)
    {
        Assert.False(ColorParser.TryParse(text, out _));
    }
}
