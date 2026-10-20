using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed partial class SqliteLearningStore : ILearningStore, ITheoryPublisher, IGenerationStore, ILabStore, ISourceSelectionHistory, IQuizPresentationStore
{
    private readonly string database;
    private readonly IPlanningStore planning;
    private readonly IOptionRandomSource optionRandom;
    private sealed record StoredLesson(string Hash, string PlanHash, TheoryLesson Content, IReadOnlyList<PlanSource> Sources);
    private sealed record StoredHistory(StoredLesson Original, LessonHistory Public);

    public SqliteLearningStore(string root, IPlanningStore planning, IOptionRandomSource? optionRandom = null)
    {
        this.planning = planning;
        this.optionRandom = optionRandom ?? new CryptoOptionRandomSource();
        Directory.CreateDirectory(root);
        database = Path.Combine(root, "learning.db");
        using var db = Open();
        var version = Convert.ToInt32(Scalar(db, null, "PRAGMA user_version"));
        if (version > 8)
            throw new InvalidDataException("Unsupported learning database version.");
        if (version is > 0 and < 8) DatabaseMigrationBackup.Create(db, database, version);
        using var tx = db.BeginTransaction();
        // Read under the write lock, not only before waiting for it.
        version = Convert.ToInt32(Scalar(db, tx, "PRAGMA user_version"));
        if (version > 8) throw new InvalidDataException("Unsupported learning database version.");
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS lessons(subject TEXT NOT NULL, node TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(subject,node));
            CREATE TABLE IF NOT EXISTS requests(id TEXT PRIMARY KEY, hash TEXT NOT NULL, result TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attempts(id TEXT PRIMARY KEY, subject TEXT NOT NULL, node TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS attempts_node ON attempts(subject,node);
            CREATE TABLE IF NOT EXISTS mastery(subject TEXT NOT NULL, node TEXT NOT NULL, question TEXT NOT NULL, PRIMARY KEY(subject,node,question));
            CREATE TABLE IF NOT EXISTS completions(subject TEXT NOT NULL, node TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(subject,node));
            CREATE TABLE IF NOT EXISTS positions(subject TEXT NOT NULL, node TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(subject,node));
            CREATE TABLE IF NOT EXISTS outbox(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS generation(id TEXT PRIMARY KEY, subject TEXT NOT NULL, node TEXT NOT NULL, state TEXT NOT NULL, json TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS generation_active ON generation(subject,node) WHERE state IN ('Queued','Running');
            CREATE UNIQUE INDEX IF NOT EXISTS generation_single_worker ON generation((1)) WHERE state='Running';
            CREATE TABLE IF NOT EXISTS generation_control(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS dispatches(task TEXT NOT NULL, number INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(task,number));
            CREATE TABLE IF NOT EXISTS lesson_history(subject TEXT NOT NULL,node TEXT NOT NULL,hash TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(subject,node,hash));
            CREATE TABLE IF NOT EXISTS lab_packages(subject TEXT NOT NULL,node TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(subject,node));
            CREATE TABLE IF NOT EXISTS lab_jobs(subject TEXT NOT NULL,node TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(subject,node));
            CREATE TABLE IF NOT EXISTS lab_attestations(subject TEXT NOT NULL,node TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(subject,node));
            CREATE TABLE IF NOT EXISTS deleted_subjects(subject TEXT PRIMARY KEY);
            """);
        Execute(db, tx, "INSERT OR IGNORE INTO generation_control VALUES(1,$p0)", Json(new GenerationControl(GenerationMode.Click, false, 0)));
        if (version < 3) Execute(db, tx, """
            UPDATE attempts SET json=json_set(json,'$.lessonHash',(SELECT json_extract(lessons.json,'$.hash') FROM lessons WHERE lessons.subject=attempts.subject AND lessons.node=attempts.node))
            WHERE json_extract(json,'$.lessonHash') IS NULL;
            """);
        if (version < 5 && Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM pragma_table_info('requests') WHERE name='subject'")) == 0)
        {
            // Legacy response caches have no unambiguous subject ownership.
            // Invalidate caches once; all durable learning facts remain intact.
            Execute(db, tx, "DELETE FROM requests");
            Execute(db, tx, "ALTER TABLE requests ADD COLUMN subject TEXT NOT NULL DEFAULT ''");
        }
        if (version < 6) Execute(db, tx, """
            CREATE TABLE generation_budgets(task_id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE generation_timings(task_id TEXT NOT NULL,phase TEXT NOT NULL,attempt INTEGER NOT NULL,
                started TEXT NOT NULL,ended TEXT NOT NULL,outcome TEXT NOT NULL,usage TEXT,
                PRIMARY KEY(task_id,phase,attempt));
            PRAGMA user_version=6;
            """);
        if (version < 7) Execute(db, tx, "CREATE TABLE IF NOT EXISTS lesson_source_selections(subject TEXT NOT NULL,node TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(subject,node)); PRAGMA user_version=7;");
        if (version < 8) Execute(db, tx, """
            CREATE TABLE quiz_option_orders(
                subject TEXT NOT NULL,node TEXT NOT NULL,lesson_hash TEXT NOT NULL,question TEXT NOT NULL,json TEXT NOT NULL,
                PRIMARY KEY(subject,node,lesson_hash,question));
            PRAGMA user_version=8;
            """);
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false, DefaultTimeout = 10 }.ToString());
        db.Open(); return db;
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params object[] values)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]);
        return cmd;
    }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params object[] values)
    { using var cmd = Command(db, tx, sql, values); return cmd.ExecuteScalar(); }
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params object[] values)
    { using var cmd = Command(db, tx, sql, values); cmd.ExecuteNonQuery(); }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Wire.Options);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Wire.Options)!;
    private static Result<T> Bad<T>(string message = "课程、正文摘要或请求字段无效。") => Result<T>.Fail(ErrorCode.VALIDATION_ERROR, message);
    private static StoredLesson? Lesson(SqliteConnection db, SqliteTransaction? tx, Guid subject, string node) =>
        Scalar(db, tx, "SELECT json FROM lessons WHERE subject=$p0 AND node=$p1", subject.ToString(), node) is string json
            ? Read<StoredLesson>(json) : null;
    private static HashSet<string> Completed(SqliteConnection db, SqliteTransaction? tx, Guid subject)
    {
        using var cmd = Command(db, tx, "SELECT node FROM completions WHERE subject=$p0", subject.ToString());
        using var reader = cmd.ExecuteReader(); var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
    private static bool Mastered(SqliteConnection db, SqliteTransaction? tx, Guid subject, string node, string question) =>
        Scalar(db, tx, "SELECT 1 FROM mastery WHERE subject=$p0 AND node=$p1 AND question=$p2", subject.ToString(), node, question) is not null;
    private static DateTimeOffset? Completion(SqliteConnection db, SqliteTransaction? tx, Guid subject, string node) =>
        Scalar(db, tx, "SELECT at FROM completions WHERE subject=$p0 AND node=$p1", subject.ToString(), node) is string at
            ? DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture) : null;
    private static bool Deleted(SqliteConnection db, SqliteTransaction tx, Guid subject) =>
        Scalar(db, tx, "SELECT 1 FROM deleted_subjects WHERE subject=$p0", subject.ToString()) is not null;

    private async Task<Result<T>> Write<T, TPayload>(Command<TPayload> command, Guid subject, string node,
        object binding, Func<SqliteConnection, SqliteTransaction, PlanningDraft, PlannedNode, Result<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (command.RequestId == Guid.Empty || command.SchemaVersion != 1 || command.ExpectedRevision < 0 || subject == Guid.Empty || string.IsNullOrWhiteSpace(node))
            return Bad<T>();
        // P3 confirmed plans are immutable. Future subject deletion must join this transactional boundary.
        var draft = await planning.GetAsync(subject, ct);
        var hash = PlanRules.Hash(new { command, subject, node, binding });
        using var db = Open(); using var tx = db.BeginTransaction();
        if (Deleted(db, tx, subject)) return Result<T>.Fail(ErrorCode.SUBJECT_DELETING, "学科已删除。");
        using (var cmd = Command(db, tx, "SELECT hash,result FROM requests WHERE id=$p0", command.RequestId.ToString()))
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read()) return reader.GetString(0) == hash ? Read<Result<T>>(reader.GetString(1)) :
                Result<T>.Fail(ErrorCode.IDEMPOTENCY_CONFLICT, "请求标识已用于不同操作。");
        }
        var definition = draft?.Plan?.Candidate.Nodes.SingleOrDefault(n => n.Key == node);
        var result = draft?.Confirmed != true || draft.Plan is null
            ? Result<T>.Fail(ErrorCode.PLAN_NOT_CONFIRMED, "请先确认完整学习计划。")
            : definition is null || definition.Type != "theory" ? Bad<T>("理论课程不存在。")
            : action(db, tx, draft, definition);
        Execute(db, tx, "INSERT INTO requests VALUES($p0,$p1,$p2,$p3)", command.RequestId.ToString(), hash, Json(result), subject.ToString());
        ct.ThrowIfCancellationRequested(); tx.Commit(); return result;
    }

    public Task<Result<LessonIdentity>> PublishAsync(Command<TheoryLesson> command, Guid subjectId, string nodeKey,
        string planHash, IReadOnlyList<PlanSource> sentSources, CancellationToken ct = default)
    {
        // Detach mutable caller lists before the first await/hash/validation.
        var frozen = Read<Command<TheoryLesson>>(Json(command));
        var sources = Read<PlanSource[]>(Json(sentSources));
        return Write(frozen, subjectId, nodeKey, new { operation = "publish", planHash, sources }, (db, tx, draft, node) =>
        {
            if (draft.Plan!.Hash != planHash) return Bad<LessonIdentity>("已确认计划摘要不匹配。");
            if (Lesson(db, tx, subjectId, nodeKey) is not null)
                return Result<LessonIdentity>.Fail(ErrorCode.ALREADY_READY, "已有成功正文不可替换。");
            var valid = LessonRules.Validate(frozen.Payload, sources);
            if (!valid.Ok) return new(false, null, valid.Error);
            if (frozen.Payload.SourceKeys.Count == 0 && !TheoryKnowledgePolicy.CanUseKnowledgeOnly(node))
                return Bad<LessonIdentity>("动态规则或指定资料课程不能无来源发布。");
            return Result<LessonIdentity>.Success(PublishCore(db, tx, subjectId, nodeKey, planHash, frozen.Payload, sources));
        }, ct);
    }

    private static LessonIdentity PublishCore(SqliteConnection db, SqliteTransaction tx, Guid subjectId, string nodeKey,
        string planHash, TheoryLesson content, IReadOnlyList<PlanSource> sources)
    {
        var cited = sources.Where(s => content.SourceKeys.Contains(s.Key)).ToArray();
        var hash = PlanRules.Hash(new { subjectId, nodeKey, planHash, content, sources = cited });
        Execute(db, tx, "INSERT INTO lessons VALUES($p0,$p1,$p2) ON CONFLICT(subject,node) DO UPDATE SET json=excluded.json", subjectId.ToString(), nodeKey, Json(new StoredLesson(hash, planHash, content, cited)));
        AddEvent(db, tx, subjectId, nodeKey, "LessonReady", hash, 1);
        return new(subjectId, nodeKey, hash);
    }

    private static void ArchiveCurrent(SqliteConnection db, SqliteTransaction tx, Guid subject, string node, StoredLesson old)
    {
        var questions = old.Content.Questions.Select(q => {
            var correct = Mastered(db, tx, subject, node, q.Id);
            return QuizProjection.Project(q, new(1, q.Options.Select(o => o.Id).ToArray()), correct);
        }).ToArray();
        var position = Scalar(db, tx, "SELECT json FROM positions WHERE subject=$p0 AND node=$p1", subject.ToString(), node) is string json ? Read<ReadingPosition>(json) : null;
        var reader = new LessonReader(new(subject, node, old.Hash), old.PlanHash, old.Content.Blocks, questions, old.Sources, false, Completion(db, tx, subject, node), position);
        Execute(db, tx, "INSERT OR IGNORE INTO lesson_history VALUES($p0,$p1,$p2,$p3)", subject.ToString(), node, old.Hash,
            Json(new StoredHistory(old, new(old.Hash, DateTimeOffset.UtcNow, reader))));
        Execute(db, tx, "DELETE FROM mastery WHERE subject=$p0 AND node=$p1", subject.ToString(), node);
        Execute(db, tx, "DELETE FROM positions WHERE subject=$p0 AND node=$p1", subject.ToString(), node);
    }
    public Task<IReadOnlyList<LessonHistory>> HistoryAsync(Guid subjectId, string nodeKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, tx, "SELECT json FROM lesson_history WHERE subject=$p0 AND node=$p1 ORDER BY rowid DESC", subjectId.ToString(), nodeKey);
        var stored = new List<StoredHistory>();
        using (var rows = cmd.ExecuteReader()) while (rows.Read()) stored.Add(Read<StoredHistory>(rows.GetString(0)));
        var items = stored.Select(h => h.Public with { Reader = h.Public.Reader with
        {
            Questions = h.Original.Content.Questions.Select(q => QuizProjection.Project(q,
                OptionOrder(db, tx, new(subjectId, nodeKey, h.Original.Hash), q),
                h.Public.Reader.Questions.Single(old => old.Id == q.Id).EverCorrect)).ToArray()
        }}).ToList();
        ct.ThrowIfCancellationRequested(); tx.Commit();
        return Task.FromResult<IReadOnlyList<LessonHistory>>(items);
    }

    public async Task<Result<LessonReader>> GetLessonAsync(Guid subjectId, string nodeKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var draft = await planning.GetAsync(subjectId, ct);
        if (draft?.Confirmed != true || draft.Plan is null)
            return Result<LessonReader>.Fail(ErrorCode.PLAN_NOT_CONFIRMED, "请先确认完整学习计划。");
        var node = draft.Plan.Candidate.Nodes.SingleOrDefault(n => n.Key == nodeKey);
        try
        {
        using var db = Open(); using var tx = db.BeginTransaction();
        if (Deleted(db, tx, subjectId)) return Result<LessonReader>.Fail(ErrorCode.SUBJECT_DELETING, "学科已删除。");
        var lesson = Lesson(db, tx, subjectId, nodeKey);
        if (node is null || lesson is null) return Bad<LessonReader>("正文尚未发布；可从课程图查看标题与目标。");
        var questions = lesson.Content.Questions.Select(q =>
        {
            var mastered = Mastered(db, tx, subjectId, nodeKey, q.Id);
            return QuizProjection.Project(q, OptionOrder(db, tx, new(subjectId, nodeKey, lesson.Hash), q), mastered);
        }).ToArray();
        var position = Scalar(db, tx, "SELECT json FROM positions WHERE subject=$p0 AND node=$p1", subjectId.ToString(), nodeKey) is string json
            ? Read<ReadingPosition>(json) : null;
        var result = Result<LessonReader>.Success(new(new(subjectId, nodeKey, lesson.Hash), lesson.PlanHash, lesson.Content.Blocks,
            questions, lesson.Sources, node.Type == "theory", Completion(db, tx, subjectId, nodeKey), position));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return result;
        }
        catch (Exception e) when (e is SqliteException or JsonException or InvalidDataException)
        {
            return Result<LessonReader>.Fail(ErrorCode.IO_FAILURE, "无法读取或保存选项排列，请检查学习库或恢复备份后重试。");
        }
    }

    public Task<Result<AnswerResult>> SubmitAnswerAsync(Command<SubmitAnswer> command, CancellationToken ct = default)
    {
        if (command.Payload?.Lesson is null) return Task.FromResult(Bad<AnswerResult>());
        var input = command.Payload; var identity = input.Lesson;
        return Write(command, identity.SubjectId, identity.NodeKey, "answer", (db, tx, draft, node) =>
        {
            var lesson = Lesson(db, tx, identity.SubjectId, identity.NodeKey);
            if (lesson is null || lesson.Hash != identity.LessonHash) return Bad<AnswerResult>();
            var question = lesson.Content.Questions.SingleOrDefault(q => q.Id == input.QuestionId);
            if (question is null || !question.Options.Any(o => o.Id == input.OptionId)) return Bad<AnswerResult>();
            QuizOptionOrder order;
            try { order = OptionOrder(db, tx, identity, question); }
            catch (Exception e) when (e is SqliteException or JsonException or InvalidDataException)
            { return Bad<AnswerResult>("选项排列不可用，请检查学习库或恢复备份后重试。"); }
            var correct = question.CorrectOptionId == input.OptionId;
            var attempt = new LearningAttempt(Guid.NewGuid(), input.QuestionId, input.OptionId, correct, DateTimeOffset.UtcNow, identity.LessonHash);
            Execute(db, tx, "INSERT INTO attempts VALUES($p0,$p1,$p2,$p3)", attempt.Id.ToString(), identity.SubjectId.ToString(), identity.NodeKey, Json(attempt));
            if (correct) Execute(db, tx, "INSERT OR IGNORE INTO mastery VALUES($p0,$p1,$p2)", identity.SubjectId.ToString(), identity.NodeKey, input.QuestionId);
            var mastered = Mastered(db, tx, identity.SubjectId, identity.NodeKey, input.QuestionId);
            if (Completion(db, tx, identity.SubjectId, identity.NodeKey) is null &&
                lesson.Content.Questions.All(q => Mastered(db, tx, identity.SubjectId, identity.NodeKey, q.Id)))
            {
                Execute(db, tx, "INSERT INTO completions VALUES($p0,$p1,$p2)", identity.SubjectId.ToString(), identity.NodeKey, attempt.At.ToString("O"));
                var revision = 1 + Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM attempts WHERE subject=$p0 AND node=$p1", identity.SubjectId.ToString(), identity.NodeKey));
                AddEvent(db, tx, identity.SubjectId, identity.NodeKey, "NodeCompleted", lesson.Hash, revision);
            }
            return Result<AnswerResult>.Success(new(attempt.Id, correct, mastered,
                Completion(db, tx, identity.SubjectId, identity.NodeKey) is not null, QuizProjection.Project(question, order, mastered).Answer));
        }, ct);
    }

    public Task<Result<ReadingPosition>> SavePositionAsync(Command<SaveReadingPosition> command, CancellationToken ct = default)
    {
        if (command.Payload?.Lesson is null) return Task.FromResult(Bad<ReadingPosition>());
        var input = command.Payload; var identity = input.Lesson;
        return Write(command, identity.SubjectId, identity.NodeKey, "position", (db, tx, draft, node) =>
        {
            var lesson = Lesson(db, tx, identity.SubjectId, identity.NodeKey);
            var block = lesson?.Content.Blocks.SingleOrDefault(b => b.Id == input.BlockId);
            if (lesson is null || lesson.Hash != identity.LessonHash || block is null || input.Offset < 0 || input.Offset > (block.Text?.Length ?? 0))
                return Bad<ReadingPosition>();
            var position = new ReadingPosition(input.BlockId, input.Offset);
            Execute(db, tx, "INSERT INTO positions VALUES($p0,$p1,$p2) ON CONFLICT(subject,node) DO UPDATE SET json=excluded.json",
                identity.SubjectId.ToString(), identity.NodeKey, Json(position));
            return Result<ReadingPosition>.Success(position);
        }, ct);
    }

    private static void AddEvent(SqliteConnection db, SqliteTransaction tx, Guid subject, string node, string type, string hash, long revision)
    {
        var entry = new LearningEvent(Guid.NewGuid(), subject, node, type, hash, revision, DateTimeOffset.UtcNow);
        Execute(db, tx, "INSERT INTO outbox VALUES($p0,$p1)", entry.EventId.ToString(), Json(entry));
    }
    public Task<IReadOnlySet<string>> CompletedNodesAsync(Guid subjectId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var db = Open(); return Task.FromResult<IReadOnlySet<string>>(Completed(db, null, subjectId)); }
    public Task<IReadOnlyList<LearningAttempt>> AttemptsAsync(Guid subjectId, string nodeKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open();
        using var cmd = Command(db, null, "SELECT json FROM attempts WHERE subject=$p0 AND node=$p1 ORDER BY rowid", subjectId.ToString(), nodeKey);
        using var reader = cmd.ExecuteReader(); var result = new List<LearningAttempt>();
        while (reader.Read()) result.Add(Read<LearningAttempt>(reader.GetString(0)));
        return Task.FromResult<IReadOnlyList<LearningAttempt>>(result);
    }
    public Task<IReadOnlyList<LearningEvent>> PendingEventsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = Command(db, null, "SELECT json FROM outbox ORDER BY rowid");
        using var reader = cmd.ExecuteReader(); var result = new List<LearningEvent>();
        while (reader.Read()) result.Add(Read<LearningEvent>(reader.GetString(0)));
        return Task.FromResult<IReadOnlyList<LearningEvent>>(result);
    }
    public Task AcknowledgeEventAsync(Guid eventId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); Execute(db, null, "DELETE FROM outbox WHERE id=$p0", eventId.ToString());
        return Task.CompletedTask;
    }
}
