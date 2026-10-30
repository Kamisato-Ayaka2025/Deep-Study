using StudyHarness.Application;

namespace StudyHarness.Infrastructure;

public sealed partial class SqliteLearningStore : ITaskTelemetry, IContextDiagnostics
{
    public Task<string?> ContextGapAsync(Guid taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open();
        return Task.FromResult(Scalar(db, null, "SELECT outcome FROM generation_timings WHERE task_id=$p0 AND phase='context-check' ORDER BY attempt DESC LIMIT 1", taskId.ToString()) as string);
    }
    public Task SaveBudgetAsync(TaskBudgetRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        if (!Deleted(db, tx, record.SubjectId))
            Execute(db, tx, "INSERT OR IGNORE INTO generation_budgets VALUES($p0,$p1)", record.TaskId.ToString(), Json(record));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.CompletedTask;
    }
    public Task RecordAsync(TaskTiming timing, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        // Deleted subjects have no budget; late telemetry cannot recreate their data.
        if (Scalar(db, tx, "SELECT json FROM generation_budgets WHERE task_id=$p0", timing.TaskId.ToString()) is string json &&
            !Deleted(db, tx, Read<TaskBudgetRecord>(json).SubjectId))
            Execute(db, tx, "INSERT OR IGNORE INTO generation_timings VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
                timing.TaskId.ToString(), timing.Phase, timing.Attempt, timing.StartedAt.ToString("O"), timing.EndedAt.ToString("O"), timing.Outcome,
                timing.Usage is null ? DBNull.Value : Json(timing.Usage));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.CompletedTask;
    }
}
