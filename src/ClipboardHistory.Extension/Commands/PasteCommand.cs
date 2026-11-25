using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Paste;

namespace Waigoma.ClipboardHistory.Extension;

// What a paste puts on the clipboard. Full keeps every stored format, unless the plain-text preference applies.
internal enum PasteKind
{
    Full,
    Plain,
    Format,
}

// Pastes one entry into the last target window. The paste itself runs in the background; this command only starts it
// and reports whether it could start.
internal sealed partial class PasteCommand : EntryCommand
{
    private readonly PasteKind _kind;
    private readonly string? _format;

    internal PasteCommand(AppHost host, long entryId, PasteKind kind, string? format, string name, string id)
        : base(host, entryId, name, id)
    {
        _kind = kind;
        _format = format;
    }

    protected override CommandResult Run() => Paste(Host, EntryId, _kind, _format);

    // Whether a paste started, and if not, the line to show. A null message means there is nothing to say.
    internal readonly record struct PasteStart(bool Started, string? Message);

    // Shared with the edit page's "save and paste", so both paths report the same outcomes.
    internal static CommandResult Paste(AppHost host, long entryId, PasteKind kind, string? format = null)
    {
        PasteStart start = Start(host, entryId, kind, format);
        if (start.Started)
        {
            return CommandResult.Hide();
        }

        return start.Message is null ? CommandResult.KeepOpen() : EntryResults.Toast(start.Message);
    }

    internal static PasteStart Start(AppHost host, long entryId, PasteKind kind, string? format)
    {
        EntryDetail? entry = host.Store.Get(entryId);
        ClipboardPayload? payload = entry is null ? null : PayloadFor(host, entry, kind, format);
        if (payload is null)
        {
            return new PasteStart(false, PasteNotice.TextOf(PasteOutcome.SourceMissing) ?? EntryMessages.OperationFailed);
        }

        PasteOutcome? immediate = host.Paste.Begin(payload, fromHotkey: false, outcome => OnDone(host, outcome));
        if (immediate is null)
        {
            // The paste runs in the background. The entry is touched once it starts, since the user chose it; a failure
            // later is reported through the notice instead.
            if (host.Options().MoveToTop)
            {
                host.Store.Touch(entryId);
            }

            return new PasteStart(true, null);
        }

        // Busy means another paste is still in flight; the user is still looking at the list, so stay silent.
        if (immediate == PasteOutcome.Busy)
        {
            return new PasteStart(false, null);
        }

        return new PasteStart(false, PasteNotice.TextOf(immediate.Value) ?? EntryMessages.OperationFailed);
    }

    // Built from the stored entry. The plain-text preference applies to text entries only: a file list pasted as
    // its paths would no longer be files, and an image has no text form.
    internal static ClipboardPayload? PayloadFor(AppHost host, EntryDetail entry, PasteKind kind, string? format)
    {
        switch (kind)
        {
            case PasteKind.Plain:
                return PayloadFactory.Plain(entry);
            case PasteKind.Format:
                return PayloadFactory.Single(entry, format ?? string.Empty, host.Dib);
            default:
                bool isText = entry.Summary.Kind is not (EntryKind.Image or EntryKind.Files);
                if (isText && host.Options().PreferPlainText)
                {
                    ClipboardPayload? plain = PayloadFactory.Plain(entry);
                    if (plain is not null)
                    {
                        return plain;
                    }
                }

                return PayloadFactory.Full(entry, host.Dib);
        }
    }

    // Runs on the paste's background thread. Sent is the normal case; everything else is shown on the list.
    private static void OnDone(AppHost host, PasteOutcome outcome)
    {
        try
        {
            if (outcome == PasteOutcome.Busy)
            {
                return;
            }

            // A paste that landed clears the notice of an earlier failure, so the list does not show a stale one.
            if (outcome == PasteOutcome.Sent)
            {
                host.Notice.Clear();
            }
            else
            {
                host.Notice.Set(outcome);
            }

            host.RefreshRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("PasteCommand.OnDone", ex);
        }
    }
}
