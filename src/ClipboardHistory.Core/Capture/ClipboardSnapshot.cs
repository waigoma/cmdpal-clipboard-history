namespace Waigoma.ClipboardHistory.Capture;

// One clipboard change, read once so that filtering and building see the same data.
public sealed record ClipboardSnapshot(uint Sequence, string? SourceExePath, bool IsOwnWrite,
    bool ExcludeMonitor, bool ViewerIgnore, int? CanIncludeInHistory,
    string? Text, IReadOnlyList<string>? Files, IReadOnlyList<ClipFormat> Formats);
