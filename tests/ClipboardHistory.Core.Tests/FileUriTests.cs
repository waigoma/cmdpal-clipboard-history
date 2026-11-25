using Waigoma.ClipboardHistory.Presentation;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class FileUriTests
{
    // The target must open the same file it was built from, so each case is checked by decoding it back to a path.
    [Theory]
    [InlineData(@"C:\pictures\plain.png")]
    [InlineData(@"C:\my pictures\a b.png")]
    [InlineData(@"C:\work\a#b.png")]
    [InlineData(@"C:\work\what?.png")]
    [InlineData(@"C:\work\100%.png")]
    [InlineData(@"C:\work\50%20off.png")]
    [InlineData(@"C:\work\x (1).png")]
    [InlineData(@"C:\work\[draft].png")]
    [InlineData(@"C:\画像\テスト 画像.png")]
    public void ToMarkdownTarget_OpensTheSameFileItWasBuiltFrom(string path)
    {
        string target = FileUri.ToMarkdownTarget(path);

        Assert.Equal(path, DecodeOnce(target));
    }

    // Decodes the target the way a file loader does: one pass of percent-decoding after the scheme.
    private static string DecodeOnce(string target)
    {
        Assert.StartsWith("file:///", target, StringComparison.Ordinal);
        return Uri.UnescapeDataString(target["file:///".Length..]).Replace('/', '\\');
    }

    [Fact]
    public void ToMarkdownTarget_EncodesSpacesAsPercentTwenty()
    {
        Assert.Equal("file:///C:/my%20pictures/a.png", FileUri.ToMarkdownTarget(@"C:\my pictures\a.png"));
    }

    [Fact]
    public void ToMarkdownTarget_EncodesAPercentSignSoAnExistingEscapeIsNotDecoded()
    {
        // Without this, "%20" in a file name would decode to a space and point at another file.
        Assert.Equal("file:///C:/work/50%2520off.png", FileUri.ToMarkdownTarget(@"C:\work\50%20off.png"));
    }

    [Fact]
    public void ToMarkdownTarget_EncodesParenthesesThatWouldEndTheMarkdownTarget()
    {
        string target = FileUri.ToMarkdownTarget(@"C:\work\x (1).png");

        Assert.DoesNotContain("(", target);
        Assert.DoesNotContain(")", target);
        Assert.Contains("%28", target);
        Assert.Contains("%29", target);
    }

    [Fact]
    public void ToMarkdownTarget_EncodesJapaneseAsUtf8()
    {
        Assert.Equal("file:///C:/%E7%94%BB%E5%83%8F/a.png", FileUri.ToMarkdownTarget(@"C:\画像\a.png"));
    }
}
