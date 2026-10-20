using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed class SqlitePlanningStore : IPlanningStore
{
    private readonly string database;
    public SqlitePlanningStore(string root)
    {
        Directory.CreateDirectory(root); database = Path.Combine(root, "planning.db");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > 2) throw new InvalidDataException("Unsupported planning database version.");
        if (version == 1) DatabaseMigrationBackup.Create(db, database, version);
        using var tx = db.BeginTransaction(); cmd.Transaction = tx;
        // Another initializer may have migrated while this connection waited.
        cmd.CommandText = "PRAGMA user_version";
        version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > 2) throw new InvalidDataException("Unsupported planning database version.");
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS drafts(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS requests(id TEXT PRIMARY KEY, hash TEXT NOT NULL, result TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox(id TEXT PRIMARY KEY, subject_id TEXT NOT NULL, event_type TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        if (version < 2)
        {
            cmd.CommandText = """
                CREATE TABLE plan_v2_metadata(draft_id TEXT PRIMARY KEY,strategy_json TEXT NOT NULL,coverage_json TEXT NOT NULL);
                PRAGMA user_version=2;
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, DefaultTimeout = 10 }.ToString());
        db.Open(); return db;
    }
    public Task<PlanningDraft?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); return Task.FromResult(Read(db, null, id));
    }
    private static PlanningDraft? Read(SqliteConnection db, SqliteTransaction? tx, Guid id)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT json FROM drafts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PlanningDraft>(json, Wire.Options) : null;
    }
    public Task<IReadOnlyList<PlanningDraft>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM drafts ORDER BY rowid DESC"; using var reader = cmd.ExecuteReader();
        var result = new List<PlanningDraft>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<PlanningDraft>(reader.GetString(0), Wire.Options)!);
        return Task.FromResult<IReadOnlyList<PlanningDraft>>(result);
    }
    public Task<Result<PlanningDraft>> StartAsync(Command<PlanningInput> command, CancellationToken ct = default) =>
        Write(command, "start", null, current => PlanRules.ValidInput(command.Payload) && command.ExpectedRevision == 0
            ? Result<PlanningDraft>.Success(new(Guid.NewGuid(), 0, command.Payload, null, false, null))
            : Bad("请求或初始修订号无效。"), ct);

    public Task<Result<PlanningDraft>> ReviseAsync(Command<RevisePlan> command, CancellationToken ct = default) =>
        Write(command, "revise", command.Payload.DraftId, current => PlanRules.ValidInput(command.Payload.Input)
            ? Result<PlanningDraft>.Success(current! with { Input = command.Payload.Input, Plan = null, Revision = current.Revision + 1 })
            : Bad("请求字段无效。"), ct);

    public Task<Result<PlanningDraft>> SaveProposalAsync(Command<PlanDocument> command, Guid id, CancellationToken ct = default) =>
        Write(command, "propose", id, current =>
        {
            var valid = PlanRules.Validate(id, current!.Input, command.Payload.Candidate, command.Payload.Sources, command.Payload.Usage, command.Payload.Collections);
            if (!valid.Ok || valid.Value!.Hash != command.Payload.Hash ||
                PlanRules.Hash(valid.Value.Weeks) != PlanRules.Hash(command.Payload.Weeks)) return Bad("计划摘要或安排无效。");
            return Result<PlanningDraft>.Success(current with { Plan = command.Payload, Revision = current.Revision + 1 });
        }, ct);

    public Task<Result<PlanningDraft>> ConfirmAsync(Command<ConfirmPlan> command, CancellationToken ct = default) =>
        Write(command, "confirm", command.Payload.DraftId, current =>
        {
            if (!command.Payload.AckFrozen || current!.Plan is null || current.Plan.Hash != command.Payload.PlanHash)
                return Bad("请重新核对完整计划及不可修改提示，旧摘要不能确认。");
            if (current.Input.WeeklyMinutes == 0 || current.Plan.Candidate.Clarifications.Count != 0 || current.Plan.Candidate.Strategy?.Clarifications.Count > 0 || current.Plan.Candidate.Scope == "OutOfScope")
                return Bad("请先解决时间、目标或范围问题后重新拟定计划。");
            return Result<PlanningDraft>.Success(current with { Confirmed = true, ConfirmedAt = DateTimeOffset.UtcNow, Revision = current.Revision + 1 });
        }, ct);

    private static Result<PlanningDraft> Bad(string message) => Result<PlanningDraft>.Fail(ErrorCode.VALIDATION_ERROR, message);
    private Task<Result<PlanningDraft>> Write<T>(Command<T> command, string operation, Guid? id,
        Func<PlanningDraft?, Result<PlanningDraft>> change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (command.RequestId == Guid.Empty || command.SchemaVersion != 1 || command.ExpectedRevision < 0) return Task.FromResult(Bad("命令无效。"));
        // The hash binds operation, target, revision and payload for idempotent replay.
        var hash = PlanRules.Hash(new { operation, id, command });
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT hash,result FROM requests WHERE id=$id"; cmd.Parameters.AddWithValue("$id", command.RequestId.ToString());
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read()) return Task.FromResult(reader.GetString(0) == hash
                ? JsonSerializer.Deserialize<Result<PlanningDraft>>(reader.GetString(1), Wire.Options)!
                : Result<PlanningDraft>.Fail(ErrorCode.IDEMPOTENCY_CONFLICT, "请求标识已用于不同操作。"));
        }
        var current = id is { } identity ? Read(db, tx, identity) : null;
        Result<PlanningDraft> result;
        if (id is not null && current is null) result = Bad("草稿不存在。");
        else if (current?.Confirmed == true) result = Result<PlanningDraft>.Fail(ErrorCode.PLAN_FROZEN, "确认后的目标、时间、目录与实验方案不可修改。");
        else if (current is not null && current.Revision != command.ExpectedRevision) result = Result<PlanningDraft>.Fail(ErrorCode.REVISION_CONFLICT, "草稿已改变，请重新载入。");
        else result = change(current);
        if (result.Ok)
        {
            cmd.CommandText = "INSERT INTO drafts VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", result.Value!.Id.ToString());
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result.Value, Wire.Options)); cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM plan_v2_metadata WHERE draft_id=$id";
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", result.Value.Id.ToString()); cmd.ExecuteNonQuery();
            if (result.Value.Plan?.Candidate is { SchemaVersion: 2 } adaptive)
            {
                cmd.CommandText = "INSERT INTO plan_v2_metadata VALUES($id,$strategy,$coverage)";
                cmd.Parameters.AddWithValue("$strategy", JsonSerializer.Serialize(adaptive.Strategy, Wire.Options));
                cmd.Parameters.AddWithValue("$coverage", JsonSerializer.Serialize(adaptive.Coverage, Wire.Options)); cmd.ExecuteNonQuery();
            }
            if (operation == "confirm")
            {
                cmd.CommandText = "INSERT INTO outbox VALUES($event,$id,'PlanConfirmed')";
                cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$event", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("$id", result.Value.Id.ToString()); cmd.ExecuteNonQuery();
            }
        }
        cmd.CommandText = "INSERT INTO requests VALUES($id,$hash,$result)"; cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$id", command.RequestId.ToString()); cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, Wire.Options)); cmd.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.FromResult(result);
    }
}
