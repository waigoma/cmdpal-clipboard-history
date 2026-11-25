using System.Collections.Frozen;

namespace Waigoma.ClipboardHistory;

// Defaults follow spec section 6.7.
public sealed record HistoryOptions
{
    public TimeSpan? Retention { get; init; } = TimeSpan.FromDays(90);
    public bool PasteIsPrimary { get; init; } = true;
    public bool PreferPlainText { get; init; }
    public bool MoveToTop { get; init; } = true;
    public bool Ocr { get; init; } = true;

    // File names only, lower-cased by the caller.
    public IReadOnlySet<string> ExcludedApps { get; init; } = FrozenSet<string>.Empty;

    public TimeSpan SequenceTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public bool SequenceNewline { get; init; } = true;
    public long MaxFormatBytes { get; init; } = 50L * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 2048L * 1024 * 1024;
}
