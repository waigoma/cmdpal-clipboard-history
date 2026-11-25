namespace Waigoma.ClipboardHistory.Paste;

// Every field is optional so one payload can carry any mix of formats; the clipboard writer decides how to store them.
public sealed record ClipboardPayload
{
    public string? Text { get; init; }

    public byte[]? Html { get; init; }

    public byte[]? Rtf { get; init; }

    public IReadOnlyList<string>? Files { get; init; }

    public string? PngPath { get; init; }

    public string? DibPath { get; init; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(Text)
        && (Html is null || Html.Length == 0)
        && (Rtf is null || Rtf.Length == 0)
        && (Files is null || Files.Count == 0)
        && string.IsNullOrEmpty(PngPath)
        && string.IsNullOrEmpty(DibPath);
}
