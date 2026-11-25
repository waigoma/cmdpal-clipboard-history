using Waigoma.ClipboardHistory.Paste;

namespace Waigoma.ClipboardHistory.Sequence;

// One step of the sequential paste. The position moves only after Sent, so a paste that failed or was refused is
// retried on the same entry. Start and done are split because the paste finishes on a background thread.
public sealed class SequentialPasteRunner
{
    private readonly SequentialPaste _sequence;
    private readonly Func<IReadOnlyList<long>> _loadIds;
    private readonly Func<long, bool> _exists;
    private readonly Func<long, EntryDetail?> _load;
    private readonly Func<EntryDetail, bool, ClipboardPayload?> _buildPayload;
    private readonly Func<ClipboardPayload, Action<PasteOutcome>, PasteOutcome?> _begin;
    private readonly Func<bool> _appendNewline;

    // Size of the id list the current session was started from, which bounds how many entries one run may skip.
    private int _snapshotSize;

    public SequentialPasteRunner(
        SequentialPaste sequence,
        Func<IReadOnlyList<long>> loadIds,
        Func<long, bool> exists,
        Func<long, EntryDetail?> load,
        Func<EntryDetail, bool, ClipboardPayload?> buildPayload,
        Func<ClipboardPayload, Action<PasteOutcome>, PasteOutcome?> begin,
        Func<bool> appendNewline)
    {
        _sequence = sequence;
        _loadIds = loadIds;
        _exists = exists;
        _load = load;
        _buildPayload = buildPayload;
        _begin = begin;
        _appendNewline = appendNewline;
    }

    // Returns null when a paste started or when there is nothing left to paste. A refusal is returned as is, and a
    // failure of the run itself is logged and reported as SendFailed, so no exception leaves this method.
    public PasteOutcome? Run()
    {
        try
        {
            return RunStep();
        }
        catch (Exception ex)
        {
            Log.Error("SequentialPasteRunner.Run", ex);
            return PasteOutcome.SendFailed;
        }
    }

    private PasteOutcome? RunStep()
    {
        int skips = 0;
        while (true)
        {
            long? id = _sequence.Peek(LoadIds, _exists);
            if (id is null)
            {
                return null;
            }

            EntryDetail? entry = _load(id.Value);
            ClipboardPayload? payload = entry is null ? null : _buildPayload(entry, _appendNewline());
            if (payload is null)
            {
                // Advancing past an entry that cannot be pasted keeps the walk moving. A session cannot hold more
                // entries than its snapshot, so the counter only guards against a list that keeps changing.
                _sequence.Advance();
                if (++skips > _snapshotSize)
                {
                    return null;
                }

                continue;
            }

            return _begin(payload, OnDone);
        }
    }

    private IReadOnlyList<long> LoadIds()
    {
        IReadOnlyList<long> ids = _loadIds();
        _snapshotSize = ids.Count;
        return ids;
    }

    // Runs on the paste's background thread. Advance after a Reset in the meantime does nothing, because the sequence
    // then has no position to move.
    private void OnDone(PasteOutcome outcome)
    {
        if (outcome == PasteOutcome.Sent)
        {
            _sequence.Advance();
        }
    }
}

// The payload for one step of a sequential paste. A text entry gets a CRLF after its text when the setting is on, and
// keeps only the text so the newline lands after it in the target. Everything else is pasted as a full paste.
public static class SequentialPayload
{
    public static ClipboardPayload? Build(EntryDetail e, bool appendNewline, IDibProvider dib)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!appendNewline || !IsTextKind(e.Summary.Kind))
        {
            return PayloadFactory.Full(e, dib);
        }

        return string.IsNullOrEmpty(e.Body) ? null : new ClipboardPayload { Text = e.Body + "\r\n" };
    }

    private static bool IsTextKind(EntryKind kind) =>
        kind is EntryKind.Text or EntryKind.Link or EntryKind.Email or EntryKind.Color;
}
