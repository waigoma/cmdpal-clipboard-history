using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Waigoma.ClipboardHistory.Interop;

// Win32 surface for the clipboard layer. LibraryImport keeps marshalling AOT-friendly.
internal static unsafe partial class Native
{
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_CLIPBOARDUPDATE = 0x031D;
    internal const uint WM_APP_QUEUE = 0x8001; // WM_APP + 1
    internal const uint CF_HDROP = 15;
    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_DIB = 8;
    internal const uint CF_DIBV5 = 17;
    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenElevation = 20;
    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint MAPVK_VK_TO_VSC = 0;
    internal const uint CP_ACP = 0;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;
    internal const int VK_V = 0x56;
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;
    internal static readonly nint HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    // MOUSEINPUT is the largest union member on x64, so the union starts at offset 8 and cbSize is 40.
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(MSG* msg, nint hWnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, nint lpfnWinEventProc, uint idProcess, uint idThread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    internal static partial int GetClassNameW(nint hwnd, char* className, int maxCount);

    // GA_ROOTOWNER walks owners up to the top window, which is how an app's secondary windows are grouped.
    internal const uint GA_ROOTOWNER = 3;

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumChildWindows(nint parent, nint enumFunc, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetClipboardData(uint format, nint hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    internal static partial nint GetClipboardOwner();

    [LibraryImport("user32.dll")]
    internal static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", EntryPoint = "GetClipboardFormatNameW")]
    internal static partial int GetClipboardFormatNameW(uint format, char* buffer, int maxChars);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("user32.dll")]
    internal static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    internal static partial uint MapVirtualKeyW(uint code, uint mapType);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint count, INPUT* inputs, int cbSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalFree(nint hMem);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, uint* size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int MultiByteToWideChar(uint codePage, uint flags, byte* multiByteStr, int multiByteLength, char* wideCharStr, int wideCharLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint desiredAccess, nint* token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(nint token, int tokenInformationClass, uint* tokenInformation, int tokenInformationLength, int* returnLength);
}
