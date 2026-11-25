using System.Text.RegularExpressions;

namespace Waigoma.ClipboardHistory.Capture;

public static partial class ContentClassifier
{
    // Text longer than this is never treated as a link, email or color, so skipping the scan keeps classification constant-time.
    internal const int MaxScannedLength = 4096;

    public static EntryKind ClassifyText(string text)
    {
        if (text.Length > MaxScannedLength)
        {
            return EntryKind.Text;
        }

        string trimmed = text.Trim();
        if (IsLink(trimmed))
        {
            return EntryKind.Link;
        }

        if (IsEmail(trimmed))
        {
            return EntryKind.Email;
        }

        return ColorParser.TryParse(trimmed, out _) ? EntryKind.Color : EntryKind.Text;
    }

    private static bool IsLink(string text)
    {
        // A URL with inner whitespace is prose that happens to contain a URL, and Uri would silently accept some of them.
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;
    }

    private static bool IsEmail(string text) => EmailPattern().IsMatch(text);

    // The domain must contain a dot, and '/' and ':' are excluded so that a path such as "a.com/x@b.com" is not an address.
    // Each domain label takes letters (ASCII or not), digits and '-' only, so a character that follows an address, such
    // as '?' or '>', makes the text something else rather than being absorbed into the domain.
    [GeneratedRegex(@"^(?:mailto:)?[^\s@,/:]+@[\p{L}0-9-]+(?:\.[\p{L}0-9-]+)+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
