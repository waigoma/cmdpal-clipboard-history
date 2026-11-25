using Waigoma.ClipboardHistory.Presentation;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class NewlinesTests
{
    [Theory]
    [InlineData("a\nb", "x\r\ny", "a\r\nb")]
    [InlineData("a\r\nb", "x\r\ny", "a\r\nb")]
    [InlineData("a\rb", "x\r\ny", "a\r\nb")]
    [InlineData("a\n\r\nb", "x\r\ny", "a\r\n\r\nb")]
    public void OriginalWithCrlf_EditedTextIsCrlf(string edited, string original, string expected)
    {
        Assert.Equal(expected, Newlines.MatchOriginal(edited, original));
    }

    [Theory]
    [InlineData("a\r\nb", "x\ny", "a\nb")]
    [InlineData("a\rb", "x\ny", "a\nb")]
    [InlineData("a\nb", "x", "a\nb")]
    [InlineData("a\r\nb", "x", "a\nb")]
    public void OriginalWithoutCrlf_EditedTextIsLf(string edited, string original, string expected)
    {
        Assert.Equal(expected, Newlines.MatchOriginal(edited, original));
    }

    [Fact]
    public void EmptyAndSingleLine_AreUnchanged()
    {
        Assert.Equal(string.Empty, Newlines.MatchOriginal(string.Empty, "x\r\n"));
        Assert.Equal("line", Newlines.MatchOriginal("line", string.Empty));
    }
}
