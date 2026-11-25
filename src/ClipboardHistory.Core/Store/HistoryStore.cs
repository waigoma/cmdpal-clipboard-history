using System.Text;
using Microsoft.Data.Sqlite;
using Waigoma.ClipboardHistory.Search;

namespace Waigoma.ClipboardHistory.Store;

// Every public method takes the one connection's lock. Changed fires after commit and outside the lock,
// so a handler that calls back into the store cannot deadlock.
public sealed partial class HistoryStore : IDisposable
{
    private const int PreviewMaxUnits = 200;

    // Bounds the indexed text so a very large paste does not bloat the search column.
    private const int SearchBodyMaxUnits = 65536;

    private const string ImagePreview = "画像";
    private const string BlankPreview = "(空白)";

    private readonly StorePaths _paths;
    private readonly TimeProvider _clock;
    private readonly Database _db;

    public HistoryStore(StorePaths paths, TimeProvider clock)
    {
        _paths = paths;
        _clock = clock;
        paths.EnsureCreated();
        _db = new Database(paths.Db);
        Blobs = new BlobStore(paths);
        Jobs = new JobQueue(_db, RaiseChanged);
    }

    public BlobStore Blobs { get; }

    public JobQueue Jobs { get; }

    public event Action? Changed;

    public (long Id, bool IsNew) Upsert(CapturedEntry e)
    {
        (long Id, bool IsNew) result;
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            long copiedAt = e.CopiedAt.ToUnixTimeMilliseconds();
            bool carriesImage = HasImageFormat(e.Formats);
            (long Id, ImageState State)? existing = FindEntry(e.Kind, e.Hash);
            if (existing is { } row)
            {
                if (e.Kind == EntryKind.Image && row.State == ImageState.Done)
                {
                    // A processed image already has its formats, OCR and thumbnail, so a re-copy only refreshes the time.
                    Touch(row.Id, copiedAt, e.SourceApp);
                }
                else
                {
                    WriteFormats(row.Id, e.Formats);
                    Touch(row.Id, copiedAt, e.SourceApp);
                    if (carriesImage)
                    {
                        SetImageState(row.Id, ImageState.Pending);
                        JobQueue.EnqueueCore(_db, row.Id);
                    }
                }

                result = (row.Id, false);
            }
            else
            {
                long id = InsertEntry(e, copiedAt, carriesImage);
                WriteFormats(id, e.Formats);
                if (carriesImage)
                {
                    JobQueue.EnqueueCore(_db, id);
                }

                result = (id, true);
            }

            transaction.Commit();
        }

        RaiseChanged();
        return result;
    }

    public void CompleteImage(long id, int width, int height, string? pngFile, string? thumb, string? ocr, string? qr)
    {
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            EntryKind kind;
            string? name;
            string? body;
            using (SqliteCommand select = _db.Command("SELECT kind, name, body FROM entries WHERE id = @id", ("@id", id)))
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                if (!reader.Read())
                {
                    // A job can outlive its entry only through a race, and there is nothing left to complete.
                    return;
                }

                kind = (EntryKind)reader.GetInt32(0);
                name = NullableString(reader, 1);
                body = NullableString(reader, 2);
            }

            if (pngFile is not null)
            {
                using (SqliteCommand drop = _db.Command(
                    "DELETE FROM formats WHERE entry_id = @id AND name IN (@dib, @dibV5)",
                    ("@id", id), ("@dib", FormatNames.Dib), ("@dibV5", FormatNames.DibV5)))
                {
                    drop.ExecuteNonQuery();
                }

                // The PNG is measured from disk because the worker wrote it and only it knows the final size.
                InsertFormat(id, FormatNames.Png, pngFile, new FileInfo(Blobs.FullPath(pngFile)).Length);
            }

            using (SqliteCommand update = _db.Command(
                "UPDATE entries SET width = @width, height = @height, thumb = @thumb, ocr = @ocr, qr = @qr, image_state = @state, search = @search WHERE id = @id",
                ("@width", width), ("@height", height), ("@thumb", thumb), ("@ocr", ocr), ("@qr", qr),
                ("@state", (long)ImageState.Done), ("@search", BuildSearch(kind, body, name, ocr, qr)), ("@id", id)))
            {
                update.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        RaiseChanged();
    }

    public EntryDetail? Get(long id)
    {
        EntrySummary summary;
        string? body;
        string? ocr;
        string? qr;
        var formats = new List<StoredFormat>();
        lock (_db.Gate)
        {
            using (SqliteCommand select = _db.Command(
                "SELECT id, kind, preview, name, source, pinned, created_at, last_copied_at, width, height, byte_size, char_count, line_count, image_state, ocr, qr, thumb, body FROM entries WHERE id = @id",
                ("@id", id)))
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                ocr = NullableString(reader, 14);
                qr = NullableString(reader, 15);
                string? thumb = NullableString(reader, 16);
                body = NullableString(reader, 17);
                summary = new EntrySummary(
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
                    !string.IsNullOrEmpty(ocr),
                    !string.IsNullOrEmpty(qr),
                    thumb is null ? null : Path.Combine(_paths.Thumbs, thumb));
            }

            using SqliteCommand formatQuery = _db.Command(
                "SELECT name, file, size FROM formats WHERE entry_id = @id ORDER BY name", ("@id", id));
            using SqliteDataReader formatReader = formatQuery.ExecuteReader();
            while (formatReader.Read())
            {
                formats.Add(new StoredFormat(formatReader.GetString(0), Path.Combine(_paths.Files, formatReader.GetString(1)), formatReader.GetInt64(2)));
            }
        }

        return new EntryDetail(summary, body, formats, ocr, qr);
    }

    public string? GetMeta(string key)
    {
        lock (_db.Gate)
        {
            using SqliteCommand command = _db.Command("SELECT value FROM meta WHERE key = @key", ("@key", key));
            return command.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value)
    {
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            if (value is null)
            {
                using SqliteCommand delete = _db.Command("DELETE FROM meta WHERE key = @key", ("@key", key));
                delete.ExecuteNonQuery();
            }
            else
            {
                using SqliteCommand upsert = _db.Command(
                    "INSERT OR REPLACE INTO meta(key, value) VALUES(@key, @value)", ("@key", key), ("@value", value));
                upsert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void Dispose()
    {
        lock (_db.Gate)
        {
            _db.Dispose();
        }
    }

    private (long Id, ImageState State)? FindEntry(EntryKind kind, string hash)
    {
        using SqliteCommand command = _db.Command(
            "SELECT id, image_state FROM entries WHERE kind = @kind AND hash = @hash",
            ("@kind", (long)kind), ("@hash", hash));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetInt64(0), (ImageState)reader.GetInt32(1));
    }

    private long InsertEntry(CapturedEntry e, long copiedAt, bool carriesImage)
    {
        string? body = e.Body;
        long byteSize = (body is null ? 0 : Encoding.UTF8.GetByteCount(body)) + e.Formats.Sum(f => (long)f.Data.Length);
        ImageState state = carriesImage ? ImageState.Pending : ImageState.None;

        using (SqliteCommand insert = _db.Command(
            "INSERT INTO entries(kind, hash, preview, name, source, pinned, created_at, last_copied_at, width, height, byte_size, char_count, line_count, image_state, thumb, search, ocr, qr, body) VALUES(@kind, @hash, @preview, NULL, @source, 0, @created, @copied, 0, 0, @bytes, @chars, @lines, @state, NULL, @search, NULL, NULL, @body)",
            ("@kind", (long)e.Kind),
            ("@hash", e.Hash),
            ("@preview", Preview(e)),
            ("@source", e.SourceApp),
            ("@created", _clock.GetUtcNow().ToUnixTimeMilliseconds()),
            ("@copied", copiedAt),
            ("@bytes", byteSize),
            ("@chars", (long)(body?.Length ?? 0)),
            ("@lines", (long)CountLines(body)),
            ("@state", (long)state),
            ("@search", BuildSearch(e.Kind, body, null, null, null)),
            ("@body", body)))
        {
            insert.ExecuteNonQuery();
        }

        using SqliteCommand last = _db.Command("SELECT last_insert_rowid()");
        return (long)last.ExecuteScalar()!;
    }

    private void Touch(long id, long copiedAt, string? source)
    {
        // The timestamp never moves backwards, and a null source keeps the one already stored.
        using SqliteCommand command = _db.Command(
            "UPDATE entries SET last_copied_at = max(last_copied_at, @copied), source = COALESCE(@source, source) WHERE id = @id",
            ("@copied", copiedAt), ("@source", source), ("@id", id));
        command.ExecuteNonQuery();
    }

    private void SetImageState(long id, ImageState state)
    {
        using SqliteCommand command = _db.Command(
            "UPDATE entries SET image_state = @state WHERE id = @id", ("@state", (long)state), ("@id", id));
        command.ExecuteNonQuery();
    }

    private void WriteFormats(long entryId, IReadOnlyList<ClipFormat> formats)
    {
        using (SqliteCommand delete = _db.Command("DELETE FROM formats WHERE entry_id = @id", ("@id", entryId)))
        {
            delete.ExecuteNonQuery();
        }

        foreach (ClipFormat format in formats)
        {
            string file = Blobs.Save(format.Data, ExtensionFor(format.Name));
            InsertFormat(entryId, format.Name, file, format.Data.Length);
        }
    }

    private void InsertFormat(long entryId, string name, string file, long size)
    {
        using SqliteCommand command = _db.Command(
            "INSERT OR REPLACE INTO formats(entry_id, name, file, size) VALUES(@id, @name, @file, @size)",
            ("@id", entryId), ("@name", name), ("@file", file), ("@size", size));
        command.ExecuteNonQuery();
    }

    private void RaiseChanged()
    {
        Action? handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            // One failing observer must neither stop the others nor reach the COM or timer thread that committed the change.
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                Log.Error("HistoryStore.Changed", ex);
            }
        }
    }

    private static bool HasImageFormat(IEnumerable<ClipFormat> formats) =>
        formats.Any(f => f.Name is FormatNames.Png or FormatNames.Dib or FormatNames.DibV5);

    private static string ExtensionFor(string formatName) => formatName switch
    {
        FormatNames.Html => "html",
        FormatNames.Rtf => "rtf",
        FormatNames.Png => "png",
        FormatNames.Dib or FormatNames.DibV5 => "dib",
        _ => "bin",
    };

    private static string Preview(CapturedEntry e) => e.Kind switch
    {
        EntryKind.Image => ImagePreview,
        EntryKind.Files => FilesPreview(e.Body),
        _ => TextPreview(e.Body),
    };

    private static string TextPreview(string? body)
    {
        var collapsed = new StringBuilder();
        bool pendingSpace = false;
        foreach (char c in body ?? string.Empty)
        {
            if (char.IsWhiteSpace(c))
            {
                // A run becomes one space, and a trailing run is dropped, which is the trim.
                pendingSpace = collapsed.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }

            collapsed.Append(c);
            if (collapsed.Length > PreviewMaxUnits)
            {
                break;
            }
        }

        return collapsed.Length == 0 ? BlankPreview : Clip(collapsed.ToString(), PreviewMaxUnits);
    }

    private static string FilesPreview(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return BlankPreview;
        }

        string[] paths = body.Split('\n');
        string first = Clip(Path.GetFileName(paths[0]), PreviewMaxUnits);
        int others = paths.Length - 1;
        return others > 0 ? $"{first} ほか {others} 件" : first;
    }

    // Cuts at a unit boundary: a lone high surrogate left at the end would be invalid UTF-16.
    private static string Clip(string text, int maxUnits)
    {
        if (text.Length <= maxUnits)
        {
            return text;
        }

        int length = maxUnits;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    // A trailing newline ends the last line rather than starting an empty one, so "a\n" counts as one line.
    private static int CountLines(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return 0;
        }

        int lines = 0;
        bool openLine = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '\n' || c == '\r')
            {
                lines++;
                openLine = false;
                if (c == '\r' && i + 1 < body.Length && body[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                openLine = true;
            }
        }

        return openLine ? lines + 1 : lines;
    }

    private static string BuildSearch(EntryKind kind, string? body, string? name, string? ocr, string? qr)
    {
        var text = new StringBuilder();
        if (body is not null)
        {
            text.Append(Clip(body, SearchBodyMaxUnits));
        }

        AppendSearchPart(text, name);
        if (kind == EntryKind.Files && !string.IsNullOrEmpty(body))
        {
            foreach (string path in body.Split('\n'))
            {
                AppendSearchPart(text, Path.GetFileName(path));
            }
        }

        AppendSearchPart(text, ocr);
        AppendSearchPart(text, qr);
        return SearchNormalizer.Normalize(text.ToString());
    }

    private static void AppendSearchPart(StringBuilder text, string? part)
    {
        if (string.IsNullOrEmpty(part))
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append('\n');
        }

        text.Append(part);
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset FromUnixMilliseconds(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
