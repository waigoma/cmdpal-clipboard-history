namespace Waigoma.ClipboardHistory.Sequence;

// Walks the history newest-first for repeated paste. The id list is taken once when a session starts, so entries
// copied mid-sequence do not shift what the next press pastes. An idle session expires after the timeout.
public sealed class SequentialPaste
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan> _timeout;

    private IReadOnlyList<long>? _ids;
    private int _position;
    private int _peeked = -1;
    private DateTimeOffset _lastActivity;

    public SequentialPaste(TimeProvider clock, Func<TimeSpan> timeout)
    {
        _clock = clock;
        _timeout = timeout;
    }

    // Callbacks run under the lock, so they must not call back into this instance.
    public long? Peek(Func<IReadOnlyList<long>> loadIds, Func<long, bool> exists)
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            if (_ids is null || now - _lastActivity > _timeout())
            {
                // Copied, because the caller's list may be a live view that keeps changing.
                _ids = loadIds().ToArray();
                _position = 0;
                _peeked = -1;
                _lastActivity = now;
            }

            while (_position < _ids.Count)
            {
                long id = _ids[_position];
                if (exists(id))
                {
                    _peeked = _position;
                    return id;
                }

                // A deleted entry is skipped for good, so a later Peek never revisits it.
                _position++;
            }

            _peeked = -1;
            return null;
        }
    }

    public void Advance()
    {
        lock (_gate)
        {
            if (_ids is null || _peeked < 0)
            {
                return;
            }

            _position = _peeked + 1;
            _peeked = -1;
            _lastActivity = _clock.GetUtcNow();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _ids = null;
            _position = 0;
            _peeked = -1;
        }
    }
}
