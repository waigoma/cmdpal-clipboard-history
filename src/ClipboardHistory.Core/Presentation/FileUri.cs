using System.Text;

namespace Waigoma.ClipboardHistory.Presentation;

// Builds the target of a markdown image for a local file. The result is placed inside (...), so every character that
// would end or confuse that target must be encoded.
public static class FileUri
{
    private const string Prefix = "file:///";

    public static string ToMarkdownTarget(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Each segment is encoded on its own. Uri would keep an existing "%xx" as an escape, so a literal "%20" in a
        // file name would point at a different file; EscapeDataString encodes the '%' itself.
        var sb = new StringBuilder(Prefix);
        string[] segments = path.Split('\\');
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('/');
            }

            // The drive segment ("C:") stays as it is; encoding its colon would break the drive.
            sb.Append(i == 0 && segments[i].EndsWith(':') ? segments[i] : Uri.EscapeDataString(segments[i]));
        }

        return sb.ToString();
    }
}
