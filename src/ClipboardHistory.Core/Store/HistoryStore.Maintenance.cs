using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Waigoma.ClipboardHistory.Capture;

namespace Waigoma.ClipboardHistory.Store;

// Every method here is one transaction, and Changed fires only when the call actually changed a row.
public sealed partial class HistoryStore
{
    // Lets the worker process and other writers announce changes that this process did not make itself.
    public void NotifyChanged() => RaiseChanged();

    // Asks SQLite to give back its page cache. The lock keeps it from running inside another statement.
    public void ReleaseMemory()
    {
        lock (_db.Gate)
        {
            using SqliteCommand command = _db.Command("PRAGMA shrink_memory");
            command.ExecuteNonQuery();
        }
    }

    public void Touch(long id)
    {
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        bool changed;
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            // The timestamp never moves backwards, so a row already at or past now is not a change.
            changed = ExecuteWrite(
                "UPDATE entries SET last_copied_at = @now WHERE id = @id AND last_copied_at < @now",
                ("@now", now), ("@id", id)) > 0;
            transaction.Commit();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void SetPinned(long id, bool pinned)
    {
        long value = pinned ? 1 : 0;
        bool changed;
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            changed = ExecuteWrite(
                "UPDATE entries SET pinned = @pinned WHERE id = @id AND pinned <> @pinned",
                ("@pinned", value), ("@id", id)) > 0;
            transaction.Commit();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    // Names are trimmed, and a blank or null name clears the name. The search column is rebuilt because it holds the name.
    public void Rename(long id, string? name)
    {
        string? trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        bool changed = false;
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            SearchParts? row = ReadSearchParts(id);
            if (row is { } parts && parts.Name != trimmed)
            {
                ExecuteWrite(
                    "UPDATE entries SET name = @name, search = @search WHERE id = @id",
                    ("@name", trimmed), ("@search", BuildSearch(parts.Kind, parts.Body, trimmed, parts.Ocr, parts.Qr)), ("@id", id));
                changed = true;
            }

            transaction.Commit();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    // Replaces the text of a text-like entry and returns the id that survives. When another entry already has the
    // same kind and hash, that entry is merged in and removed, so the edited id is the one that remains.
    public long EditText(long id, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Text must not be empty.", nameof(text));
        }

        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            EditRow row = ReadEditRow(
                "SELECT id, kind, name, pinned, last_copied_at FROM entries WHERE id = @id", ("@id", id))
                ?? throw new KeyNotFoundException("No history entry has the given id.");
            if (row.Kind is EntryKind.Image or EntryKind.Files)
            {
                throw new InvalidOperationException("Images and file lists cannot be edited as text.");
            }

            EntryKind kind = ContentClassifier.ClassifyText(text);
            string hash = ContentHasher.ForText(text);
            long lastCopiedAt = row.LastCopiedAt;
            string? name = row.Name;
            bool pinned = row.Pinned;

            EditRow? duplicate = ReadEditRow(
                "SELECT id, kind, name, pinned, last_copied_at FROM entries WHERE kind = @kind AND hash = @hash AND id <> @id",
                ("@kind", (long)kind), ("@hash", hash), ("@id", id));
            if (duplicate is { } other)
            {
                // Removed first, because the UNIQUE(kind, hash) constraint would reject the edited row otherwise.
                ExecuteWrite("DELETE FROM entries WHERE id = @id", ("@id", other.Id));
                lastCopiedAt = Math.Max(lastCopiedAt, other.LastCopiedAt);
                name ??= other.Name;
                pinned |= other.Pinned;
            }

            ExecuteWrite(
                "UPDATE entries SET kind = @kind, hash = @hash, body = @body, preview = @preview, char_count = @chars, " +
                "line_count = @lines, byte_size = @bytes, search = @search, name = @name, pinned = @pinned, " +
                "last_copied_at = @copied, image_state = @state, thumb = NULL, ocr = NULL, qr = NULL WHERE id = @id",
                ("@kind", (long)kind),
                ("@hash", hash),
                ("@body", text),
                ("@preview", TextPreview(text)),
                ("@chars", (long)text.Length),
                ("@lines", (long)CountLines(text)),
                ("@bytes", (long)Encoding.UTF8.GetByteCount(text)),
                ("@search", BuildSearch(kind, text, name, null, null)),
                ("@name", name),
                ("@pinned", pinned ? 1L : 0L),
                ("@copied", lastCopiedAt),
                ("@state", (long)ImageState.None),
                ("@id", id));
            ExecuteWrite("DELETE FROM formats WHERE entry_id = @id", ("@id", id));
            ExecuteWrite("DELETE FROM jobs WHERE entry_id = @id", ("@id", id));
            transaction.Commit();
        }

        RaiseChanged();
        return id;
    }

    // Deleting an entry leaves its blob files in place; the sweeper removes them once nothing refers to them.
    public void Delete(long id) => DeleteEntries("DELETE FROM entries WHERE id = @id", ("@id", id));

    // Pinned entries are kept by every bulk delete and expiry.
    public int DeleteSince(DateTimeOffset since) =>
        DeleteEntries("DELETE FROM entries WHERE last_copied_at >= @since AND pinned = 0", ("@since", since.ToUnixTimeMilliseconds()));

    public int DeleteAll() => DeleteEntries("DELETE FROM entries WHERE pinned = 0");

    public int Expire(DateTimeOffset cutoff) =>
        DeleteEntries("DELETE FROM entries WHERE last_copied_at < @cutoff AND pinned = 0", ("@cutoff", cutoff.ToUnixTimeMilliseconds()));

    public long TotalBytes()
    {
        lock (_db.Gate)
        {
            return Convert.ToInt64(ScalarValue("SELECT COALESCE(SUM(byte_size), 0) FROM entries"), CultureInfo.InvariantCulture);
        }
    }

    // Removes the oldest unpinned entries until the total is within the limit. If pins alone exceed it, nothing is removed.
    public int EnforceTotalLimit(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        int removed = 0;
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            // The total is read once and then tracked by subtraction, so the loop costs one scan per deleted row.
            long total = Convert.ToInt64(ScalarValue("SELECT COALESCE(SUM(byte_size), 0) FROM entries"), CultureInfo.InvariantCulture);
            // Pins that alone reach the limit cannot be satisfied by trimming, so nothing is removed in that case.
            long pinnedBytes = Convert.ToInt64(
                ScalarValue("SELECT COALESCE(SUM(byte_size), 0) FROM entries WHERE pinned = 1"), CultureInfo.InvariantCulture);
            while (pinnedBytes < maxBytes && total > maxBytes)
            {
                (long Id, long ByteSize)? oldest = ReadOldestUnpinned();
                if (oldest is not { } row)
                {
                    break;
                }

                ExecuteWrite("DELETE FROM entries WHERE id = @id", ("@id", row.Id));
                total -= row.ByteSize;
                removed++;
            }

            transaction.Commit();
        }

        if (removed > 0)
        {
            RaiseChanged();
        }

        return removed;
    }

    // Deletes files that no row refers to and that are older than minAge. The database lock is held for the whole
    // sweep, so a Save in this process cannot slip a blob in between the reference check and the delete.
    // Changed is not raised: removing unreferenced files is invisible to the list.
    public int SweepFiles(TimeSpan minAge)
    {
        DateTime cutoff = _clock.GetUtcNow().UtcDateTime - minAge;
        lock (_db.Gate)
        {
            HashSet<string> files = ReadNames("SELECT file FROM formats");
            HashSet<string> thumbs = ReadNames("SELECT thumb FROM entries WHERE thumb IS NOT NULL");
            int removed = SweepDirectory(_paths.Files, files, cutoff);
            removed += SweepDirectory(_paths.Thumbs, thumbs, cutoff);
            // Everything in tmp is a leftover from an interrupted save, so age is the only test there.
            removed += SweepDirectory(_paths.Temp, referenced: null, cutoff);
            return removed;
        }
    }

    private sealed record EditRow(long Id, EntryKind Kind, string? Name, bool Pinned, long LastCopiedAt);

    private sealed record SearchParts(EntryKind Kind, string? Name, string? Body, string? Ocr, string? Qr);

    private int DeleteEntries(string sql, params (string Name, object? Value)[] parameters)
    {
        int count;
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            count = ExecuteWrite(sql, parameters);
            transaction.Commit();
        }

        if (count > 0)
        {
            RaiseChanged();
        }

        return count;
    }

    private int ExecuteWrite(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = _db.Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private object? ScalarValue(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = _db.Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private EditRow? ReadEditRow(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = _db.Command(sql, parameters);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new EditRow(
            reader.GetInt64(0),
            (EntryKind)reader.GetInt32(1),
            NullableString(reader, 2),
            reader.GetInt64(3) != 0,
            reader.GetInt64(4));
    }

    // The newest row is excluded so a copy the user has just made is never trimmed, even when it alone exceeds the limit.
    private (long Id, long ByteSize)? ReadOldestUnpinned()
    {
        using SqliteCommand command = _db.Command(
            "SELECT id, byte_size FROM entries WHERE pinned = 0 " +
            "AND id <> (SELECT id FROM entries ORDER BY last_copied_at DESC, id DESC LIMIT 1) " +
            "ORDER BY last_copied_at ASC, id ASC LIMIT 1");
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private SearchParts? ReadSearchParts(long id)
    {
        using SqliteCommand command = _db.Command(
            "SELECT kind, name, body, ocr, qr FROM entries WHERE id = @id", ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SearchParts(
            (EntryKind)reader.GetInt32(0),
            NullableString(reader, 1),
            NullableString(reader, 2),
            NullableString(reader, 3),
            NullableString(reader, 4));
    }

    private HashSet<string> ReadNames(string sql)
    {
        // Windows file names compare case-insensitively, so the reference check does too.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand command = _db.Command(sql);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static int SweepDirectory(string directory, ISet<string>? referenced, DateTime cutoff)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("HistoryStore.SweepFiles", ex);
            return 0;
        }

        int removed = 0;
        foreach (string path in paths)
        {
            // One file that cannot be read or deleted must not stop the rest of the sweep, and the path itself is never logged.
            try
            {
                if (referenced is not null && referenced.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(path) >= cutoff)
                {
                    continue;
                }

                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("HistoryStore.SweepFiles", ex);
            }
        }

        return removed;
    }
}
