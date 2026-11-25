using System.Text;

namespace Waigoma.ClipboardHistory.Search;

// Turns a user query into LIKE patterns, one per term, that are matched with AND.
public static class LikePattern
{
    // Used with ESCAPE '\' in the SQL, so the escape character itself must be doubled here.
    public static IReadOnlyList<string> Build(string search)
    {
        string normalized = SearchNormalizer.Normalize(search);
        // A null separator array splits on any whitespace, which covers the ideographic space as well.
        string[] terms = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var patterns = new List<string>(terms.Length);
        foreach (string term in terms)
        {
            patterns.Add("%" + Escape(term) + "%");
        }

        return patterns;
    }

    private static string Escape(string term)
    {
        var escaped = new StringBuilder(term.Length);
        foreach (char c in term)
        {
            if (c is '\\' or '%' or '_')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
