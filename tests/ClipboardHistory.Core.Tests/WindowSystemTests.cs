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

    [Fact]
    public void IsIgnoredTarget_IgnoresDesktopWindowClasses()
    {
        Assert.True(WindowSystem.IsIgnoredTarget("explorer", "Progman"));
        Assert.True(WindowSystem.IsIgnoredTarget("explorer", "WorkerW"));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("XamlExplorerHostIslandWindow")]
    [InlineData("MultitaskingViewFrame")]
    [InlineData("TaskSwitcherWnd")]
    public void SameTarget_ShellForeground_IsNeverTheTarget(string foregroundClass)
    {
        // Even the same window is refused, because a shell surface is where the Ctrl+V would land.
        Assert.False(WindowSystem.SameTarget(sameWindow: true, sameRootOwner: true, samePid: true, "explorer", foregroundClass));
    }

    [Fact]
    public void SameTarget_SameWindow_IsTheTarget()
    {
        Assert.True(WindowSystem.SameTarget(sameWindow: true, sameRootOwner: false, samePid: false, "notepad", "Notepad"));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("EXPLORER")]
    [InlineData("ApplicationFrameHost")]
    [InlineData("applicationframehost")]
    public void SameTarget_ShellHostProcess_OnlyCountsSameRootOwner(string processName)
    {
        // Explorer and UWP frames put unrelated windows in one process, so a shared pid proves nothing there.
        Assert.False(WindowSystem.SameTarget(sameWindow: false, sameRootOwner: false, samePid: true, processName, "Other"));
        Assert.True(WindowSystem.SameTarget(sameWindow: false, sameRootOwner: true, samePid: false, processName, "Other"));
    }

    [Fact]
    public void SameTarget_OrdinaryProcess_AcceptsSamePidOrSameRootOwner()
    {
        Assert.True(WindowSystem.SameTarget(sameWindow: false, sameRootOwner: false, samePid: true, "notepad", "Edit"));
        Assert.True(WindowSystem.SameTarget(sameWindow: false, sameRootOwner: true, samePid: false, "notepad", "Edit"));
        Assert.False(WindowSystem.SameTarget(sameWindow: false, sameRootOwner: false, samePid: false, "notepad", "Edit"));
    }

    [Fact]
    public void SameTarget_UnknownProcessName_FallsBackToPidOrRootOwner()
    {
        Assert.True(WindowSystem.SameTarget(sameWindow: false, sameRootOwner: false, samePid: true, null, null));
        Assert.False(WindowSystem.SameTarget(sameWindow: false, sameRootOwner: false, samePid: false, null, null));
    }
}
