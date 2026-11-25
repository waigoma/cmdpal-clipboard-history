using System.Threading;
using Waigoma.ClipboardHistory.Paste;

namespace Waigoma.ClipboardHistory.Interop;

// Win32 implementation of IWindowSystem. The paste target must never be the Command Palette or a shell
// surface such as the taskbar or Alt+Tab, because those would swallow the Ctrl+V meant for the user's app.
public sealed class WindowSystem : IWindowSystem
{
    private const string CommandPaletteProcess = "Microsoft.CmdPal.UI";

    private static readonly string[] IgnoredProcesses =
    {
        CommandPaletteProcess,
        "SearchHost",
        "StartMenuExperienceHost",
        "ShellExperienceHost",
    };

    // Desktop surfaces (Progman, WorkerW) are shell windows too: pasting into them would drop the text on the desktop.
    private static readonly string[] IgnoredClasses =
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "TaskSwitcherWnd",
    };

    private static readonly string[] SharedHostProcesses =
    {
        "explorer",
        "ApplicationFrameHost",
    };

    // Computed once: the answer does not change for the life of this process.
    private static readonly bool OwnProcessElevated = TokenElevated(Native.GetCurrentProcess()) == true;

    private nint _lastTarget;

    public nint Foreground => Native.GetForegroundWindow();

    public nint LastTarget
    {
        get
        {
            nint hwnd = Volatile.Read(ref _lastTarget);
            return hwnd != 0 && Native.IsWindow(hwnd) ? hwnd : 0;
        }
    }

    public uint PidOf(nint hwnd) => ProcessInfo.PidOf(hwnd);

    public bool IsCommandPalette(nint hwnd)
    {
        string? name = ProcessInfo.NameOf(PidOf(hwnd));
        return string.Equals(name, CommandPaletteProcess, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsPasteTarget(nint hwnd)
    {
        if (hwnd == 0 || IsCommandPalette(hwnd))
        {
            return false;
        }

        return !IsIgnoredTarget(ProcessInfo.NameOf(PidOf(hwnd)), ClassNameOf(hwnd));
    }

    public bool IsSameTarget(nint target, nint foreground)
    {
        if (target == 0 || foreground == 0)
        {
            return false;
        }

        // A zero root owner means the window has no owner chain, so it must never match another window by accident.
        nint targetRoot = Native.GetAncestor(target, Native.GA_ROOTOWNER);
        nint foregroundRoot = Native.GetAncestor(foreground, Native.GA_ROOTOWNER);
        uint targetPid = PidOf(target);
        uint foregroundPid = PidOf(foreground);

        return SameTarget(
            sameWindow: target == foreground,
            sameRootOwner: targetRoot != 0 && targetRoot == foregroundRoot,
            samePid: targetPid != 0 && targetPid == foregroundPid,
            processName: ProcessInfo.NameOf(targetPid),
            foregroundClass: ClassNameOf(foreground));
    }

    // Pure so the decision can be tested without windows. Shell surfaces are refused first, even for the same window.
    // Explorer and UWP frame hosts share one process across unrelated windows, so only the root owner identifies an app there.
    internal static bool SameTarget(bool sameWindow, bool sameRootOwner, bool samePid, string? processName, string? foregroundClass)
    {
        if (!string.IsNullOrEmpty(foregroundClass) && IgnoredClasses.Contains(foregroundClass, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (sameWindow)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(processName) && SharedHostProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return sameRootOwner;
        }

        return samePid || sameRootOwner;
    }

    // Unknown means the answer cannot be trusted, so the paste is refused rather than sent blind.
    public bool IsElevatedOrUnknown(nint hwnd)
    {
        if (hwnd == 0)
        {
            return true;
        }

        // An elevated process can already reach elevated windows, so the restriction does not apply.
        if (OwnProcessElevated)
        {
            return false;
        }

        uint pid = PidOf(hwnd);
        nint process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
        {
            return true;
        }

        try
        {
            return TokenElevated(process) != false;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    public bool AnyModifierDown()
    {
        return IsDown(Native.VK_CONTROL) || IsDown(Native.VK_SHIFT) || IsDown(Native.VK_MENU)
            || IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN);
    }

    public unsafe bool SendCtrlV()
    {
        Native.INPUT[] inputs =
        {
            Key(Native.VK_CONTROL, 0),
            Key(Native.VK_V, 0),
            Key(Native.VK_V, Native.KEYEVENTF_KEYUP),
            Key(Native.VK_CONTROL, Native.KEYEVENTF_KEYUP),
        };

        int size = sizeof(Native.INPUT);
        uint sent;
        fixed (Native.INPUT* p = inputs)
        {
            sent = Native.SendInput((uint)inputs.Length, p, size);
        }

        // A partial send means the target received a key-up without its key-down, so anything short of 4 counts as failure.
        return sent == inputs.Length;
    }

    // Called on the foreground event thread. Ignored windows keep the previous target, so a tray or Alt+Tab
    // click does not replace the app the user was typing into.
    public void OnForegroundChanged(nint hwnd)
    {
        try
        {
            if (hwnd == 0)
            {
                return;
            }

            string? processName = ProcessInfo.NameOf(PidOf(hwnd));
            string? className = ClassNameOf(hwnd);
            if (IsIgnoredTarget(processName, className))
            {
                return;
            }

            Volatile.Write(ref _lastTarget, hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("window.foreground", ex);
        }
    }

    public string? ExeOf(nint hwnd)
    {
        return hwnd == 0 ? null : ProcessInfo.ImagePathOf(PidOf(hwnd));
    }

    internal static bool IsIgnoredTarget(string? processName, string? className)
    {
        if (!string.IsNullOrEmpty(processName) && IgnoredProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrEmpty(className) && IgnoredClasses.Contains(className, StringComparer.OrdinalIgnoreCase);
    }

    private static unsafe string? ClassNameOf(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length;
        fixed (char* p = buffer)
        {
            length = Native.GetClassNameW(hwnd, p, buffer.Length);
        }

        return length > 0 ? new string(buffer[..length]) : null;
    }

    // Null when the token cannot be read, so callers decide whether that counts as elevated.
    private static unsafe bool? TokenElevated(nint process)
    {
        nint token = 0;
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, &token))
        {
            return null;
        }

        try
        {
            uint elevated = 0;
            int returned = 0;
            if (!Native.GetTokenInformation(token, Native.TokenElevation, &elevated, sizeof(uint), &returned))
            {
                return null;
            }

            return elevated != 0;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    private static bool IsDown(int virtualKey) => (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static Native.INPUT Key(int virtualKey, uint flags)
    {
        Native.INPUT input = default;
        input.type = Native.INPUT_KEYBOARD;
        input.ki = new Native.KEYBDINPUT
        {
            wVk = (ushort)virtualKey,
            // Scan codes let apps that read hardware codes (games, some terminals) see the keystroke too.
            wScan = (ushort)Native.MapVirtualKeyW((uint)virtualKey, Native.MAPVK_VK_TO_VSC),
            dwFlags = flags,
        };
        return input;
    }
}
