using System.Globalization;
using System.Text;

namespace Waigoma.ClipboardHistory.Interop;

// Parses the CF_HTML envelope ("Version:0.9" header with byte offsets) to get the fragment the user copied.
public static class HtmlFormat
{
    private const string StartFragmentKey = "StartFragment:";
    private const string EndFragmentKey = "EndFragment:";

    // Header lines are short and ASCII; the scan stops at the first line that is not a "key:value" pair.
    private const int MaxHeaderBytes = 4096;

    public static string? ExtractFragment(ReadOnlySpan<byte> cfHtml)
    {
        long? start = null;
        long? end = null;
        int position = 0;
        int limit = Math.Min(cfHtml.Length, MaxHeaderBytes);

        while (position < limit)
        {
            int lineEnd = position;
            while (lineEnd < limit && cfHtml[lineEnd] != '\n')
            {
                lineEnd++;
            }

            string line = Encoding.ASCII.GetString(cfHtml.Slice(position, lineEnd - position)).TrimEnd('\r');
            if (line.Length == 0 || line[0] == '<' || line.IndexOf(':') < 0)
            {
                break;
            }

            if (line.StartsWith(StartFragmentKey, StringComparison.Ordinal))
            {
                start = ParseOffset(line, StartFragmentKey.Length);
            }
            else if (line.StartsWith(EndFragmentKey, StringComparison.Ordinal))
            {
                end = ParseOffset(line, EndFragmentKey.Length);
            }

            position = lineEnd + 1;
        }

        if (start is not { } s || end is not { } e)
        {
            return null;
        }

        if (s < 0 || e < s || e > cfHtml.Length)
        {
            return null;
        }

        return Encoding.UTF8.GetString(cfHtml.Slice((int)s, (int)(e - s)));
    }

    private static long? ParseOffset(string line, int keyLength)
    {
        string value = line.Substring(keyLength).Trim();
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long offset) ? offset : null;
    }
}
