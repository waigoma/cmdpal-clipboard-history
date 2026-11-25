using Waigoma.ClipboardHistory.Capture;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class WindowsHistorySourceTests
{
    [Fact]
    public void HasBlockingMarker_PlainFormats_ReturnsFalseAndNoCheck()
    {
        bool blocked = WindowsHistorySource.HasBlockingMarker(new[] { "Text", "UnicodeText" }, out bool needsCheck);

        Assert.False(blocked);
        Assert.False(needsCheck);
    }

    [Fact]
    public void HasBlockingMarker_EmptyFormats_ReturnsFalseAndNoCheck()
    {
        bool blocked = WindowsHistorySource.HasBlockingMarker(Array.Empty<string>(), out bool needsCheck);

        Assert.False(blocked);
        Assert.False(needsCheck);
    }

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("Clipboard Viewer Ignore")]
    [InlineData("waigoma.ClipboardHistory.Own")]
    public void HasBlockingMarker_BlockingName_ReturnsTrue(string name)
    {
        bool blocked = WindowsHistorySource.HasBlockingMarker(new[] { "Text", name }, out bool needsCheck);

        Assert.True(blocked);
        Assert.False(needsCheck);
    }

    [Theory]
    [InlineData("excludeclipboardcontentfrommonitorprocessing")]
    [InlineData("CLIPBOARD VIEWER IGNORE")]
    [InlineData("waigoma.clipboardhistory.own")]
    public void HasBlockingMarker_IsCaseInsensitive(string name)
    {
        bool blocked = WindowsHistorySource.HasBlockingMarker(new[] { name }, out _);

        Assert.True(blocked);
    }

    [Theory]
    [InlineData("CanIncludeInClipboardHistory")]
    [InlineData("CANINCLUDEINCLIPBOARDHISTORY")]
    public void HasBlockingMarker_CanIncludeMarker_RequestsValueCheckWithoutBlocking(string name)
    {
        bool blocked = WindowsHistorySource.HasBlockingMarker(new[] { "Text", name }, out bool needsCheck);

        Assert.False(blocked);
        Assert.True(needsCheck);
    }

    [Fact]
    public void HasBlockingMarker_BlockingAndCanInclude_ReturnsBoth()
    {
        bool blocked = WindowsHistorySource.HasBlockingMarker(
            new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory" }, out bool needsCheck);

        Assert.True(blocked);
        Assert.True(needsCheck);
    }
}
