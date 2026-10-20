using StudyHarness.Application;
using StudyHarness.Contracts;
using Microsoft.Data.Sqlite;

namespace StudyHarness.Infrastructure;

public sealed partial class SqliteLearningStore
{
    private static T? LabRow<T>(SqliteConnection db, SqliteTransaction? tx, string table, Guid subject, string node) where T : class =>
        Scalar(db, tx, $"SELECT json FROM {table} WHERE subject=$p0 AND node=$p1", subject.ToString(), node) is string json ? Read<T>(json) : null;
    private static void LabSave<T>(SqliteConnection db, SqliteTransaction tx, string table, Guid subject, string node, T value) =>
        Execute(db, tx, $"INSERT INTO {table} VALUES($p0,$p1,$p2) ON CONFLICT(subject,node) DO UPDATE SET json=excluded.json", subject.ToString(), node, Json(value));
    public Task<LabPackage?> GetLabAsync(Guid subject, string node, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var db = Open(); return Task.FromResult(LabRow<LabPackage>(db, null, "lab_packages", subject, node)); }
    public Task<LabJob?> GetLabJobAsync(Guid subject, string node, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var db = Open(); return Task.FromResult(LabRow<LabJob>(db, null, "lab_jobs", subject, node)); }
    public Task<LabAttestation?> GetLabAttestationAsync(Guid subject, string node, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var db = Open(); return Task.FromResult(LabRow<LabAttestation>(db, null, "lab_attestations", subject, node)); }
    public async Task<Result<LabJob>> BeginLabAsync(Guid subject, string node, CancellationToken ct = default)
    {
        var draft = await planning.GetAsync(subject, ct);
        var definition = draft?.Plan?.Candidate.Nodes.SingleOrDefault(n => n.Key == node);
        using var db = Open(); using var tx = db.BeginTransaction();
        if (Deleted(db, tx, subject)) return Bad<LabJob>("学科已删除。");
        if (draft?.Confirmed != true || definition?.Type != "lab") return Bad<LabJob>("先确认有效实验计划。");
        if (LabRow<LabPackage>(db, tx, "lab_packages", subject, node) is not null) return Result<LabJob>.Fail(ErrorCode.ALREADY_READY, "已有材料可阅读或重新导出。");
        if (LabRow<LabJob>(db, tx, "lab_jobs", subject, node)?.Status == "running") return Result<LabJob>.Fail(ErrorCode.TASK_ACTIVE, "正在生成，请勿重复请求。");
        var job = new LabJob(Guid.NewGuid(), subject, node, "running", 0, null, null, DateTimeOffset.UtcNow);
        LabSave(db, tx, "lab_jobs", subject, node, job); ct.ThrowIfCancellationRequested(); tx.Commit(); return Result<LabJob>.Success(job);
    }
    public Task<bool> UpdateLabJobAsync(LabJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        var current = LabRow<LabJob>(db, tx, "lab_jobs", job.SubjectId, job.NodeKey);
        if (current?.Id != job.Id || current.Status != "running") return Task.FromResult(false);
        LabSave(db, tx, "lab_jobs", job.SubjectId, job.NodeKey, job); tx.Commit(); return Task.FromResult(true);
    }
    public async Task<Result<LabPackage>> PublishLabAsync(LabJob job, LabMaterial material, string planHash, IReadOnlyList<PlanSource> sources, CancellationToken ct = default)
    {
        var frozen = Read<LabMaterial>(Json(material)); var sent = Read<PlanSource[]>(Json(sources));
        var draft = await planning.GetAsync(job.SubjectId, ct);
        using var db = Open(); using var tx = db.BeginTransaction();
        var current = LabRow<LabJob>(db, tx, "lab_jobs", job.SubjectId, job.NodeKey);
        if (current?.Id != job.Id || current.Status != "running") return Result<LabPackage>.Fail(ErrorCode.RUN_CANCELLED, "任务已结束，迟到材料未发布。");
        var node = draft?.Plan?.Candidate.Nodes.SingleOrDefault(n => n.Key == job.NodeKey);
        if (draft?.Confirmed != true || draft.Plan!.Hash != planHash || node?.Type != "lab" || node.Lab is null ||
            node.Lab.Toolchain is "paper-exercise" or "manual-project" ||
            draft.Plan.Candidate.SchemaVersion == 2 && node.Lab.Kind != frozen.ExperimentKind ||
            sent.Any(s => !CourseMaterials.Allowed(draft.Plan, node, s)) ||
            (node.Lab.Toolchain == "portable-cpp" ? frozen.SchemaVersion != 4 : frozen.SchemaVersion == 4) ||
            !LabMaterialRules.Validate(frozen, sent.Select(s => s.Key).ToArray()).Ok)
            return Result<LabPackage>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "材料或冻结来源不匹配。");
        if (LabRow<LabPackage>(db, tx, "lab_packages", job.SubjectId, job.NodeKey) is not null) return Result<LabPackage>.Fail(ErrorCode.ALREADY_READY, "材料已存在。");
        var cited = sent.Where(s => frozen.SourceKeys.Contains(s.Key)).ToArray();
        var hash = PlanRules.Hash(new { job.SubjectId, job.NodeKey, planHash, material = frozen, sources = cited });
        var package = new LabPackage(job.SubjectId, job.NodeKey, planHash, hash, frozen, cited, DateTimeOffset.UtcNow);
        LabSave(db, tx, "lab_packages", job.SubjectId, job.NodeKey, package);
        LabSave(db, tx, "lab_jobs", job.SubjectId, job.NodeKey, job with { Status = "ready", Failure = null });
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Result<LabPackage>.Success(package);
    }
    public async Task<Result<LabPackage>> PublishPracticeAsync(LabJob job, GeneralPractice material, string planHash,
        IReadOnlyList<PlanSource> sources, CancellationToken ct = default)
    {
        var frozen = Read<GeneralPractice>(Json(material)); var sent = Read<PlanSource[]>(Json(sources));
        var draft = await planning.GetAsync(job.SubjectId, ct);
        using var db = Open(); using var tx = db.BeginTransaction();
        var current = LabRow<LabJob>(db, tx, "lab_jobs", job.SubjectId, job.NodeKey);
        if (Deleted(db, tx, job.SubjectId) || current?.Id != job.Id || current.Status != "running")
            return Result<LabPackage>.Fail(ErrorCode.RUN_CANCELLED, "任务已结束，迟到材料未发布。");
        var node = draft?.Plan?.Candidate.Nodes.SingleOrDefault(n => n.Key == job.NodeKey);
        if (draft?.Confirmed != true || draft.Plan!.Hash != planHash || node?.Type != "lab" || node.Lab is null ||
            node.Lab.Toolchain != frozen.Capability || node.Lab.Kind != frozen.Kind ||
            sent.Any(s => !CourseMaterials.Allowed(draft.Plan, node, s)) ||
            !GeneralPracticeRules.Validate(frozen, sent.Select(s => s.Key).ToArray()).Ok)
            return Result<LabPackage>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "通用材料与冻结计划或来源不一致。");
        if (LabRow<LabPackage>(db, tx, "lab_packages", job.SubjectId, job.NodeKey) is not null)
            return Result<LabPackage>.Fail(ErrorCode.ALREADY_READY, "材料已存在。");
        var cited = sent.Where(s => frozen.Sources.Contains(s.Key)).ToArray();
        var hash = PlanRules.Hash(new { job.SubjectId, job.NodeKey, planHash, practice = frozen, sources = cited });
        var package = new LabPackage(job.SubjectId, job.NodeKey, planHash, hash, GeneralPracticeRules.Presentation(frozen), cited, DateTimeOffset.UtcNow, frozen);
        LabSave(db, tx, "lab_packages", job.SubjectId, job.NodeKey, package);
        LabSave(db, tx, "lab_jobs", job.SubjectId, job.NodeKey, job with { Status = "ready", Failure = null });
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Result<LabPackage>.Success(package);
    }
    public async Task<Result<LabAttestation>> ConfirmLabAsync(Guid subject, Command<ConfirmLab> command, CancellationToken ct = default)
    {
        if (command.RequestId == Guid.Empty || command.SchemaVersion != 1 || !command.Payload.AcknowledgeSelfTest) return Bad<LabAttestation>("请明确确认已自行测评；软件未验证。");
        var draft = await planning.GetAsync(subject, ct); var node = draft?.Plan?.Candidate.Nodes.SingleOrDefault(n => n.Key == command.Payload.NodeKey);
        using var db = Open(); using var tx = db.BeginTransaction();
        if (Deleted(db, tx, subject)) return Bad<LabAttestation>("学科已删除。");
        var hash = PlanRules.Hash(new { subject, command });
        using (var cmd = Command(db, tx, "SELECT hash,result FROM requests WHERE id=$p0", command.RequestId.ToString()))
        using (var reader = cmd.ExecuteReader())
            if (reader.Read()) return reader.GetString(0) == hash ? Read<Result<LabAttestation>>(reader.GetString(1)) : Result<LabAttestation>.Fail(ErrorCode.IDEMPOTENCY_CONFLICT, "请求标识冲突。");
        if (draft?.Confirmed != true || node?.Type != "lab") return Result<LabAttestation>.Fail(ErrorCode.PLAN_NOT_CONFIRMED, "先确认有效实验计划。");
        var package = LabRow<LabPackage>(db, tx, "lab_packages", subject, node.Key);
        if (package is null || package.Hash != command.Payload.PackageHash) return Bad<LabAttestation>("材料不存在或已改变。");
        var attestation = LabRow<LabAttestation>(db, tx, "lab_attestations", subject, node.Key);
        if (attestation is null)
        {
            attestation = new(package.Hash, DateTimeOffset.UtcNow);
            LabSave(db, tx, "lab_attestations", subject, node.Key, attestation);
            Execute(db, tx, "INSERT OR IGNORE INTO completions VALUES($p0,$p1,$p2)", subject.ToString(), node.Key, attestation.At.ToString("O"));
            AddEvent(db, tx, subject, node.Key, "NodeCompleted", package.Hash, 1);
        }
        var result = Result<LabAttestation>.Success(attestation);
        Execute(db, tx, "INSERT INTO requests VALUES($p0,$p1,$p2,$p3)", command.RequestId.ToString(), hash, Json(result), subject.ToString());
        ct.ThrowIfCancellationRequested(); tx.Commit(); return result;
    }
    public Task RecoverLabsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        var jobs = new List<LabJob>();
        using (var cmd = Command(db, tx, "SELECT json FROM lab_jobs")) using (var reader = cmd.ExecuteReader()) while (reader.Read()) jobs.Add(Read<LabJob>(reader.GetString(0)));
        foreach (var job in jobs.Where(j => j.Status == "running")) LabSave(db, tx, "lab_jobs", job.SubjectId, job.NodeKey, job with { Status = "failed", Failure = "INTERRUPTED_MAY_BE_BILLED" });
        tx.Commit(); return Task.CompletedTask;
    }
}
