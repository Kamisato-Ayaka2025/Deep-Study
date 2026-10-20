using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed partial class SqliteLearningStore
{
    private static GenerationTask? TaskRow(SqliteConnection db, SqliteTransaction? tx, Guid id) =>
        Scalar(db, tx, "SELECT json FROM generation WHERE id=$p0", id.ToString()) is string json ? Read<GenerationTask>(json) : null;
    private static List<GenerationTask> TaskRows(SqliteConnection db, SqliteTransaction? tx)
    {
        using var cmd = Command(db, tx, "SELECT json FROM generation ORDER BY rowid"); using var reader = cmd.ExecuteReader();
        var list = new List<GenerationTask>(); while (reader.Read()) list.Add(Read<GenerationTask>(reader.GetString(0))); return list;
    }
    private static GenerationControl Control(SqliteConnection db, SqliteTransaction? tx) =>
        Read<GenerationControl>((string)Scalar(db, tx, "SELECT json FROM generation_control WHERE id=1")!);
    private static void SaveControl(SqliteConnection db, SqliteTransaction tx, GenerationControl control) =>
        Execute(db, tx, "UPDATE generation_control SET json=$p0 WHERE id=1", Json(control with { Revision = control.Revision + 1 }));
    private static void SaveTask(SqliteConnection db, SqliteTransaction tx, GenerationTask task) =>
        Execute(db, tx, "INSERT INTO generation VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(id) DO UPDATE SET state=excluded.state,json=excluded.json",
            task.Id.ToString(), task.SubjectId.ToString(), task.NodeKey, task.Status.ToString(), Json(task));
    public Task<Result<GenerationTask>> RequestAsync(Guid subjectId, Command<RequestGeneration> command, CancellationToken ct = default) =>
        Write(command, subjectId, command.Payload.NodeKey, "generation-request", (db, tx, draft, node) =>
        {
            if (!Enum.IsDefined(command.Payload.Trigger)) return Bad<GenerationTask>();
            if (Lesson(db, tx, subjectId, node.Key) is not null && command.Payload.Trigger != GenerationTrigger.Regenerate) return Result<GenerationTask>.Fail(ErrorCode.ALREADY_READY, "正文已就绪，直接阅读或明确重新生成。");
            var control = Control(db, tx);
            if (command.Payload.Trigger is GenerationTrigger.Auto or GenerationTrigger.Resume && (control.Mode != GenerationMode.Auto || control.AutoPaused))
                return Result<GenerationTask>.Fail(ErrorCode.AUTO_PAUSED, "自动生成已暂停或处于点击模式。");
            var previous = TaskRows(db, tx).LastOrDefault(t => t.SubjectId == subjectId && t.NodeKey == node.Key);
            if (previous?.Status is GenerationStatus.Queued or GenerationStatus.Running) return Result<GenerationTask>.Success(previous);
            if (previous is not null && command.Payload.Trigger is not (GenerationTrigger.ManualRetry or GenerationTrigger.Resume or GenerationTrigger.Regenerate) && previous.Failure != "MODE_CHANGED")
                return Result<GenerationTask>.Fail(ErrorCode.RUN_CANCELLED, "失败或取消的任务需要明确手动重试。");
            var task = new GenerationTask(Guid.NewGuid(), subjectId, node.Key, draft.Plan!.Hash, command.Payload.Trigger,
                GenerationStatus.Queued, 0, 0, DateTimeOffset.UtcNow, null, null, null);
            SaveTask(db, tx, task); return Result<GenerationTask>.Success(task);
        }, ct);
    public Task<IReadOnlyList<GenerationTask>> TasksAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var db = Open(); return Task.FromResult<IReadOnlyList<GenerationTask>>(TaskRows(db, null)); }
    public Task<GenerationControl> ControlAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var db = Open(); return Task.FromResult(Control(db, null)); }
    public Task SetModeAsync(GenerationMode mode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (!Enum.IsDefined(mode)) throw new ArgumentException("Invalid mode");
        using var db = Open(); using var tx = db.BeginTransaction(); var control = Control(db, tx);
        SaveControl(db, tx, control with { Mode = mode });
        if (mode == GenerationMode.Click)
            foreach (var t in TaskRows(db, tx).Where(t => t.Status == GenerationStatus.Queued && t.Trigger is GenerationTrigger.Auto or GenerationTrigger.Resume))
                SaveTask(db, tx, t with { Status = GenerationStatus.Cancelled, Revision = t.Revision + 1, Failure = "MODE_CHANGED" });
        tx.Commit(); return Task.CompletedTask;
    }
    public Task ResumeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        SaveControl(db, tx, Control(db, tx) with { AutoPaused = false }); tx.Commit(); return Task.CompletedTask;
    }
    public Task CancelAsync(Guid taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(); var task = TaskRow(db, tx, taskId);
        if (task?.Status is GenerationStatus.Queued or GenerationStatus.Running)
        {
            SaveTask(db, tx, task with { Status = GenerationStatus.Cancelled, Revision = task.Revision + 1, Failure = "RUN_CANCELLED" });
            if (task.Trigger is GenerationTrigger.Auto or GenerationTrigger.Resume)
                SaveControl(db, tx, Control(db, tx) with { AutoPaused = true });
        }
        tx.Commit(); return Task.CompletedTask;
    }
    public Task RecoverAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var task in TaskRows(db, tx).Where(t => t.Status == GenerationStatus.Running))
        {
            SaveTask(db, tx, task with { Status = GenerationStatus.Failed, Revision = task.Revision + 1, Failure = "INTERRUPTED_MAY_BE_BILLED" });
            if (task.Trigger is GenerationTrigger.Auto or GenerationTrigger.Resume) SaveControl(db, tx, Control(db, tx) with { AutoPaused = true });
        }
        tx.Commit(); return Task.CompletedTask;
    }
    public Task<Result<GenerationTask>> DispatchAsync(Guid taskId, long revision, string model, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(); var task = TaskRow(db, tx, taskId);
        if (task is null || task.Revision != revision || task.Status != GenerationStatus.Queued || task.Attempts >= 3 || task.NotBefore > DateTimeOffset.UtcNow)
            return Task.FromResult(Result<GenerationTask>.Fail(ErrorCode.REVISION_CONFLICT, "任务状态已改变或尚在退避等待。"));
        var control = Control(db, tx);
        if (task.Trigger is GenerationTrigger.Auto or GenerationTrigger.Resume && (control.AutoPaused || control.Mode != GenerationMode.Auto))
            return Task.FromResult(Result<GenerationTask>.Fail(ErrorCode.AUTO_PAUSED, "自动调度已暂停。"));
        if (TaskRows(db, tx).Any(t => t.Status == GenerationStatus.Running))
            return Task.FromResult(Result<GenerationTask>.Fail(ErrorCode.TASK_ACTIVE, "已有课程正在生成。"));
        task = task with { Status = GenerationStatus.Running, Attempts = task.Attempts + 1, Revision = task.Revision + 1, Failure = null, NotBefore = null };
        SaveTask(db, tx, task);
        Execute(db, tx, "INSERT INTO dispatches VALUES($p0,$p1,$p2)", taskId.ToString(), task.Attempts,
            Json(new GenerationAttempt(taskId, task.Attempts, DateTimeOffset.UtcNow, model, null, null, task.Revision)));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.FromResult(Result<GenerationTask>.Success(task));
    }
    private static bool FinishAttempt(SqliteConnection db, SqliteTransaction tx, GenerationTask task, long revision, string outcome, TokenUsage? usage)
    {
        if (Scalar(db, tx, "SELECT json FROM dispatches WHERE task=$p0 AND number=$p1", task.Id.ToString(), task.Attempts) is not string json) return false;
        var attempt = Read<GenerationAttempt>(json);
        if (attempt.Outcome is not null || attempt.TaskRevision != revision) return false;
        Execute(db, tx, "UPDATE dispatches SET json=$p2 WHERE task=$p0 AND number=$p1", task.Id.ToString(), task.Attempts,
            Json(attempt with { Outcome = outcome, Usage = usage }));
        return true;
    }
    private static TokenUsage? AddUsage(TokenUsage? previous, TokenUsage? next) => next is null ? previous : previous is null ? next :
        new(previous.PromptTokens + next.PromptTokens, previous.CompletionTokens + next.CompletionTokens, previous.TotalTokens + next.TotalTokens);
    public Task<GenerationTask?> FailAsync(Guid taskId, long revision, ErrorCode code, TokenUsage? usage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(); var task = TaskRow(db, tx, taskId);
        if (task is null) return Task.FromResult<GenerationTask?>(null);
        var recorded = FinishAttempt(db, tx, task, revision, code.ToString(), usage);
        if (task.Status == GenerationStatus.Running && task.Revision == revision)
        {
            var retry = code == ErrorCode.TRANSIENT_PROVIDER_ERROR && task.Attempts < 3;
            task = task with { Status = retry ? GenerationStatus.Queued : GenerationStatus.Failed, Revision = task.Revision + 1,
                Failure = code.ToString(), NotBefore = retry ? DateTimeOffset.UtcNow.AddSeconds(task.Attempts == 1 ? 1 : 3) : null,
                Usage = AddUsage(task.Usage, usage) };
            SaveTask(db, tx, task);
        }
        else if (recorded && usage is not null) { task = task with { Usage = AddUsage(task.Usage, usage) }; SaveTask(db, tx, task); }
        tx.Commit(); return Task.FromResult<GenerationTask?>(task);
    }
    public async Task<Result<LessonIdentity>> CompleteAsync(Guid taskId, long revision, TheoryLesson lesson,
        IReadOnlyList<PlanSource> sources, TokenUsage? usage, CancellationToken ct = default)
    {
        var frozen = Read<TheoryLesson>(Json(lesson)); var sent = Read<PlanSource[]>(Json(sources));
        using var db = Open(); var before = TaskRow(db, null, taskId);
        if (before is null) return Bad<LessonIdentity>();
        var draft = await planning.GetAsync(before.SubjectId, ct);
        using var tx = db.BeginTransaction(); var task = TaskRow(db, tx, taskId);
        if (task is null) return Result<LessonIdentity>.Fail(ErrorCode.SUBJECT_DELETING, "学科或任务已删除，迟到正文未发布。");
        var recorded = FinishAttempt(db, tx, task, revision, task.Status == GenerationStatus.Cancelled ? "LATE_CANCELLED" : "REPLY", usage);
        if (task.Status != GenerationStatus.Running || task.Revision != revision)
        {
            if (recorded && usage is not null) SaveTask(db, tx, task with { Usage = AddUsage(task.Usage, usage) });
            tx.Commit(); return Result<LessonIdentity>.Fail(ErrorCode.RUN_CANCELLED, "任务已取消或改变，迟到正文未发布。");
        }
        var node = draft?.Plan?.Candidate.Nodes.SingleOrDefault(n => n.Key == task.NodeKey);
        var valid = Application.LessonRules.Validate(frozen, sent);
        if (draft?.Confirmed != true || draft.Plan!.Hash != task.PlanHash || node is null || !valid.Ok ||
            frozen.SourceKeys.Count == 0 && !Application.TheoryKnowledgePolicy.CanUseKnowledgeOnly(node))
        {
            SaveTask(db, tx, task with { Status = GenerationStatus.Failed, Revision = task.Revision + 1, Failure = "INVALID_MODEL_OUTPUT", Usage = AddUsage(task.Usage, usage) });
            tx.Commit(); return Result<LessonIdentity>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "课程或来源校验失败，未发布。");
        }
        var existing = Lesson(db, tx, task.SubjectId, task.NodeKey);
        if (existing is not null)
        {
            if (task.Trigger != GenerationTrigger.Regenerate)
            {
                SaveTask(db, tx, task with { Status = GenerationStatus.Failed, Revision = task.Revision + 1, Failure = "ALREADY_READY", Usage = AddUsage(task.Usage, usage) });
                tx.Commit(); return Result<LessonIdentity>.Fail(ErrorCode.ALREADY_READY, "正文已存在。");
            }
            var cited = sent.Where(s => frozen.SourceKeys.Contains(s.Key)).ToArray();
            var nextHash = PlanRules.Hash(new { subjectId = task.SubjectId, nodeKey = task.NodeKey, planHash = task.PlanHash, content = frozen, sources = cited });
            if (existing.Hash != nextHash) ArchiveCurrent(db, tx, task.SubjectId, task.NodeKey, existing);
        }
        var identity = PublishCore(db, tx, task.SubjectId, task.NodeKey, task.PlanHash, frozen, sent);
        SaveTask(db, tx, task with { Status = GenerationStatus.Ready, Revision = task.Revision + 1, Usage = AddUsage(task.Usage, usage) });
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Result<LessonIdentity>.Success(identity);
    }
    public Task<IReadOnlyList<GenerationAttempt>> DispatchesAsync(Guid taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = Command(db, null, "SELECT json FROM dispatches WHERE task=$p0 ORDER BY number", taskId.ToString());
        using var reader = cmd.ExecuteReader(); var list = new List<GenerationAttempt>();
        while (reader.Read()) list.Add(Read<GenerationAttempt>(reader.GetString(0))); return Task.FromResult<IReadOnlyList<GenerationAttempt>>(list);
    }
}
