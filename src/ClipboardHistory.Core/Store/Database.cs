using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Waigoma.ClipboardHistory.Store;

// Owns the one connection. Every statement must run under Gate, because the UI and the worker share it.
// Commits explicitly and rolls back on dispose, and only then stops new commands from enlisting in the transaction.
internal sealed class WriteTransaction : IDisposable
{
    private readonly Database _database;
    private readonly SqliteTransaction _inner;

    internal WriteTransaction(Database database, SqliteTransaction inner)
    {
        _database = database;
        _inner = inner;
    }

    public void Commit() => _inner.Commit();

    public void Dispose()
    {
        _database.EndWrite(_inner);
        _inner.Dispose();
    }
}

internal sealed class Database : IDisposable
{
    public const int SchemaVersion = 1;

    private static readonly string[] Schema =
    {
        """
        CREATE TABLE entries(id INTEGER PRIMARY KEY, kind INTEGER NOT NULL, hash TEXT NOT NULL, preview TEXT NOT NULL,
          name TEXT, source TEXT, pinned INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL, last_copied_at INTEGER NOT NULL,
          width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0, byte_size INTEGER NOT NULL DEFAULT 0,
          char_count INTEGER NOT NULL DEFAULT 0, line_count INTEGER NOT NULL DEFAULT 0,
          image_state INTEGER NOT NULL DEFAULT 0, thumb TEXT, search TEXT NOT NULL, ocr TEXT, qr TEXT, body TEXT,
          UNIQUE(kind, hash))
        """,
        "CREATE INDEX ix_entries_recent ON entries(last_copied_at DESC)",
        """
        CREATE TABLE formats(entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE, name TEXT NOT NULL,
          file TEXT NOT NULL, size INTEGER NOT NULL, PRIMARY KEY(entry_id, name)) WITHOUT ROWID
        """,
        """
        CREATE TABLE jobs(entry_id INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
          attempts INTEGER NOT NULL DEFAULT 0, state INTEGER NOT NULL DEFAULT 0)
        """,
        "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID",
    };

    static Database()
    {
        // Runs once per process, before the first connection is opened.
        SQLitePCL.Batteries_V2.Init();
    }

    public Database(string path)
    {
        // Pooling is off so that closing the store really releases the file; the worker must be able to reopen it.
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            Connection.Open();
            Configure();
            Migrate();
        }
        catch
        {
            Connection.Dispose();
            throw;
        }
    }

    public SqliteConnection Connection { get; }

    public Lock Gate { get; } = new();

    private SqliteTransaction? _active;

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = Connection.CreateCommand();
        command.CommandText = sql;
        // Set explicitly so a command issued inside a write always enlists in it.
        command.Transaction = _active;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    // BEGIN IMMEDIATE takes the write lock up front, so a read followed by a write cannot race with the other process.
    public WriteTransaction BeginWrite()
    {
        SqliteTransaction inner = Connection.BeginTransaction(deferred: false);
        _active = inner;
        return new WriteTransaction(this, inner);
    }

    internal void EndWrite(SqliteTransaction inner)
    {
        if (ReferenceEquals(_active, inner))
        {
            _active = null;
        }
    }

    public void Dispose() => Connection.Dispose();

    private void Configure()
    {
        Execute("PRAGMA busy_timeout = 5000");
        Execute("PRAGMA journal_mode = WAL");
        Execute("PRAGMA foreign_keys = ON");
        Execute("PRAGMA cache_size = -512");
    }

    private void Migrate()
    {
        // The version is read inside the write lock. Two processes creating the schema at once would otherwise both see 0.
        using WriteTransaction transaction = BeginWrite();
        int version = ReadUserVersion();
        if (version > SchemaVersion)
        {
            // Opening an unknown newer schema could corrupt it, so refuse rather than guess.
            throw new InvalidDataException("history database schema is newer than this version supports");
        }

        if (version == 0)
        {
            foreach (string sql in Schema)
            {
                Execute(sql);
            }

            Execute($"PRAGMA user_version = {SchemaVersion}");
        }

        transaction.Commit();
    }

    private int ReadUserVersion()
    {
        using SqliteCommand command = Command("PRAGMA user_version");
        object? value = command.ExecuteScalar();
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = Command(sql);
        command.ExecuteNonQuery();
    }
}
