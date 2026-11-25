using Microsoft.Data.Sqlite;

namespace Waigoma.ClipboardHistory.Store;

public sealed record Job(long EntryId, int Attempts);

// The worker is a separate process, so a claim is one UPDATE ... RETURNING that the database applies atomically.
public sealed class JobQueue
{
    private const int MaxAttempts = 3;

    private const int StatePending = 0;
    private const int StateRunning = 1;
    private const int StateFailed = 2;

    private readonly Database _db;
    private readonly Action _onEntryFailed;

    internal JobQueue(Database db, Action onEntryFailed)
    {
        _db = db;
        _onEntryFailed = onEntryFailed;
    }

    public void Enqueue(long entryId)
    {
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            EnqueueCore(_db, entryId);
            transaction.Commit();
        }
    }

    // Shared with HistoryStore.Upsert, which must enqueue inside its own transaction.
    internal static void EnqueueCore(Database db, long entryId)
    {
        // A re-enqueue starts the job over, so an image copied again is not stuck behind old failures.
        using SqliteCommand command = db.Command(
            "INSERT INTO jobs(entry_id, attempts, state) VALUES(@id, 0, 0) ON CONFLICT(entry_id) DO UPDATE SET attempts = 0, state = 0",
            ("@id", entryId));
        command.ExecuteNonQuery();
    }

    public Job? Claim()
    {
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            Job? job;
            using (SqliteCommand command = _db.Command(
                "UPDATE jobs SET state = 1 WHERE entry_id = (SELECT entry_id FROM jobs WHERE state = @pending ORDER BY entry_id LIMIT 1) RETURNING entry_id, attempts",
                ("@pending", StatePending)))
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                job = reader.Read() ? new Job(reader.GetInt64(0), reader.GetInt32(1)) : null;
            }

            transaction.Commit();
            return job;
        }
    }

    public void Complete(long entryId)
    {
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            using (SqliteCommand command = _db.Command("DELETE FROM jobs WHERE entry_id = @id", ("@id", entryId)))
            {
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void Fail(long entryId)
    {
        bool failed = false;
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            long? state = null;
            using (SqliteCommand command = _db.Command(
                "UPDATE jobs SET attempts = attempts + 1, state = CASE WHEN attempts + 1 >= @max THEN @failed ELSE @pending END WHERE entry_id = @id RETURNING state",
                ("@max", MaxAttempts), ("@failed", StateFailed), ("@pending", StatePending), ("@id", entryId)))
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    state = reader.GetInt64(0);
                }
            }

            if (state is null)
            {
                // The job was completed or never existed, so there is nothing to record.
                return;
            }

            if (state == StateFailed)
            {
                using SqliteCommand mark = _db.Command(
                    "UPDATE entries SET image_state = @failed WHERE id = @id",
                    ("@failed", (long)ImageState.Failed), ("@id", entryId));
                mark.ExecuteNonQuery();
                failed = true;
            }

            transaction.Commit();
        }

        // Raised after the lock is released, so a handler that reads the store cannot deadlock.
        if (failed)
        {
            _onEntryFailed();
        }
    }

    public int ResetRunning()
    {
        lock (_db.Gate)
        {
            using WriteTransaction transaction = _db.BeginWrite();
            int count;
            using (SqliteCommand command = _db.Command(
                "UPDATE jobs SET state = @pending WHERE state = @running",
                ("@pending", StatePending), ("@running", StateRunning)))
            {
                count = command.ExecuteNonQuery();
            }

            transaction.Commit();
            return count;
        }
    }

    public bool HasPending()
    {
        lock (_db.Gate)
        {
            using SqliteCommand command = _db.Command(
                "SELECT EXISTS(SELECT 1 FROM jobs WHERE state = @pending)",
                ("@pending", StatePending));
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
    }
}
