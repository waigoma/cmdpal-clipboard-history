using Waigoma.ClipboardHistory.Interop;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class WindowSystemTests
{
    [Theory]
    [InlineData("Microsoft.CmdPal.UI")]
    [InlineData("microsoft.cmdpal.ui")]
    [InlineData("SearchHost")]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("ShellExperienceHost")]
    public void IsIgnoredTarget_IgnoresOwnAndShellProcesses(string processName)
    {
        Assert.True(WindowSystem.IsIgnoredTarget(processName, null));
    }

    [Theory]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("XamlExplorerHostIslandWindow")]
    [InlineData("MultitaskingViewFrame")]
    [InlineData("TaskSwitcherWnd")]
    [InlineData("shell_traywnd")]
    public void IsIgnoredTarget_IgnoresShellWindowClasses(string className)
    {
        Assert.True(WindowSystem.IsIgnoredTarget("explorer", className));
    }

    [Fact]
    public void IsIgnoredTarget_AcceptsOrdinaryApplication()
    {
        Assert.False(WindowSystem.IsIgnoredTarget("notepad", "Notepad"));
    }

    [Fact]
    public void IsIgnoredTarget_UnknownNamesAreNotIgnored()
    {
        Assert.False(WindowSystem.IsIgnoredTarget(null, null));
        Assert.False(WindowSystem.IsIgnoredTarget("", ""));
    }
}
