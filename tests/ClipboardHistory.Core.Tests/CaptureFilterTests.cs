using Waigoma.ClipboardHistory.Capture;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class CaptureFilterTests
{
    private static readonly IReadOnlySet<string> NoApps = new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> KeePassOnly = new HashSet<string>(StringComparer.Ordinal) { "keepass.exe" };

    [Theory]
    [InlineData(true, false, false, null)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, true, null)]
    [InlineData(false, false, false, 0)]
    public void ShouldIgnore_ReturnsTrue_ForEachSingleSuppressionMarker(bool isOwnWrite, bool excludeMonitor, bool viewerIgnore, int? canIncludeInHistory)
    {
        ClipboardSnapshot snapshot = Snapshot(isOwnWrite: isOwnWrite, excludeMonitor: excludeMonitor,
            viewerIgnore: viewerIgnore, canIncludeInHistory: canIncludeInHistory);

        Assert.True(CaptureFilter.ShouldIgnore(snapshot, NoApps));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public void ShouldIgnore_ReturnsFalse_WhenNoMarkerAndCanIncludeIsNotZero(int? canIncludeInHistory)
    {
        ClipboardSnapshot snapshot = Snapshot(canIncludeInHistory: canIncludeInHistory);

        Assert.False(CaptureFilter.ShouldIgnore(snapshot, NoApps));
    }

    [Fact]
    public void ShouldIgnore_MatchesExcludedAppByFileNameOnly()
    {
        Assert.True(CaptureFilter.ShouldIgnore(Snapshot(sourceExePath: @"C:\x\KeePass.exe"), KeePassOnly));
    }

    [Fact]
    public void ShouldIgnore_DoesNotMatchExcludedAppInDirectoryName()
    {
        Assert.False(CaptureFilter.ShouldIgnore(Snapshot(sourceExePath: @"C:\keepass.exe\other.exe"), KeePassOnly));
    }

    [Fact]
    public void ShouldIgnore_ReturnsFalse_WhenSourceIsUnknown()
    {
        Assert.False(CaptureFilter.ShouldIgnore(Snapshot(sourceExePath: null), KeePassOnly));
    }

    [Fact]
    public void ParseExcludedApps_SplitsTrimsLowerCasesAndDropsEmptyLines()
    {
        IReadOnlySet<string> apps = CaptureFilter.ParseExcludedApps("KeePass.exe\r\n\n  code.exe \nC:\\x\\Foo.EXE");

        Assert.Equal(3, apps.Count);
        Assert.True(apps.SetEquals(new[] { "keepass.exe", "code.exe", "foo.exe" }));
    }

    [Fact]
    public void ParseExcludedApps_StripsQuotesAroundALine()
    {
        IReadOnlySet<string> apps = CaptureFilter.ParseExcludedApps("\"C:\\Tools\\Quoted.exe\"");

        Assert.True(apps.SetEquals(new[] { "quoted.exe" }));
    }

    [Fact]
    public void ParseExcludedApps_ReturnsEmpty_ForNull()
    {
        Assert.Empty(CaptureFilter.ParseExcludedApps(null));
    }

    private static ClipboardSnapshot Snapshot(
        string? sourceExePath = null,
        bool isOwnWrite = false,
        bool excludeMonitor = false,
        bool viewerIgnore = false,
        int? canIncludeInHistory = null)
    {
        return new ClipboardSnapshot(1, sourceExePath, isOwnWrite, excludeMonitor, viewerIgnore,
            canIncludeInHistory, "text", null, Array.Empty<ClipFormat>());
    }
}
