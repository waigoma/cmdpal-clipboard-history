using System.Text;

namespace Waigoma.ClipboardHistory.Search;

// Turns a user query into LIKE patterns, one per term, that are matched with AND.
public static class LikePattern
{
    // Each LIKE term is one AND clause, so an unbounded query would make the search SQL unbounded too.
    private const int MaxTerms = 16;

    private const int MaxTermUnits = 256;

    // Used with ESCAPE '\' in the SQL, so the escape character itself must be doubled here.
    public static IReadOnlyList<string> Build(string search)
    {
        string normalized = SearchNormalizer.Normalize(search);
        // A null separator array splits on any whitespace, which covers the ideographic space as well.
        string[] terms = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int count = Math.Min(terms.Length, MaxTerms);
        var patterns = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            patterns.Add("%" + Escape(Cut(terms[i])) + "%");
        }

        return patterns;
    }

    // A high surrogate left at the end would be an invalid UTF-16 unit, so the cut steps back over it.
    private static string Cut(string term)
    {
        if (term.Length <= MaxTermUnits)
        {
            return term;
        }

        int length = MaxTermUnits;
        if (char.IsHighSurrogate(term[length - 1]))
        {
            length--;
        }

        return term[..length];
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
