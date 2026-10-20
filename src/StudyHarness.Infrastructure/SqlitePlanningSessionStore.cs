using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

// Separate recovery journal: even incomplete/invalid form input must survive a crash.
// Each edit commits atomically; authorization and credentials are never journalled.
public sealed class SqlitePlanningSessionStore(string root) : IPlanningSessionStore
{
    private readonly string path = Path.Combine(Path.GetFullPath(root), "planning-session.db");
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > 1) { db.Dispose(); throw new InvalidDataException("Unsupported planning recovery version"); }
        cmd.CommandText = version == 0
            ? "PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS session(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL); PRAGMA user_version=1;"
            : "PRAGMA synchronous=FULL;";
        try { cmd.ExecuteNonQuery(); }
        catch { db.Dispose(); throw; }
        return db;
    }
    public PlanningSession? Load()
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root);
        using var file = File.Exists(path) ? SafeKnowledgeFiles.PinExistingFile(path) : null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM session WHERE id=1";
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PlanningSession>(json, Wire.Options) : null;
    }
    public void Save(PlanningSession session)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root);
        using var file = File.Exists(path) ? SafeKnowledgeFiles.PinExistingFile(path) : null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO session VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(session, Wire.Options));
        cmd.ExecuteNonQuery();
    }
}
