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
    public void Build_KeepsAtMost16Terms()
    {
        string query = string.Join(' ', Enumerable.Range(0, 20).Select(i => "t" + i));

        IReadOnlyList<string> patterns = LikePattern.Build(query);

        Assert.Equal(16, patterns.Count);
        Assert.Equal("%t15%", patterns[15]);
    }

    [Fact]
    public void Build_CutsEachTermTo256Units()
    {
        string term = new string('a', 300);

        Assert.Equal("%" + new string('a', 256) + "%", LikePattern.Build(term).Single());
    }

    [Fact]
    public void Build_CutDoesNotSplitASurrogatePair()
    {
        // 255 units plus one emoji (two units) puts the high surrogate exactly at the 256-unit cut.
        string term = new string('a', 255) + "\U0001F600";

        Assert.Equal("%" + new string('a', 255) + "%", LikePattern.Build(term).Single());
    }

    [Fact]
    public void Build_NormalizesFullWidthAndCase_BeforeEscaping()
    {
        Assert.Equal(new[] { "%abc%" }, LikePattern.Build("ＡＢＣ"));
        Assert.Equal(new[] { @"%100\%%" }, LikePattern.Build("１００％"));
        Assert.Equal(new[] { "%デ-%" }, LikePattern.Build("デー"));
    }
}
