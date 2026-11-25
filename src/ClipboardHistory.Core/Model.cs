namespace Waigoma.ClipboardHistory;

public enum EntryKind { Text = 0, Link = 1, Email = 2, Color = 3, Image = 4, Files = 5 }

public static class FormatNames
{
    public const string Html = "HTML Format";
    public const string Rtf = "Rich Text Format";
    public const string Png = "PNG";
    public const string Dib = "CF_DIB";
    public const string DibV5 = "CF_DIBV5";
}

public sealed record ClipFormat(string Name, byte[] Data);

// Files stores its Body as the paths joined by '\n'.
public sealed record CapturedEntry(EntryKind Kind, string? Body, IReadOnlyList<ClipFormat> Formats,
    string Hash, string? SourceApp, DateTimeOffset CopiedAt);

public enum ImageState { None = 0, Pending = 1, Done = 2, Failed = 3 }

public sealed record EntrySummary(long Id, EntryKind Kind, string Preview, string? Name, string? SourceApp,
    bool Pinned, DateTimeOffset CreatedAt, DateTimeOffset LastCopiedAt, int Width, int Height,
    long ByteSize, int CharCount, int LineCount, ImageState ImageState, bool HasOcr, bool HasQr,
    string? ThumbPath);

public sealed record StoredFormat(string Name, string FilePath, long Size);

public sealed record EntryDetail(EntrySummary Summary, string? Body, IReadOnlyList<StoredFormat> Formats,
    string? Ocr, string? Qr);
