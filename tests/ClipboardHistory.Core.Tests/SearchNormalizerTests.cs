using Waigoma.ClipboardHistory.Search;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class SearchNormalizerTests
{
    [Fact]
    public void Normalize_FoldsFullWidthAndDashLikeCharacters()
    {
        Assert.Equal("abc デ-タ-----", SearchNormalizer.Normalize("ＡＢＣ　データ―－‐–—"));
    }

    [Theory]
    [InlineData("ー")]
    [InlineData("―")]
    [InlineData("－")]
    [InlineData("‐")]
    [InlineData("‑")]
    [InlineData("–")]
    [InlineData("—")]
    [InlineData("−")]
    public void Normalize_MapsEachDashLikeCharacterToHyphen(string dash)
    {
        Assert.Equal("-", SearchNormalizer.Normalize(dash));
    }

    [Fact]
    public void Normalize_LowerCasesAsciiLetters()
    {
        Assert.Equal("hello world 123", SearchNormalizer.Normalize("Hello World 123"));
    }

    [Fact]
    public void Normalize_ConvertsFullWidthAsciiToHalfWidth()
    {
        Assert.Equal("a!~", SearchNormalizer.Normalize("Ａ！～"));
    }

    [Fact]
    public void Normalize_ConvertsIdeographicSpaceToSpace()
    {
        Assert.Equal(" ", SearchNormalizer.Normalize("　"));
    }

    [Fact]
    public void Normalize_LeavesOtherCharactersUnchanged()
    {
        Assert.Equal("日本語ひらがな", SearchNormalizer.Normalize("日本語ひらがな"));
        Assert.Equal("Ä", SearchNormalizer.Normalize("Ä"));
    }

    [Fact]
    public void Normalize_OfEmptyString_IsEmpty()
    {
        Assert.Equal(string.Empty, SearchNormalizer.Normalize(string.Empty));
    }
}
