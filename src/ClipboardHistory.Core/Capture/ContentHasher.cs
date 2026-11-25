using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Waigoma.ClipboardHistory.Capture;

// Hashes are the dedupe key, so every producer must feed the same bytes for the same content.
public static class ContentHasher
{
    public static string ForText(string text)
    {
        // Apps disagree on line endings; normalising them lets one copy dedupe across them.
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return HashUtf16(normalized);
    }

    public static string ForFiles(IEnumerable<string> paths)
    {
        // Windows paths are case-insensitive, so the same file copied with different casing is one entry.
        string joined = string.Join('\n', paths.Select(p => p.ToLowerInvariant()));
        return HashUtf16(joined);
    }

    public static string ForBytes(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string HashUtf16(string value)
    {
        // Encoding.Unicode would replace lone surrogates with U+FFFD and merge distinct texts, so the raw
        // UTF-16 code units are hashed. The target is x64 (little-endian), so this is UTF-16LE.
        return ForBytes(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}
