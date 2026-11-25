namespace Waigoma.ClipboardHistory.Paste;

// Turns a stored entry into clipboard payloads. Blobs can disappear while the history is open (cleanup, or a
// user deleting files), so every stored format is checked on disk and a missing one is dropped, not fatal.
public static class PayloadFactory
{
    private const string TextFormat = "Text";
    private const string HtmlFormat = "HTML";
    private const string RtfFormat = "RTF";
    private const string ImageFormat = "画像";
    private const string FilesFormat = "ファイル";
    private const string PathFormat = "パス";

    public static ClipboardPayload? Full(EntryDetail entry, IDibProvider dib)
    {
        EntryKind kind = entry.Summary.Kind;
        if (kind == EntryKind.Image)
        {
            return NonEmpty(ImageFields(entry, dib));
        }

        if (kind == EntryKind.Files)
        {
            return NonEmpty(FilePayload(entry));
        }

        // An image attached to text is pasted as PNG only: a DIB would make plain-text targets show the image.
        ClipboardPayload? image = ImageFields(entry, dib);
        return NonEmpty(new ClipboardPayload
        {
            Text = NonEmptyText(entry.Body),
            Html = ReadFormat(entry, FormatNames.Html),
            Rtf = ReadFormat(entry, FormatNames.Rtf),
            PngPath = image?.PngPath,
        });
    }

    // Plain-text paste: Files entries become their newline-separated paths, images have no text form.
    public static ClipboardPayload? Plain(EntryDetail entry)
    {
        if (entry.Summary.Kind == EntryKind.Image)
        {
            return null;
        }

        return NonEmpty(new ClipboardPayload { Text = NonEmptyText(entry.Body) });
    }

    public static ClipboardPayload? Single(EntryDetail entry, string formatName, IDibProvider dib)
    {
        bool isText = IsTextKind(entry.Summary.Kind);
        switch (formatName)
        {
            case TextFormat:
                return isText ? NonEmpty(new ClipboardPayload { Text = NonEmptyText(entry.Body) }) : null;
            case HtmlFormat:
                return isText ? NonEmpty(new ClipboardPayload { Html = ReadFormat(entry, FormatNames.Html) }) : null;
            case RtfFormat:
                return isText ? NonEmpty(new ClipboardPayload { Rtf = ReadFormat(entry, FormatNames.Rtf) }) : null;
            case ImageFormat:
                return NonEmpty(ImageFields(entry, dib));
            case FilesFormat:
                return entry.Summary.Kind == EntryKind.Files ? NonEmpty(FilePayload(entry)) : null;
            case PathFormat:
                return entry.Summary.Kind == EntryKind.Files
                    ? NonEmpty(new ClipboardPayload { Text = NonEmptyText(entry.Body) })
                    : null;
            default:
                return null;
        }
    }

    public static IReadOnlyList<string> AvailableFormats(EntryDetail entry)
    {
        var formats = new List<string>();
        EntryKind kind = entry.Summary.Kind;

        if (kind == EntryKind.Files)
        {
            if (NonEmptyText(entry.Body) is not null)
            {
                formats.Add(FilesFormat);
                formats.Add(PathFormat);
            }

            return formats;
        }

        if (kind != EntryKind.Image)
        {
            if (NonEmptyText(entry.Body) is not null)
            {
                formats.Add(TextFormat);
            }

            if (ExistingPath(entry, FormatNames.Html) is not null)
            {
                formats.Add(HtmlFormat);
            }

            if (ExistingPath(entry, FormatNames.Rtf) is not null)
            {
                formats.Add(RtfFormat);
            }
        }

        if (HasImageData(entry))
        {
            formats.Add(ImageFormat);
        }

        return formats;
    }

    private static bool IsTextKind(EntryKind kind) => kind is not (EntryKind.Image or EntryKind.Files);

    private static bool HasImageData(EntryDetail entry)
    {
        if (ExistingPath(entry, FormatNames.Png) is not null)
        {
            return true;
        }

        return entry.Summary.Kind == EntryKind.Image
            && (ExistingPath(entry, FormatNames.DibV5) ?? ExistingPath(entry, FormatNames.Dib)) is not null;
    }

    // Image entries get a DIB from the provider when a PNG exists; text entries never get one.
    private static ClipboardPayload? ImageFields(EntryDetail entry, IDibProvider dib)
    {
        bool isImageEntry = entry.Summary.Kind == EntryKind.Image;
        string? png = ExistingPath(entry, FormatNames.Png);
        if (png is not null)
        {
            return new ClipboardPayload
            {
                PngPath = png,
                DibPath = isImageEntry ? CreateDib(dib, png) : null,
            };
        }

        if (!isImageEntry)
        {
            return null;
        }

        // Without a PNG the stored DIB is used as is, so there is no conversion step to fail.
        string? dibPath = ExistingPath(entry, FormatNames.DibV5) ?? ExistingPath(entry, FormatNames.Dib);
        return dibPath is null ? null : new ClipboardPayload { DibPath = dibPath };
    }

    private static ClipboardPayload? FilePayload(EntryDetail entry)
    {
        string? body = NonEmptyText(entry.Body);
        if (body is null)
        {
            return null;
        }

        // A file deleted since the copy would make the whole paste fail, so the missing local paths are dropped.
        List<string> paths = body.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && IsStillThere(line))
            .ToList();
        return paths.Count == 0 ? null : new ClipboardPayload { Files = paths };
    }

    // Network paths are kept without a check: a share may be offline now and back later, and checking would drop it.
    private static bool IsStillThere(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return File.Exists(path) || Directory.Exists(path) || IsNetworkDrive(path);
    }

    private static bool IsNetworkDrive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? CreateDib(IDibProvider dib, string pngPath)
    {
        try
        {
            return dib.TryCreate(pngPath);
        }
        catch (Exception ex)
        {
            Log.Error("paste.dib", ex);
            return null;
        }
    }

    private static byte[]? ReadFormat(EntryDetail entry, string formatName)
    {
        string? path = ExistingPath(entry, formatName);
        if (path is null)
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exception message can contain the path, so only its type is logged through Log.
            Log.Error("paste.format.read", ex);
            return null;
        }
    }

    private static string? ExistingPath(EntryDetail entry, string formatName)
    {
        foreach (StoredFormat stored in entry.Formats)
        {
            if (stored.Name == formatName && File.Exists(stored.FilePath))
            {
                return stored.FilePath;
            }
        }

        return null;
    }

    private static string? NonEmptyText(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static ClipboardPayload? NonEmpty(ClipboardPayload? payload)
    {
        return payload is null || payload.IsEmpty ? null : payload;
    }
}
