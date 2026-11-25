using System.Runtime.InteropServices;
using Waigoma.ClipboardHistory.Capture;

namespace Waigoma.ClipboardHistory.Interop;

// Reads one clipboard change while the clipboard is open. Must run on the monitor's STA thread, because the
// owner window that opens the clipboard belongs to that thread.
public sealed class ClipboardReader
{
    private const string OwnMarker = "Waigoma.ClipboardHistory.Own";
    private const string ExcludeMonitorMarker = "ExcludeClipboardContentFromMonitorProcessing";
    private const string ViewerIgnoreMarker = "Clipboard Viewer Ignore";
    private const string CanIncludeMarker = "CanIncludeInClipboardHistory";
    private const string FrameHostProcess = "ApplicationFrameHost";
    private const uint FirstPrivateFormat = 0xC000;
    private const int OpenAttempts = 10;
    private const int OpenRetryMs = 20;

    private readonly Func<nint> _owner;
    private readonly Func<string?> _foregroundExe;

    public ClipboardReader(Func<nint> owner, Func<string?> foregroundExe)
    {
        _owner = owner;
        _foregroundExe = foregroundExe;
    }

    // Returns null when the clipboard stays locked by another process. The caller then drops this change.
    public ClipboardSnapshot? Read(long maxFormatBytes)
    {
        nint clipboardOwner = Native.GetClipboardOwner();
        string? source = clipboardOwner == 0 ? null : ProcessInfo.ImagePathOf(ProcessInfo.PidOf(clipboardOwner));
        source ??= _foregroundExe();
        source = ResolveFrameHostChild(source);

        if (!TryOpen(_owner()))
        {
            Log.Info("clipboard.read open-failed");
            return null;
        }

        try
        {
            return ReadOpened(maxFormatBytes, source);
        }
        catch (Exception ex)
        {
            Log.Error("clipboard.read", ex);
            return null;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    // Opening can fail while another app holds the clipboard, so the open is retried briefly before giving up.
    internal static bool TryOpen(nint owner)
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (Native.OpenClipboard(owner))
            {
                return true;
            }

            if (attempt < OpenAttempts - 1)
            {
                Thread.Sleep(OpenRetryMs);
            }
        }

        return false;
    }

    private static unsafe ClipboardSnapshot? ReadOpened(long maxFormatBytes, string? source)
    {
        uint sequence = Native.GetClipboardSequenceNumber();
        List<FormatEntry> formats = EnumerateFormats();

        FormatEntry? canIncludeEntry = FindNamed(formats, CanIncludeMarker);
        int? canInclude = canIncludeEntry is { } can ? ReadDword(can.Id) ?? 0 : null;
        bool isOwn = FindNamed(formats, OwnMarker) is not null;
        bool excludeMonitor = FindNamed(formats, ExcludeMonitorMarker) is not null;
        bool viewerIgnore = FindNamed(formats, ViewerIgnoreMarker) is not null;

        // Markers decide before any content is read, so a password manager's copy never touches the data.
        if (isOwn || excludeMonitor || viewerIgnore || canInclude == 0)
        {
            return Withheld(sequence, source, isOwn, excludeMonitor, viewerIgnore, canInclude);
        }

        string? text = null;
        if (Find(formats, Native.CF_UNICODETEXT) is { } unicode)
        {
            nint handle = Native.GetClipboardData(unicode.Id);
            ulong size = handle == 0 ? 0 : (ulong)Native.GlobalSize(handle);

            // An oversize primary is not recorded at all, so a huge copy never becomes a smaller fallback entry.
            if (size > (ulong)maxFormatBytes * 2 + 2)
            {
                return Withheld(sequence, source, false, false, false, canInclude);
            }

            if (handle != 0)
            {
                text = ReadText(handle);
            }
        }

        List<string>? files = null;
        if (Find(formats, Native.CF_HDROP) is { } drop)
        {
            byte[]? data = ReadFormat(drop, maxFormatBytes);
            if (data is not null)
            {
                List<string> decoded = DropFiles.Decode(data).ToList();
                files = decoded.Count > 0 ? decoded : null;
            }
        }

        var formatList = new List<ClipFormat>(2);
        AddFormat(formatList, formats, FormatNames.Html, maxFormatBytes, trimTrailingNuls: true);
        AddFormat(formatList, formats, FormatNames.Rtf, maxFormatBytes, trimTrailingNuls: true);
        ClipFormat? image = PickImage(formats, maxFormatBytes);
        if (image is not null)
        {
            formatList.Add(image);
        }

        return new ClipboardSnapshot(sequence, source, false, false, false, canInclude,
            text, files, formatList);
    }

    private static ClipboardSnapshot Withheld(uint sequence, string? source, bool isOwn, bool excludeMonitor,
        bool viewerIgnore, int? canInclude)
    {
        return new ClipboardSnapshot(sequence, source, isOwn, excludeMonitor, viewerIgnore, canInclude,
            null, null, Array.Empty<ClipFormat>());
    }

    // Text is read up to its first NUL, because some apps leave garbage after the terminator.
    private static unsafe string? ReadText(nint handle)
    {
        nint pointer = Native.GlobalLock(handle);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            int chars = (int)((ulong)Native.GlobalSize(handle) / 2);
            ReadOnlySpan<char> span = new((char*)pointer, chars);
            int terminator = span.IndexOf('\0');
            ReadOnlySpan<char> value = terminator < 0 ? span : span[..terminator];
            return value.IsEmpty ? null : new string(value);
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }
    }

    // PNG is preferred because it keeps alpha and is smaller. DIB and DIBV5 are the same picture, so whichever
    // the source listed first is used, and only one image is ever kept.
    private static ClipFormat? PickImage(List<FormatEntry> formats, long maxFormatBytes)
    {
        if (FindNamed(formats, FormatNames.Png) is { } png && ReadFormat(png, maxFormatBytes) is { } pngData)
        {
            return new ClipFormat(FormatNames.Png, pngData);
        }

        foreach (FormatEntry entry in formats)
        {
            if (entry.Name is not null || (entry.Id != Native.CF_DIBV5 && entry.Id != Native.CF_DIB))
            {
                continue;
            }

            if (ReadFormat(entry, maxFormatBytes) is { } dib)
            {
                return new ClipFormat(entry.Id == Native.CF_DIBV5 ? FormatNames.DibV5 : FormatNames.Dib, dib);
            }
        }

        return null;
    }

    private static void AddFormat(List<ClipFormat> target, List<FormatEntry> formats, string name, long maxFormatBytes,
        bool trimTrailingNuls)
    {
        if (FindNamed(formats, name) is not { } entry || ReadFormat(entry, maxFormatBytes) is not { } data)
        {
            return;
        }

        int length = data.Length;
        if (trimTrailingNuls)
        {
            while (length > 0 && data[length - 1] == 0)
            {
                length--;
            }
        }

        if (length > 0)
        {
            target.Add(new ClipFormat(name, length == data.Length ? data : data[..length]));
        }
    }

    private static byte[]? ReadFormat(FormatEntry entry, long maxFormatBytes)
    {
        nint handle = Native.GetClipboardData(entry.Id);
        return handle == 0 ? null : CopyWithin(handle, maxFormatBytes);
    }

    // Copies the exact GlobalSize bytes. Anything over the limit is skipped rather than copied, so a huge
    // format never reaches managed memory.
    private static unsafe byte[]? CopyWithin(nint handle, long maxFormatBytes)
    {
        ulong size = (ulong)Native.GlobalSize(handle);
        if (size == 0 || size > (ulong)maxFormatBytes)
        {
            return null;
        }

        nint pointer = Native.GlobalLock(handle);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            byte[] data = new byte[(int)size];
            new ReadOnlySpan<byte>((void*)pointer, data.Length).CopyTo(data);
            return data;
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }
    }

    // DWORD markers carry a flag. A marker that is present but unreadable is reported as 0 by the caller,
    // because a source that declares the flag and then hides its value should not be recorded.
    private static unsafe int? ReadDword(uint id)
    {
        nint handle = Native.GetClipboardData(id);
        if (handle == 0 || (ulong)Native.GlobalSize(handle) < sizeof(uint))
        {
            return null;
        }

        nint pointer = Native.GlobalLock(handle);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            uint value = *(uint*)pointer;
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }
    }

    // Standard formats have fixed ids; only registered formats need a name lookup.
    private static unsafe List<FormatEntry> EnumerateFormats()
    {
        var formats = new List<FormatEntry>();
        uint id = 0;
        while ((id = Native.EnumClipboardFormats(id)) != 0)
        {
            formats.Add(new FormatEntry(id, NameOf(id)));
        }

        return formats;
    }

    private static unsafe string? NameOf(uint id)
    {
        if (id < FirstPrivateFormat)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[256];
        int length;
        fixed (char* p = buffer)
        {
            length = Native.GetClipboardFormatNameW(id, p, buffer.Length);
        }

        return length > 0 ? new string(buffer[..length]) : null;
    }

    // Walks up to the child window of the foreground that belongs to a different process, which is the app
    // that a Store app's frame is hosting. Without it the source would be reported as ApplicationFrameHost.
    private static unsafe string? ResolveFrameHostChild(string? source)
    {
        if (source is null || !Path.GetFileNameWithoutExtension(source).Equals(FrameHostProcess, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        nint foreground = Native.GetForegroundWindow();
        if (foreground == 0)
        {
            return source;
        }

        var search = new ChildSearch { FrameHostPid = ProcessInfo.PidOf(foreground) };
        GCHandle handle = GCHandle.Alloc(search);
        try
        {
            Native.EnumChildWindows(foreground, (nint)(delegate* unmanaged<nint, nint, int>)&EnumChildCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return search.Pid == 0 ? source : ProcessInfo.ImagePathOf(search.Pid) ?? source;
    }

    [UnmanagedCallersOnly]
    private static int EnumChildCallback(nint hwnd, nint lParam)
    {
        try
        {
            if (GCHandle.FromIntPtr(lParam).Target is not ChildSearch search)
            {
                return 0;
            }

            uint pid = ProcessInfo.PidOf(hwnd);
            if (pid != 0 && pid != search.FrameHostPid)
            {
                search.Pid = pid;
                return 0;
            }

            return 1;
        }
        catch (Exception ex)
        {
            Log.Error("clipboard.enumChild", ex);
            return 0;
        }
    }

    private static FormatEntry? Find(List<FormatEntry> formats, uint id)
    {
        foreach (FormatEntry entry in formats)
        {
            if (entry.Id == id)
            {
                return entry;
            }
        }

        return null;
    }

    private static FormatEntry? FindNamed(List<FormatEntry> formats, string name)
    {
        foreach (FormatEntry entry in formats)
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private readonly record struct FormatEntry(uint Id, string? Name);

    private sealed class ChildSearch
    {
        public uint FrameHostPid { get; init; }

        public uint Pid { get; set; }
    }
}
