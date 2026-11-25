using System.Text;

namespace Waigoma.ClipboardHistory.Capture;

public static class CaptureBuilder
{
    // Several encodings of one picture often arrive together; only the richest one is kept.
    private static readonly string[] ImageFormatPriority = { FormatNames.Png, FormatNames.DibV5, FormatNames.Dib };

    public static CapturedEntry? Build(ClipboardSnapshot s, HistoryOptions o, DateTimeOffset now)
    {
        long limit = o.MaxFormatBytes;
        string? sourceApp = string.IsNullOrEmpty(s.SourceExePath) ? null : Path.GetFileName(s.SourceExePath);

        // The primary kind is decided first and an oversize primary drops the whole entry (spec 6.1);
        // falling through to a lower kind would record content the user did not copy as the main item.
        if (s.Files is { Count: > 0 } files)
        {
            string body = string.Join('\n', files);
            if (Utf8Size(body) > limit)
            {
                return null;
            }

            return new CapturedEntry(EntryKind.Files, body, Array.Empty<ClipFormat>(),
                ContentHasher.ForFiles(files), sourceApp, now);
        }

        string? text = s.Text;
        if (!string.IsNullOrEmpty(text))
        {
            if (Utf8Size(text) > limit)
            {
                return null;
            }

            return new CapturedEntry(ContentClassifier.ClassifyText(text), text, KeepFormats(s.Formats, limit),
                ContentHasher.ForText(text), sourceApp, now);
        }

        // Within an image, an oversize format is dropped and the next fitting one becomes primary.
        ClipFormat? image = PickImage(s.Formats, limit);
        if (image is null)
        {
            return null;
        }

        return new CapturedEntry(EntryKind.Image, null, KeepFormats(s.Formats, limit),
            ContentHasher.ForBytes(image.Data), sourceApp, now);
    }

    private static List<ClipFormat> KeepFormats(IReadOnlyList<ClipFormat> formats, long limit)
    {
        var kept = new List<ClipFormat>(3);
        ClipFormat? image = PickImage(formats, limit);
        if (image is not null)
        {
            kept.Add(image);
        }

        ClipFormat? html = FirstFitting(formats, FormatNames.Html, limit);
        if (html is not null)
        {
            kept.Add(html);
        }

        ClipFormat? rtf = FirstFitting(formats, FormatNames.Rtf, limit);
        if (rtf is not null)
        {
            kept.Add(rtf);
        }

        return kept;
    }

    private static ClipFormat? PickImage(IReadOnlyList<ClipFormat> formats, long limit)
    {
        foreach (string name in ImageFormatPriority)
        {
            ClipFormat? found = FirstFitting(formats, name, limit);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static ClipFormat? FirstFitting(IReadOnlyList<ClipFormat> formats, string name, long limit)
    {
        foreach (ClipFormat format in formats)
        {
            if (format.Name == name && format.Data.Length <= limit)
            {
                return format;
            }
        }

        return null;
    }

    private static long Utf8Size(string text) => Encoding.UTF8.GetByteCount(text);
}
