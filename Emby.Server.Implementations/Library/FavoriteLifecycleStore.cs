using System.IO;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Atomically stores active viewing cycles and an indexed completion ledger without
/// retaining or rewriting the user's entire playback-session history in memory.
/// </summary>
internal sealed class FavoriteLifecycleStore
{
    private readonly string _path;

    public FavoriteLifecycleStore(string dataPath)
    {
        _path = Path.Combine(dataPath, "favorite-lifecycle.db");
    }

    public string? LoadState()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM FavoriteState WHERE Id = 1";
        return command.ExecuteScalar() as string;
    }

    public bool HasCompletedSession(string key)
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM CompletedSession WHERE SessionKey = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is not null;
    }

    public void SaveState(string json, string? completedSessionKey = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO FavoriteState (Id, Json) VALUES (1, $json) ON CONFLICT(Id) DO UPDATE SET Json = excluded.Json";
        command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery();

        if (completedSessionKey is not null)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT OR IGNORE INTO CompletedSession (SessionKey) VALUES ($key)";
            command.Parameters.AddWithValue("$key", completedSessionKey);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        // A short-lived non-pooled connection also permits normal server-data backup/removal
        // without a second long-lived database handle or a background maintenance task.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS FavoriteState (Id INTEGER PRIMARY KEY CHECK (Id = 1), Json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS CompletedSession (SessionKey TEXT PRIMARY KEY) WITHOUT ROWID;
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
