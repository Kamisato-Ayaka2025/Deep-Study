using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed class BookKnowledgeCache(string root) : IBookKnowledgeCache
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(root);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "book-knowledge.db"), Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS batches(fingerprint TEXT PRIMARY KEY, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS derived(key TEXT PRIMARY KEY,json TEXT NOT NULL)";
        cmd.ExecuteNonQuery(); return db;
    }
    public Task<BookKnowledgeUnit?> ReadKnowledgeAsync(string fingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM batches WHERE fingerprint=$key"; cmd.Parameters.AddWithValue("$key", fingerprint);
        return Task.FromResult(cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<BookKnowledgeUnit>(json, Wire.Options) : null);
    }
    public Task SaveKnowledgeAsync(BookKnowledgeUnit unit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO batches VALUES($key,$json)";
        cmd.Parameters.AddWithValue("$key", unit.Fingerprint); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(unit, Wire.Options));
        cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task<string?> ReadDerivedAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM derived WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }
    public Task SaveDerivedAsync(string key, string json, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO derived VALUES($key,$json)";
        cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$json", json);
        cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
}
