using Waigoma.ClipboardHistory.Worker;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class OcrTextTests
{
    [Fact]
    public void Clean_RemovesSpacesBetweenJapaneseCharacters()
    {
        Assert.Equal("日本語の test 文字列", OcrText.Clean("日 本 語 の test 文 字 列"));
    }

    [Fact]
    public void Clean_LeavesAsciiTextUnchanged()
    {
        Assert.Equal("hello world", OcrText.Clean("hello world"));
    }

    [Fact]
    public void Clean_KeepsNewlines()
    {
        Assert.Equal("日本\n語文", OcrText.Clean("日本\n語 文"));
    }

    [Fact]
    public void Clean_RemovesTabsAndRunsOfSpacesBetweenJapaneseCharacters()
    {
        Assert.Equal("日本語", OcrText.Clean("日\t 本   語"));
    }

    [Fact]
    public void Clean_KeepsSpaceWhenOnlyOneSideIsJapanese()
    {
        Assert.Equal("日 a", OcrText.Clean("日 a"));
        Assert.Equal("a 日", OcrText.Clean("a 日"));
    }

    [Fact]
    public void Clean_TreatsJapanesePunctuationAsJapanese()
    {
        Assert.Equal("。日", OcrText.Clean("。 日"));
    }

    [Fact]
    public void Clean_TreatsHalfWidthKatakanaAsJapanese()
    {
        Assert.Equal("ｱｲ", OcrText.Clean("ｱ ｲ"));
    }

    [Fact]
    public void Clean_KeepsSpacesBetweenNonJapaneseScripts()
    {
        Assert.Equal("한 국", OcrText.Clean("한 국"));
    }
}
