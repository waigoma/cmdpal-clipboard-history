using Waigoma.ClipboardHistory.Search;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class LikePatternTests
{
    [Fact]
    public void Build_EmptyOrWhitespace_ReturnsNoTerms_SoNoFilterApplies()
    {
        Assert.Empty(LikePattern.Build(""));
        Assert.Empty(LikePattern.Build(" \t　\r\n"));
    }

    [Fact]
    public void Build_SplitsOnWhitespace_AndWrapsEachTerm()
    {
        Assert.Equal(new[] { "%git%", "%main%" }, LikePattern.Build("git  main"));
    }

    [Fact]
    public void Build_SplitsOnIdeographicSpace()
    {
        Assert.Equal(new[] { "%git%", "%main%" }, LikePattern.Build("git　main"));
    }

    [Fact]
    public void Build_EscapesPercentUnderscoreAndBackslash()
    {
        Assert.Equal(new[] { @"%100\%%" }, LikePattern.Build("100%"));
        Assert.Equal(new[] { @"%a\_b%" }, LikePattern.Build("a_b"));
        Assert.Equal(new[] { @"%a\\b%" }, LikePattern.Build(@"a\b"));
    }

    [Fact]
    public void Build_NormalizesFullWidthAndCase_BeforeEscaping()
    {
        Assert.Equal(new[] { "%abc%" }, LikePattern.Build("ＡＢＣ"));
        Assert.Equal(new[] { @"%100\%%" }, LikePattern.Build("１００％"));
        Assert.Equal(new[] { "%デ-%" }, LikePattern.Build("デー"));
    }
}
