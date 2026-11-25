using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Paste;

namespace Waigoma.ClipboardHistory.Extension;

// Puts an entry on the clipboard without pasting it, and keeps the palette open to confirm.
internal sealed partial class CopyCommand : EntryCommand
{
    internal CopyCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    protected override CommandResult Run() => Copy(Host, EntryId);

    internal static CommandResult Copy(AppHost host, long entryId)
    {
        EntryDetail? entry = host.Store.Get(entryId);
        ClipboardPayload? payload = entry is null ? null : PasteCommand.PayloadFor(host, entry, PasteKind.Full, format: null);
        if (payload is null)
        {
            return EntryResults.SourceMissing();
        }

        PasteOutcome? outcome = host.Paste.Copy(payload);
        if (outcome is null && host.Options().MoveToTop)
        {
            host.Store.Touch(entryId);
        }

        return EntryResults.ForWrite(outcome, EntryMessages.Copied);
    }
}
