namespace Waigoma.ClipboardHistory.Capture;

// One item from the system clipboard history. Text and Files are independent so that a source can report either.
public sealed record BackfillItem(string? Text, IReadOnlyList<string>? Files, DateTimeOffset CopiedAt);

// Lets the service recover copies that happened while the app was not running.
public interface IBackfillSource
{
    IReadOnlyList<BackfillItem> ReadNewerThan(DateTimeOffset since);
}
