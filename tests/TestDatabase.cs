using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

internal static class TestDatabase
{
    /// <summary>Opens a non-pooled connection to the database at <paramref name="path" />.</summary>
    internal static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    /// <summary>Opens a non-pooled, read-only connection to the database at <paramref name="path" />.</summary>
    internal static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return connection;
    }

    /// <summary>Clears the connection pools, then deletes <paramref name="path" /> and its <c>-wal</c> and <c>-shm</c> files.</summary>
    internal static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
    }

    /// <summary>Clears the connection pools, then deletes <paramref name="directory" /> and its content. A locked file throws.</summary>
    internal static void DeleteDirectory(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
