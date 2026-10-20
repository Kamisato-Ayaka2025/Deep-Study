using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

// Both databases use rollback journals on the same local volume. SQLite's
// super-journal commits their changes atomically, including crash recovery.
// No source/library/export pathname is ever accepted as a deletion target.
public sealed class SqliteSubjectLifecycle(string root) : ISubjectLifecycle
{
    public Task<Result<bool>> DeleteAsync(Guid subject, long revision, bool confirmed, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!confirmed || subject == Guid.Empty || revision < 0)
            return Task.FromResult(Result<bool>.Fail(ErrorCode.VALIDATION_ERROR, "请先确认整学科删除范围。"));
        using var pins = SafeKnowledgeFiles.PinDirectory(Path.GetFullPath(root));
        using var planPin = SafeKnowledgeFiles.PinExistingFile(Path.Combine(root, "planning.db"));
        using var lessonPin = SafeKnowledgeFiles.PinExistingFile(Path.Combine(root, "learning.db"));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "learning.db"), Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "ATTACH DATABASE $path AS planning";
        cmd.Parameters.AddWithValue("$path", Path.Combine(root, "planning.db")); cmd.ExecuteNonQuery(); cmd.Parameters.Clear();
        foreach (var schema in new[] { "main", "planning" })
        {
            cmd.CommandText = $"PRAGMA {schema}.journal_mode";
            if (!string.Equals(cmd.ExecuteScalar()?.ToString(), "delete", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Atomic deletion requires rollback journals.");
            cmd.CommandText = $"PRAGMA {schema}.synchronous=FULL"; cmd.ExecuteNonQuery();
        }
        using var tx = db.BeginTransaction(); cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", subject.ToString());
        cmd.CommandText = "SELECT json_extract(json,'$.revision') FROM planning.drafts WHERE id=$id";
        var current = cmd.ExecuteScalar();
        if (current is null) return Task.FromResult(Result<bool>.Success(false));
        if (Convert.ToInt64(current) != revision)
            return Task.FromResult(Result<bool>.Fail(ErrorCode.REVISION_CONFLICT, "学科已改变，请重新核对删除范围。"));
        // Tombstones contain only an opaque identity, never learner content.
        Run("INSERT OR IGNORE INTO deleted_subjects VALUES($id)");
        Run("DELETE FROM generation_timings WHERE task_id IN (SELECT id FROM generation WHERE subject=$id) OR task_id IN (SELECT task_id FROM generation_budgets WHERE json_extract(json,'$.subjectId')=$id)");
        Run("DELETE FROM generation_budgets WHERE task_id IN (SELECT id FROM generation WHERE subject=$id) OR json_extract(json,'$.subjectId')=$id");
        Run("DELETE FROM dispatches WHERE task IN (SELECT id FROM generation WHERE subject=$id)");
        foreach (var table in new[] { "lessons", "attempts", "mastery", "completions", "positions", "generation", "lesson_history", "lab_packages", "lab_jobs", "lab_attestations", "requests", "lesson_source_selections", "quiz_option_orders" })
            Run($"DELETE FROM {table} WHERE subject=$id");
        Run("DELETE FROM outbox WHERE json_extract(json,'$.subjectId')=$id");
        Run("DELETE FROM planning.requests WHERE json_extract(result,'$.value.id')=$id");
        Run("DELETE FROM planning.outbox WHERE subject_id=$id");
        Run("DELETE FROM planning.plan_v2_metadata WHERE draft_id=$id");
        Run("DELETE FROM planning.drafts WHERE id=$id");
        ct.ThrowIfCancellationRequested(); tx.Commit();
        return Task.FromResult(Result<bool>.Success(true));
        void Run(string sql) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    }
}
