using Waigoma.ClipboardHistory.Presentation;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class MarkdownTextTests
{
    [Fact]
    public void Fence_WrapsPlainBodyInThreeBackticksWithTextLanguage()
    {
        Assert.Equal("```text\nspike item\n```", MarkdownText.Fence("spike item", 4000));
    }

    [Fact]
    public void Fence_UsesOneMoreBacktickThanTheLongestRunInTheBody()
    {
        Assert.Equal("````text\na ``` b\n````", MarkdownText.Fence("a ``` b", 4000));
        Assert.Equal("`````text\n````\n`````", MarkdownText.Fence("````", 4000));
    }

    [Fact]
    public void Fence_NormalisesCarriageReturnsToLineFeeds()
    {
        Assert.Equal("```text\nfirst\nsecond\nthird\n```", MarkdownText.Fence("first\r\nsecond\rthird", 4000));
    }

    [Fact]
    public void Fence_AppendsEllipsisWhenTheBodyIsCut()
    {
        Assert.Equal("```text\nabcde…\n```", MarkdownText.Fence("abcdefgh", 5));
    }

    [Fact]
    public void Fence_LeavesBodyAloneWhenItFitsExactly()
    {
        Assert.Equal("```text\nabcde\n```", MarkdownText.Fence("abcde", 5));
    }

    [Fact]
    public void Fence_DoesNotSplitASurrogatePairAtTheLimit()
    {
        // The emoji takes two UTF-16 units, so cutting after the first unit would leave half of it.
        Assert.Equal("```text\na…\n```", MarkdownText.Fence("a\U0001F600b", 2));
    }

    [Fact]
    public void Fence_ReplacesAnUnpairedSurrogateWithReplacementCharacter()
    {
        Assert.Equal("```text\na�b\n```", MarkdownText.Fence("a\uD83Db", 4000));
    }

    [Fact]
    public void Fence_CountsBacktickRunsAfterCutting()
    {
        // The backticks past the limit are gone, so they must not lengthen the fence.
        Assert.Equal("```text\nab…\n```", MarkdownText.Fence("ab```", 2));
    }

    [Fact]
    public void Escape_BackslashEscapesEachMarkdownSignificantCharacter()
    {
        Assert.Equal(@"\[a\]\(b)\*\_\`\<\#\|", MarkdownText.Escape("[a](b)*_`<#|"));
    }

    [Fact]
    public void Escape_EscapesABackslashItself()
    {
        Assert.Equal(@"a\\b\[", MarkdownText.Escape(@"a\b["));
    }

    [Fact]
    public void Escape_ReplacesLineBreaksWithOneSpace()
    {
        Assert.Equal("line1 line2 end", MarkdownText.Escape("line1\r\nline2\n\rend"));
        Assert.Equal("a b", MarkdownText.Escape("a\n\nb"));
    }

    [Fact]
    public void Fence_CutsLongBodyToLimitWithEllipsis()
    {
        string body = new('a', 5_000_000);

        Assert.Equal("```text\n" + new string('a', 10) + "…\n```", MarkdownText.Fence(body, 10));
    }

    [Fact]
    public void Fence_LargeBody_RunsUnder50Ms()
    {
        string body = new('a', 5_000_000);
        _ = MarkdownText.Fence("warm up", 10);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string fenced = MarkdownText.Fence(body, 4000);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 50, $"Fence took {watch.ElapsedMilliseconds} ms.");
        // Three fence backticks, "text", a newline, 4000 characters, the ellipsis, a newline and the closing fence.
        Assert.Equal(3 + 4 + 1 + 4000 + 1 + 1 + 3, fenced.Length);
        Assert.StartsWith("```text\n" + new string('a', 4000) + "…", fenced, StringComparison.Ordinal);
    }

    [Fact]
    public void Fence_CountsTheLimitAfterLineBreaksAreNormalised()
    {
        // The raw prefix is shorter than the normalised text here, so the limit must be checked on the normalised text.
        Assert.Equal("```text\na\nb…\n```", MarkdownText.Fence("a\r\nb\r\nc\r\nd", 3));
        Assert.Equal("```text\na\nb\n```", MarkdownText.Fence("a\r\nb", 10));
    }

    [Fact]
    public void Fence_DoesNotSplitASurrogatePairAtTheCut()
    {
        string body = new string('a', 9) + "😀" + "bbb";

        Assert.Equal("```text\n" + new string('a', 9) + "…\n```", MarkdownText.Fence(body, 10));
    }

    [Fact]
    public void Escape_LeavesOrdinaryTextUnchanged()
    {
        Assert.Equal("ただのテキスト 123 abc", MarkdownText.Escape("ただのテキスト 123 abc"));
    }
}
