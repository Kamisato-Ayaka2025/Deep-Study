using System.Text.Json;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed partial class SqliteLearningStore
{
    public Task SaveSelectionAsync(Guid subject, LessonSourceSelection selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        if (Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM deleted_subjects WHERE subject=$p0", subject.ToString())) != 0)
            throw new InvalidDataException("Deleted subject");
        Execute(db, tx, "INSERT INTO lesson_source_selections VALUES($p0,$p1,$p2) ON CONFLICT(subject,node) DO UPDATE SET json=excluded.json",
            subject.ToString(), selection.LessonKey, Json(selection));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.CompletedTask;
    }
    public Task<LessonSourceSelection?> SelectionAsync(Guid subject, string nodeKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open();
        var json = Scalar(db, null, "SELECT json FROM lesson_source_selections WHERE subject=$p0 AND node=$p1", subject.ToString(), nodeKey) as string;
        return Task.FromResult(json is null ? null : JsonSerializer.Deserialize<LessonSourceSelection>(json, Wire.Options));
    }
}
