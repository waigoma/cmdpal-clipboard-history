using System.Text;

namespace Waigoma.ClipboardHistory.Interop;

// DROPFILES layout: pFiles (4), pt (8), fNC (4), fWide (4), then the file list, each path followed by NUL and the list by a second NUL.
public static class DropFiles
{
    private const int HeaderSize = 20;
    private const int FileListOffset = HeaderSize;

    public static byte[] Encode(IReadOnlyList<string> paths)
    {
        var builder = new StringBuilder();
        foreach (string path in paths)
        {
            builder.Append(path).Append('\0');
        }

        builder.Append('\0');
        byte[] list = Encoding.Unicode.GetBytes(builder.ToString());

        byte[] data = new byte[HeaderSize + list.Length];
        BitConverter.GetBytes((uint)FileListOffset).CopyTo(data, 0);
        BitConverter.GetBytes(1u).CopyTo(data, 16);
        list.CopyTo(data, HeaderSize);
        return data;
    }

    // A truncated list (no terminating empty entry) yields nothing, so a partial path set is never pasted.
    public static IReadOnlyList<string> Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            return Array.Empty<string>();
        }

        uint offset = BitConverter.ToUInt32(data[..4]);
        bool wide = BitConverter.ToUInt32(data.Slice(16, 4)) != 0;
        if (offset < HeaderSize || offset > data.Length)
        {
            return Array.Empty<string>();
        }

        ReadOnlySpan<byte> list = data[(int)offset..];
        string text = wide ? Encoding.Unicode.GetString(list[..(list.Length & ~1)]) : AnsiToString(list);
        return ParseList(text);
    }

    private static IReadOnlyList<string> ParseList(string text)
    {
        var paths = new List<string>();
        int position = 0;
        while (true)
        {
            int end = text.IndexOf('\0', position);
            if (end < 0)
            {
                return Array.Empty<string>();
            }

            if (end == position)
            {
                return paths;
            }

            paths.Add(text.Substring(position, end - position));
            position = end + 1;
        }
    }

    // fWide == 0 means the system ANSI code page, which .NET cannot name without a provider, so Win32 decodes it.
    private static unsafe string AnsiToString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        fixed (byte* source = bytes)
        {
            int count = Native.MultiByteToWideChar(Native.CP_ACP, 0, source, bytes.Length, null, 0);
            if (count <= 0)
            {
                return string.Empty;
            }

            char[] buffer = new char[count];
            fixed (char* target = buffer)
            {
                int written = Native.MultiByteToWideChar(Native.CP_ACP, 0, source, bytes.Length, target, count);
                return written <= 0 ? string.Empty : new string(target, 0, written);
            }
        }
    }
}
