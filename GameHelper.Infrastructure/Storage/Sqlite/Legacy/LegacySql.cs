using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

internal static class LegacySql
{
    internal static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values)
    {
        using var command = Command(c, tx, sql, values);
        return command.ExecuteNonQuery();
    }
    internal static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values)
    {
        using var command = Command(c, tx, sql, values);
        return command.ExecuteScalar();
    }
    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values)
    {
        var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$" + i, values[i] ?? DBNull.Value);
        return command;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string Id(string source, long ordinal) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(source + ":" + ordinal))[..16]).ToString("D");
    internal static string Date(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);
    internal static DateTime Date(string value, int kind) => DateTime.SpecifyKind(DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), (DateTimeKind)kind);
    internal static void WriteJson<T>(string path, T value) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));
    internal static void WriteBytes(string path, byte[] bytes)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
    internal static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Null manifest: " + path);
}
