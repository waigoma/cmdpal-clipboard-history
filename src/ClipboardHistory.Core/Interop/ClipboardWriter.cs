using System.Text;
using Microsoft.Win32.SafeHandles;
using Waigoma.ClipboardHistory.Paste;

namespace Waigoma.ClipboardHistory.Interop;

// Writes a payload to the clipboard. Works from any thread: the owner window may belong to the monitor thread.
public sealed class ClipboardWriter : IClipboardWriter
{
    private const string OwnMarker = "Waigoma.ClipboardHistory.Own";
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const uint DropEffectCopy = 1;
    private const uint DibV5HeaderSize = 124;
    private const long MaxFileBytes = 512L * 1024 * 1024;

    // Registered once per process, since RegisterClipboardFormat returns the same id for the same name.
    private static readonly uint HtmlId = Native.RegisterClipboardFormatW(FormatNames.Html);
    private static readonly uint RtfId = Native.RegisterClipboardFormatW(FormatNames.Rtf);
    private static readonly uint PngId = Native.RegisterClipboardFormatW(FormatNames.Png);
    private static readonly uint OwnId = Native.RegisterClipboardFormatW(OwnMarker);
    private static readonly uint DropEffectId = Native.RegisterClipboardFormatW(PreferredDropEffect);

    private readonly Func<nint> _owner;

    public ClipboardWriter(Func<nint> owner)
    {
        _owner = owner;
    }

    // Everything is prepared before the clipboard is opened, so a missing file or failed allocation leaves the
    // user's current clipboard as it was.
    public bool Write(ClipboardPayload payload)
    {
        var pending = new List<Pending>();
        try
        {
            Prepare(payload, pending);
            if (!pending.Exists(p => p.Primary))
            {
                return false;
            }

            return Publish(pending);
        }
        catch (Exception ex)
        {
            Log.Error("clipboard.write", ex);
            return false;
        }
        finally
        {
            // A handle still owned here was never handed to the clipboard, so it must be freed to avoid a leak.
            foreach (Pending item in pending)
            {
                if (item.Handle != 0)
                {
                    Native.GlobalFree(item.Handle);
                }
            }
        }
    }

    private static void Prepare(ClipboardPayload payload, List<Pending> pending)
    {
        // Text first, then files, then an image: the same order a user would expect a paste target to prefer.
        bool textPrimary = !string.IsNullOrEmpty(payload.Text);
        bool filesPrimary = !textPrimary && payload.Files is { Count: > 0 };
        // PNG and DIB are interchangeable primaries: either one succeeding is enough for an image copy.
        bool imagePrimary = !textPrimary && !filesPrimary;

        if (textPrimary)
        {
            Add(pending, Native.CF_UNICODETEXT, AllocBytes(Encoding.Unicode.GetBytes(payload.Text!), 2), primary: true);
        }

        if (payload.Html is { Length: > 0 } html)
        {
            Add(pending, HtmlId, AllocBytes(html, 1));
        }

        if (payload.Rtf is { Length: > 0 } rtf)
        {
            Add(pending, RtfId, AllocBytes(rtf, 1));
        }

        if (payload.Files is { Count: > 0 } files)
        {
            Add(pending, Native.CF_HDROP, AllocBytes(DropFiles.Encode(files), 0), primary: filesPrimary);
            Add(pending, DropEffectId, AllocBytes(BitConverter.GetBytes(DropEffectCopy), 0));
        }

        if (!string.IsNullOrEmpty(payload.PngPath))
        {
            Add(pending, PngId, ReadFileToGlobal(payload.PngPath, out _), primary: imagePrimary);
        }

        if (!string.IsNullOrEmpty(payload.DibPath))
        {
            nint handle = ReadFileToGlobal(payload.DibPath, out uint headerSize);
            uint format = headerSize == DibV5HeaderSize ? Native.CF_DIBV5 : Native.CF_DIB;
            Add(pending, format, handle, primary: imagePrimary);
        }

        // The marker is always written, so the monitor recognises this copy as our own and does not record it again.
        Add(pending, OwnId, AllocBytes(new byte[] { 1 }, 0));
    }

    private static void Add(List<Pending> pending, uint format, nint handle, bool primary = false)
    {
        if (handle == 0)
        {
            return;
        }

        pending.Add(new Pending { Format = format, Handle = handle, Primary = primary });
    }

    private bool Publish(List<Pending> pending)
    {
        if (!ClipboardReader.TryOpen(_owner()))
        {
            Log.Info("clipboard.write open-failed");
            return false;
        }

        bool primarySet = false;
        try
        {
            if (!Native.EmptyClipboard())
            {
                Log.Info("clipboard.write empty-failed");
                return false;
            }

            foreach (Pending item in pending)
            {
                // Format 0 means the name could not be registered; the handle stays owned here and is freed.
                if (item.Format == 0)
                {
                    continue;
                }

                // A successful SetClipboardData takes ownership of the handle, so it must not be freed afterwards.
                if (Native.SetClipboardData(item.Format, item.Handle) != 0)
                {
                    item.Handle = 0;
                    primarySet |= item.Primary;
                }
            }
        }
        finally
        {
            Native.CloseClipboard();
        }

        // Without the primary format the paste target would receive nothing useful, so the copy counts as failed.
        return primarySet;
    }

    // Copies data into a new movable global block, plus a zeroed terminator. Returns 0 on failure.
    private static unsafe nint AllocBytes(ReadOnlySpan<byte> data, int terminatorBytes)
    {
        int size = data.Length + terminatorBytes;
        nint handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)size);
        if (handle == 0)
        {
            return 0;
        }

        nint pointer = Native.GlobalLock(handle);
        if (pointer == 0)
        {
            Native.GlobalFree(handle);
            return 0;
        }

        try
        {
            Span<byte> destination = new((void*)pointer, size);
            data.CopyTo(destination);
            destination.Slice(data.Length).Clear();
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }

        return handle;
    }

    // Reads the file straight into the global block, so a large picture is never held twice in managed memory.
    // On success, firstWord holds the first little-endian uint32 (the DIB header size) for format selection.
    private static unsafe nint ReadFileToGlobal(string path, out uint firstWord)
    {
        firstWord = 0;
        nint handle = 0;
        bool succeeded = false;
        try
        {
            using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            long length = RandomAccess.GetLength(file);
            if (length <= 0 || length > MaxFileBytes)
            {
                return 0;
            }

            handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)length);
            if (handle == 0)
            {
                return 0;
            }

            nint pointer = Native.GlobalLock(handle);
            if (pointer == 0)
            {
                return 0;
            }

            bool complete;
            uint first = 0;
            try
            {
                Span<byte> target = new((void*)pointer, (int)length);
                complete = ReadAll(file, target);
                if (complete && target.Length >= sizeof(uint))
                {
                    first = BitConverter.ToUInt32(target[..sizeof(uint)]);
                }
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }

            if (!complete)
            {
                return 0;
            }

            firstWord = first;
            succeeded = true;
            return handle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The path itself is left out of the log: it can name a private file.
            Log.Info("clipboard.write file-unreadable");
            return 0;
        }
        finally
        {
            // Any exit that does not hand the block over (including an unexpected exception) frees it here.
            if (!succeeded && handle != 0)
            {
                Native.GlobalFree(handle);
            }
        }
    }

    private static bool ReadAll(SafeFileHandle file, Span<byte> target)
    {
        int total = 0;
        while (total < target.Length)
        {
            int read = RandomAccess.Read(file, target.Slice(total), total);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private sealed class Pending
    {
        public uint Format { get; init; }

        public nint Handle { get; set; }

        // The format the paste depends on. Other content formats are only fallbacks for the receiving app.
        public bool Primary { get; init; }
    }
}
