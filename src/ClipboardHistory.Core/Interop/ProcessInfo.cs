using System.IO;

namespace Waigoma.ClipboardHistory.Interop;

internal static unsafe class ProcessInfo
{
    // Larger than MAX_PATH so long paths still fit; a path that does not fit yields null rather than a truncated name.
    private const int PathChars = 4096;

    internal static uint PidOf(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        uint pid = 0;
        _ = Native.GetWindowThreadProcessId(hwnd, &pid);
        return pid;
    }

    internal static string? ImagePathOf(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        nint process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            Span<char> buffer = stackalloc char[PathChars];
            uint size = PathChars;
            fixed (char* p = buffer)
            {
                if (!Native.QueryFullProcessImageNameW(process, 0, p, &size))
                {
                    return null;
                }
            }

            return new string(buffer[..(int)size]);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    // File name without extension, e.g. "Microsoft.CmdPal.UI". Null when the process cannot be queried.
    internal static string? NameOf(uint pid)
    {
        string? path = ImagePathOf(pid);
        return path is null ? null : Path.GetFileNameWithoutExtension(path);
    }
}
