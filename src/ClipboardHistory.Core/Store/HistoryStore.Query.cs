using Microsoft.Data.Sqlite;
using Waigoma.ClipboardHistory.Search;

namespace Waigoma.ClipboardHistory.Store;

// Search text and the kind filter. Null Kind means every kind; an empty Search means no text filter.
public sealed record HistoryQuery(string Search, EntryKind? Kind, int Offset, int Limit);

public sealed partial class HistoryStore
{
    // body, ocr, qr and search are left out on purpose: listing a long history must not read them.
    // HasOcr and HasQr are answered by expressions over ocr and qr, which keeps the row small.
    private const string SummaryColumns =
        "id, kind, preview, name, source, pinned, created_at, last_copied_at, width, height, byte_size, char_count, line_count, image_state, " +
        "COALESCE(length(ocr), 0) > 0, COALESCE(length(qr), 0) > 0, thumb";

    // Pinned entries come first, then each group by last_copied_at descending, with id descending as the tie-break.
    public IReadOnlyList<EntrySummary> Query(HistoryQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentOutOfRangeException.ThrowIfNegative(q.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(q.Limit);

        var where = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        IReadOnlyList<string> terms = LikePattern.Build(q.Search);
        for (int i = 0; i < terms.Count; i++)
        {
            // Terms are ANDed; the backslash is the escape character that LikePattern uses.
            where.Add($"search LIKE @term{i} ESCAPE '\\'");
            parameters.Add(($"@term{i}", terms[i]));
        }

        if (q.Kind is { } kind)
        {
            where.Add("kind = @kind");
            parameters.Add(("@kind", (long)kind));
        }

        string sql = $"SELECT {SummaryColumns} FROM entries"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
            + " ORDER BY pinned DESC, last_copied_at DESC, id DESC LIMIT @limit OFFSET @offset";
        parameters.Add(("@limit", (long)q.Limit));
        parameters.Add(("@offset", (long)q.Offset));

        var result = new List<EntrySummary>();
        lock (_db.Gate)
        {
            using SqliteCommand command = _db.Command(sql, parameters.ToArray());
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadSummary(reader));
            }
        }

        return result;
    }

    // Ids in recency order without the pinned-first rule, for callers that only need the order of the newest rows.
    public IReadOnlyList<long> IdsNewestFirst(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var ids = new List<long>();
        lock (_db.Gate)
        {
            using SqliteCommand command = _db.Command(
                "SELECT id FROM entries ORDER BY last_copied_at DESC, id DESC LIMIT @limit", ("@limit", (long)limit));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        return ids;
    }

    public bool Exists(long id)
    {
        lock (_db.Gate)
        {
            using SqliteCommand command = _db.Command("SELECT EXISTS(SELECT 1 FROM entries WHERE id = @id)", ("@id", id));
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
    }

    // Reads the columns named by SummaryColumns, in that order.
    private EntrySummary ReadSummary(SqliteDataReader reader)
    {
        string? thumb = NullableString(reader, 16);
        return new EntrySummary(
            reader.GetInt64(0),
            (EntryKind)reader.GetInt32(1),
            reader.GetString(2),
            NullableString(reader, 3),
            NullableString(reader, 4),
            reader.GetInt64(5) != 0,
            FromUnixMilliseconds(reader.GetInt64(6)),
            FromUnixMilliseconds(reader.GetInt64(7)),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt64(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            (ImageState)reader.GetInt32(13),
            reader.GetInt64(14) != 0,
            reader.GetInt64(15) != 0,
            thumb is null ? null : Path.Combine(_paths.Thumbs, thumb));
    }
}
