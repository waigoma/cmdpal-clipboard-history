using System.Text;

namespace Waigoma.ClipboardHistory.Presentation;

// Builds the markdown the details pane renders. Clipboard text is untrusted, so everything it contributes goes through
// a fence or an escape and never reaches the renderer as markup.
public static class MarkdownText
{
    private const int MinFenceLength = 3;

    private const string Ellipsis = "…";

    private const char ReplacementCharacter = '�';

    // Characters that would start emphasis, a link, an HTML tag, a heading or a table cell. The backslash is included
    // so that an escape written by the clipboard text cannot cancel the escape added here.
    private const string InlineSpecials = @"\[](*_`<#|";

    // Returns a fenced block with the text language. The fence is longer than any backtick run in the body, so the
    // body cannot close it, and a body over maxChars is cut and marked with an ellipsis.
    public static string Fence(string body, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);

        // Normalising stops after maxChars + 1 characters, so a multi-megabyte body costs no more than the limit.
        string text = Normalize(body, maxChars + 1);
        string content = text;
        if (text.Length > maxChars)
        {
            int cut = maxChars;

            // A high surrogate at the cut would be separated from its pair, which renders as a broken glyph.
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;
            }

            content = text[..cut] + Ellipsis;
        }

        string fence = new('`', Math.Max(MinFenceLength, LongestBacktickRun(content) + 1));
        return fence + "text\n" + content + "\n" + fence;
    }

    // Makes text safe inside a sentence or a table cell. A line break becomes a space, because a break would end the
    // inline run and let the rest of the text start a new block.
    public static string Escape(string inline)
    {
        ArgumentNullException.ThrowIfNull(inline);

        var sb = new StringBuilder(inline.Length);
        for (int i = 0; i < inline.Length; i++)
        {
            char c = inline[i];
            if (c is '\r' or '\n')
            {
                while (i + 1 < inline.Length && inline[i + 1] is ('\r' or '\n'))
                {
                    i++;
                }

                sb.Append(' ');
            }
            else if (InlineSpecials.Contains(c))
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // Line endings become LF so the line count in the body does not depend on where the text was copied from. A
    // surrogate without its partner is replaced, because a lone surrogate cannot be encoded as UTF-8 for the renderer.
    // Stops once the result is longer than limit, so the caller can tell the text is over the limit without reading the rest.
    private static string Normalize(string body, int limit)
    {
        var sb = new StringBuilder(Math.Min(body.Length, limit + 1));
        for (int i = 0; i < body.Length && sb.Length <= limit; i++)
        {
            char c = body[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < body.Length && body[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (char.IsHighSurrogate(c) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]))
            {
                sb.Append(c).Append(body[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append(ReplacementCharacter);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }
}
