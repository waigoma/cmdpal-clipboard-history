using System.Text;

namespace Waigoma.ClipboardHistory.Search;

// Folds the width and dash variants that Japanese IMEs and copied text produce, so one query matches both.
public static class SearchNormalizer
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(Map(c));
        }

        return sb.ToString();
    }

    private static char Map(char c)
    {
        if (c == '　')
        {
            return ' ';
        }

        if (IsDashLike(c))
        {
            return '-';
        }

        // Full-width ASCII (U+FF01..U+FF5E) sits at a fixed offset from its half-width form.
        if (c >= '！' && c <= '～')
        {
            c = (char)(c - 0xFEE0);
        }

        if (c >= 'A' && c <= 'Z')
        {
            c = (char)(c + ('a' - 'A'));
        }

        return c;
    }

    private static bool IsDashLike(char c) => c switch
    {
        'ー' or '―' or '－' or '‐' or '‑' or '–' or '—' or '−' => true,
        _ => false,
    };
}
