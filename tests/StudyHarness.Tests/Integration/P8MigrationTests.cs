using Microsoft.Data.Sqlite;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class P8MigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-p8-migration", Guid.NewGuid().ToString("N"));

    private SqliteConnection Open(string file = "planning.db")
    {
        Directory.CreateDirectory(root);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, file), Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static object? Sql(SqliteConnection db, string text)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = text; return cmd.ExecuteScalar();
    }
    private void LegacyPlanning(bool conflictingView = false)
    {
        using var db = Open();
        Sql(db, """
            CREATE TABLE drafts(id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE requests(id TEXT PRIMARY KEY,hash TEXT NOT NULL,result TEXT NOT NULL);
            CREATE TABLE outbox(id TEXT PRIMARY KEY,subject_id TEXT NOT NULL,event_type TEXT NOT NULL);
            INSERT INTO drafts VALUES('old','{"plan":{"hash":"unchanged"},"confirmed":true}');
            INSERT INTO requests VALUES('request','hash','cached');
            PRAGMA user_version=1;
            """);
        if (conflictingView) Sql(db, "CREATE VIEW plan_v2_metadata AS SELECT id FROM drafts");
    }

    [Fact]
    public void PlanningUpgrade_PreservesExactLegacyJsonAndBackup_ReopenDoesNotBackUpAgain()
    {
        LegacyPlanning();
        string original;
        using (var db = Open()) original = (string)Sql(db, "SELECT json FROM drafts")!;
        _ = new SqlitePlanningStore(root);
        using (var db = Open())
        {
            Assert.Equal(2L, Sql(db, "PRAGMA user_version"));
            Assert.Equal(original, Sql(db, "SELECT json FROM drafts"));
            Assert.Equal("cached", Sql(db, "SELECT result FROM requests"));
            Assert.Equal(0L, Sql(db, "SELECT count(*) FROM plan_v2_metadata"));
        }
        var backup = Assert.Single(Directory.GetFiles(root, "planning.db.v1-*.bak"));
        Assert.True(File.Exists(backup + ".verified"));
        using (var db = Open(Path.GetFileName(backup)))
        {
            Assert.Equal(1L, Sql(db, "PRAGMA user_version"));
            Assert.Equal(original, Sql(db, "SELECT json FROM drafts"));
            Assert.Equal("ok", Sql(db, "PRAGMA integrity_check"));
        }
        _ = new SqlitePlanningStore(root);
        Assert.Single(Directory.GetFiles(root, "planning.db.v1-*.bak"));
    }

    [Fact]
    public void FailedPlanningMigration_RollsBackAndRetainsRecoverableBackup()
    {
        LegacyPlanning(true);
        Assert.Throws<SqliteException>(() => new SqlitePlanningStore(root));
        using var db = Open();
        Assert.Equal(1L, Sql(db, "PRAGMA user_version"));
        Assert.Equal("cached", Sql(db, "SELECT result FROM requests"));
        var backup = Assert.Single(Directory.GetFiles(root, "planning.db.v1-*.bak"));
        using var restored = Open(Path.GetFileName(backup));
        Assert.Equal(1L, Sql(restored, "PRAGMA user_version"));
        Assert.Equal(Sql(db, "SELECT json FROM drafts"), Sql(restored, "SELECT json FROM drafts"));
    }

    [Fact]
    public void FuturePlanningVersion_RejectsWithoutBackupOrMutation()
    {
        using (var db = Open()) Sql(db, "PRAGMA user_version=99");
        Assert.Throws<InvalidDataException>(() => new SqlitePlanningStore(root));
        Assert.Empty(Directory.GetFiles(root, "*.bak"));
        using var check = Open(); Assert.Equal(99L, Sql(check, "PRAGMA user_version"));
    }

    [Fact]
    public void LearningUpgrade_PreservesFactsAndCreatesV5Backup()
    {
        var planning = new SqlitePlanningStore(root);
        _ = new SqliteLearningStore(root, planning);
        using (var db = Open("learning.db"))
        {
            Sql(db, """
                DROP TABLE generation_budgets;
                DROP TABLE generation_timings;
                DROP TABLE quiz_option_orders;
                INSERT INTO completions VALUES('subject','n1','2026-10-19T00:00:00Z');
                PRAGMA user_version=5;
                """);
        }
        _ = new SqliteLearningStore(root, planning);
        using (var db = Open("learning.db"))
        {
            Assert.Equal(8L, Sql(db, "PRAGMA user_version"));
            Assert.Equal("2026-10-19T00:00:00Z", Sql(db, "SELECT at FROM completions"));
            Assert.Equal(0L, Sql(db, "SELECT count(*) FROM generation_budgets"));
            Assert.Equal(0L, Sql(db, "SELECT count(*) FROM generation_timings"));
        }
        var backup = Assert.Single(Directory.GetFiles(root, "learning.db.v5-*.bak"));
        using var old = Open(Path.GetFileName(backup));
        Assert.Equal(5L, Sql(old, "PRAGMA user_version"));
        Assert.Equal("2026-10-19T00:00:00Z", Sql(old, "SELECT at FROM completions"));
    }

    [Fact]
    public async Task ConcurrentInitialization_SerializesSchemaChanges()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            var planning = new SqlitePlanningStore(root);
            _ = new SqliteLearningStore(root, planning);
        })));
        using var db = Open(); Assert.Equal(2L, Sql(db, "PRAGMA user_version"));
        using var learning = Open("learning.db"); Assert.Equal(8L, Sql(learning, "PRAGMA user_version"));
    }

    [Fact]
    public void PlanningBackup_IncludesCommittedWalData()
    {
        LegacyPlanning();
        using var writer = Open();
        Sql(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        Sql(writer, "UPDATE requests SET result='committed-in-wal'");
        _ = new SqlitePlanningStore(root);
        var backup = Assert.Single(Directory.GetFiles(root, "planning.db.v1-*.bak"));
        using var restored = Open(Path.GetFileName(backup));
        Assert.Equal("committed-in-wal", Sql(restored, "SELECT result FROM requests"));
        Assert.Equal(1L, Sql(restored, "PRAGMA user_version"));
    }

    [Fact]
    public void FailedLearningMigration_RollsBackNewTablesAndVersion()
    {
        var planning = new SqlitePlanningStore(root);
        _ = new SqliteLearningStore(root, planning);
        using (var db = Open("learning.db")) Sql(db, """
            DROP TABLE generation_budgets;
            DROP TABLE generation_timings;
            DROP TABLE quiz_option_orders;
            CREATE VIEW generation_timings AS SELECT id FROM generation;
            INSERT INTO completions VALUES('subject','n1','retained');
            PRAGMA user_version=5;
            """);
        Assert.Throws<SqliteException>(() => new SqliteLearningStore(root, planning));
        using var check = Open("learning.db");
        Assert.Equal(5L, Sql(check, "PRAGMA user_version"));
        Assert.Equal(0L, Sql(check, "SELECT count(*) FROM sqlite_master WHERE name='generation_budgets'"));
        Assert.Equal("retained", Sql(check, "SELECT at FROM completions"));
        var backup = Assert.Single(Directory.GetFiles(root, "learning.db.v5-*.bak"));
        Assert.True(File.Exists(backup + ".verified"));
        using var restored = Open(Path.GetFileName(backup));
        Assert.Equal("retained", Sql(restored, "SELECT at FROM completions"));
    }

    [Fact]
    public async Task SubjectDeletion_RemovesOnlyOwnedMetadataAndBudgets()
    {
        var planning = new SqlitePlanningStore(root);
        _ = new SqliteLearningStore(root, planning);
        var draft = (await planning.StartAsync(new(Guid.NewGuid(), 0, new PlanningInput("test", null, null, 60, "Math", [])))).Value!;
        using (var db = Open()) Sql(db, $"INSERT INTO plan_v2_metadata VALUES('{draft.Id}','{{}}','[]'); INSERT INTO plan_v2_metadata VALUES('other','{{}}','[]')");
        using (var db = Open("learning.db")) Sql(db, $$"""
            INSERT INTO generation VALUES('task','{{draft.Id}}','n1','Failed','{}');
            INSERT INTO generation_budgets VALUES('task','{}');
            INSERT INTO generation_budgets VALUES('other','{}');
            INSERT INTO generation_timings VALUES('task','model',1,'start','end','failed',NULL);
            INSERT INTO generation_timings VALUES('other','model',1,'start','end','failed',NULL);
            """);
        using (var db = Open()) Sql(db, "CREATE TRIGGER fail_delete BEFORE DELETE ON drafts BEGIN SELECT RAISE(ABORT,'synthetic failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true));
        using (var db = Open())
        {
            Assert.Equal(2L, Sql(db, "SELECT count(*) FROM plan_v2_metadata"));
            Sql(db, "DROP TRIGGER fail_delete");
        }
        using (var db = Open("learning.db"))
        {
            Assert.Equal(2L, Sql(db, "SELECT count(*) FROM generation_budgets"));
            Assert.Equal(2L, Sql(db, "SELECT count(*) FROM generation_timings"));
            Assert.Equal(0L, Sql(db, "SELECT count(*) FROM deleted_subjects"));
        }
        Assert.True((await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true)).Ok);
        using (var db = Open()) Assert.Equal("other", Sql(db, "SELECT draft_id FROM plan_v2_metadata"));
        using (var db = Open("learning.db"))
        {
            Assert.Equal("other", Sql(db, "SELECT task_id FROM generation_budgets"));
            Assert.Equal("other", Sql(db, "SELECT task_id FROM generation_timings"));
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
