namespace Waigoma.ClipboardHistory.Presentation;

public enum EntryAction
{
    Paste,
    Copy,
    PastePlain,
    PasteFormat,
    Edit,
    Rename,
    TogglePin,
    CopyOcr,
    CopyQr,
    Open,
    OpenLocation,
    ExcludeApp,
    Delete,
    BulkDelete,
    DeleteAll,
}

// The actions offered for one entry, in display order. The first is the Enter action; the second is Ctrl+Enter.
public static class EntryActions
{
    public static IReadOnlyList<EntryAction> For(EntrySummary e, bool pasteIsPrimary, int formatCount)
    {
        ArgumentNullException.ThrowIfNull(e);

        bool text = IsTextKind(e.Kind);
        bool image = e.Kind == EntryKind.Image;
        bool files = e.Kind == EntryKind.Files;

        var actions = new List<EntryAction>(15)
        {
            pasteIsPrimary ? EntryAction.Paste : EntryAction.Copy,
            pasteIsPrimary ? EntryAction.Copy : EntryAction.Paste,
        };

        if (text || files)
        {
            actions.Add(EntryAction.PastePlain);
        }

        if (formatCount >= 2)
        {
            actions.Add(EntryAction.PasteFormat);
        }

        if (text)
        {
            actions.Add(EntryAction.Edit);
        }

        actions.Add(EntryAction.Rename);
        actions.Add(EntryAction.TogglePin);

        if (image)
        {
            actions.Add(EntryAction.CopyOcr);
            if (e.HasQr)
            {
                actions.Add(EntryAction.CopyQr);
            }
        }

        if (e.Kind is EntryKind.Link or EntryKind.Email or EntryKind.Image or EntryKind.Files)
        {
            actions.Add(EntryAction.Open);
        }

        if (image || files)
        {
            actions.Add(EntryAction.OpenLocation);
        }

        // Without a known source app there is no executable name to exclude.
        if (!string.IsNullOrWhiteSpace(e.SourceApp))
        {
            actions.Add(EntryAction.ExcludeApp);
        }

        actions.Add(EntryAction.Delete);
        actions.Add(EntryAction.BulkDelete);
        actions.Add(EntryAction.DeleteAll);
        return actions;
    }

    private static bool IsTextKind(EntryKind kind) => kind is not (EntryKind.Image or EntryKind.Files);
}
