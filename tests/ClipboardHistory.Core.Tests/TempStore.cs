using Microsoft.Data.Sqlite;
using Waigoma.ClipboardHistory.Store;

namespace Waigoma.ClipboardHistory.Tests;

// Every test gets its own folder so none of them can touch the user's real history.
internal sealed class TempStore : IDisposable
{
    public TempStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "ClipboardHistoryTests", Guid.NewGuid().ToString("N"));
        Paths = new StorePaths(Root);
    }

    public string Root { get; }

    public StorePaths Paths { get; }

    public void Dispose()
    {
        // Cleanup is best effort: a locked file must not fail the test that already finished.
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// Reads and writes the database file directly, to set fields that the store does not expose yet.
internal static class RawDb
{
    public static long Scalar(StorePaths paths, string sql)
    {
        using SqliteConnection connection = Open(paths);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    public static void Execute(StorePaths paths, string sql)
    {
        using SqliteConnection connection = Open(paths);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(StorePaths paths)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Db, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
