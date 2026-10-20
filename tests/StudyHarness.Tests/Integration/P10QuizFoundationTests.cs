using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Domain;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class P10QuizFoundationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-p10", Guid.NewGuid().ToString("N"));
    private static readonly PlanSource[] Sources = [new("s1", new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new string('A', 64), new("synthetic.md", null, "矩阵", "1")), false)];
    private sealed class FixedRandom : IOptionRandomSource
    {
        public int Calls { get; private set; }
        public int NextIndex(int bound) { Calls++; return 0; }
    }
    private sealed class CancellingRandom(CancellationTokenSource cancellation) : IOptionRandomSource
    {
        public int NextIndex(int bound) { cancellation.Cancel(); return 0; }
    }
    private SqliteLearningStore Store(IOptionRandomSource? random = null) => new(root, new SqlitePlanningStore(root), random);
    private static Command<T> Cmd<T>(T value) => new(Guid.NewGuid(), 0, value);

    // Fixed fixtures retain literal code, marked/unmarked math, and original-letter explanation.
    private static TheoryLesson Content(int schema, int choices) => new(
        [new("intro", "paragraph", "裸 x_1 保持文字；$x_1$ 可显示为公式。"),
         new("example", "example", "先计算矩阵 A 的各行。$$A(u+v)=Au+Av$$"),
         new("code", "code", "int n = 42;\n\t// 中文 <script>alert(1)</script>\nconst char* s = \"a\\\\b\";"),
         new("fence", "paragraph", "```cpp\nfor (int i=0; i<2; ++i) {}\n```\n```unknown\n<svg onload=alert(1)>\n```"),
         new("matrix", "math", @"\begin{pmatrix}1&2\\3&4\end{pmatrix}"),
         new("question", "question", QuestionId: "q1")],
        [new("q1", "集合 {1,2} 有几个元素？",
            Enumerable.Range(0, choices).Select(i => new LessonOption("id" + i, (i + 2).ToString())).ToArray(),
            "id0", "原选项A正确：集合有2个元素。矩阵 A 与变量 a 不是选项编号。")], ["s1"], schema);

    private async Task<(PlanningDraft Plan, LessonIdentity Lesson)> Publish(int choices = 4)
    {
        var planning = new SqlitePlanningStore(root);
        var input = new PlanningInput("学习集合", "零基础", "理解集合", 120, "Math", []);
        var draft = (await planning.StartAsync(Cmd(input))).Value!;
        var candidate = new PlanCandidate("集合", "零基础", "理解集合", 120, "Math", [], [], [], ["s1"],
            [new("A", "A", "theory", ["集合"], [], 20, null)]);
        var plan = PlanRules.Validate(draft.Id, input, candidate, Sources, null);
        Assert.True(plan.Ok, plan.Error?.Message);
        draft = (await planning.SaveProposalAsync(Cmd(plan.Value!), draft.Id)).Value!;
        draft = (await planning.ConfirmAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, plan.Value!.Hash, true)))).Value!;
        var published = await Store().PublishAsync(Cmd(Content(2, choices)), draft.Id, "A", draft.Plan!.Hash, Sources);
        Assert.True(published.Ok, published.Error?.Message);
        return (draft, published.Value!);
    }

    private object? Sql(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "learning.db"), Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
    }

    [Theory]
    [InlineData(1, 2, true)] [InlineData(1, 3, true)] [InlineData(1, 6, true)]
    [InlineData(2, 2, true)] [InlineData(2, 3, true)] [InlineData(2, 4, true)] [InlineData(2, 10, true)]
    [InlineData(3, 2, false)] [InlineData(3, 3, false)] [InlineData(3, 4, true)] [InlineData(3, 5, false)]
    public void VersionedChoices_PreserveLegacyAndDoNotRejectSoftMath(int schema, int choices, bool valid)
    {
        var lesson = Content(schema, choices);
        Assert.Equal(valid, LessonRules.Validate(lesson, Sources).Ok);
        Assert.Equal(valid, new TheoryLessonParser().Parse(JsonSerializer.Serialize(lesson, Wire.Options), Sources).Ok);
    }

    [Fact]
    public void LongInlineMathAndBareSubscripts_RemainValidWithoutRepair()
    {
        var lesson = Content(3, 4);
        lesson = lesson with { Blocks = [new("long-inline", "paragraph",
            "未排版 x_1 与长行内公式 $" + string.Join("+", Enumerable.Repeat("x_{1234}", 900)) + "$。"), .. lesson.Blocks] };
        var parsed = new TheoryLessonParser().Parse(JsonSerializer.Serialize(lesson, Wire.Options), Sources);
        Assert.True(parsed.Ok);
        Assert.Equal(lesson.Blocks[0].Text, parsed.Value!.Blocks[0].Text);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(10)]
    public async Task CommittedOrder_SurvivesReopenAndAnswers_WithoutChangingFacts(int choices)
    {
        var (_, lesson) = await Publish(choices);
        var original = Sql("SELECT json FROM lessons");
        var random = new FixedRandom();
        var first = await Store(random).GetOrCreateOptionOrderAsync(lesson, "q1");
        Assert.True(first.Ok);
        Assert.Equal(choices - 1, random.Calls);
        Assert.True(OptionPermutation.IsPermutation(Content(2, choices).Questions[0].Options.Select(o => o.Id).ToArray(), first.Value!.OrderedOptionIds));
        var wrong = await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(lesson, "q1", "id1")));
        Assert.True(wrong.Ok); Assert.Null(wrong.Value!.Answer);
        var right = await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(lesson, "q1", "id0")));
        Assert.True(right.Value!.Completed);
        Assert.Equal("id0", right.Value.Answer!.CorrectOptionId);
        var second = await Store(random).GetOrCreateOptionOrderAsync(lesson, "q1");
        Assert.Equal(first.Value.OrderedOptionIds, second.Value!.OrderedOptionIds);
        Assert.Equal(choices - 1, random.Calls);
        Assert.Equal(original, Sql("SELECT json FROM lessons"));
        Assert.Equal(2, (await Store().AttemptsAsync(lesson.SubjectId, lesson.NodeKey)).Count);
        Assert.Contains(lesson.NodeKey, await Store().CompletedNodesAsync(lesson.SubjectId));
    }

    [Fact]
    public async Task ConcurrentFirstReaders_AllReceiveSameCommittedWinner()
    {
        var (_, lesson) = await Publish();
        var orders = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => Store().GetOrCreateOptionOrderAsync(lesson, "q1"))));
        Assert.All(orders, r => Assert.True(r.Ok, r.Error?.Message));
        Assert.All(orders, r => Assert.Equal(orders[0].Value!.OrderedOptionIds, r.Value!.OrderedOptionIds));
        Assert.Equal(1L, Sql("SELECT count(*) FROM quiz_option_orders"));
    }

    [Fact]
    public async Task ReaderProjection_StoresBeforeDisplay_OnlyRevealsMappingAfterCorrectAnswer()
    {
        var (_, lesson) = await Publish();
        var reader = await Store(new FixedRandom()).GetLessonAsync(lesson.SubjectId, lesson.NodeKey);
        Assert.True(reader.Ok);
        Assert.Equal(1L, Sql("SELECT count(*) FROM quiz_option_orders"));
        Assert.Equal(new[] { "id1", "id2", "id3", "id0" }, reader.Value!.Questions[0].Options.Select(o => o.Id));
        Assert.Equal(new[] { "A", "B", "C", "D" }, reader.Value.Questions[0].Options.Select(o => o.DisplayLabel));
        Assert.DoesNotContain("originalLabels", JsonSerializer.Serialize(reader.Value, Wire.Options));
        var right = await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(lesson, "q1", "id0")));
        Assert.Equal("D", right.Value!.Answer!.CorrectDisplayLabel);
        Assert.Equal(new OptionLabelMapping("A", "D"), right.Value.Answer.OriginalLabels![0]);
        Assert.Contains("矩阵 A 与变量 a", right.Value.Answer.Explanation);
        Sql("UPDATE quiz_option_orders SET json='{}'");
        var damaged = await Store().GetLessonAsync(lesson.SubjectId, lesson.NodeKey);
        Assert.False(damaged.Ok); Assert.Equal(ErrorCode.IO_FAILURE, damaged.Error!.Code);
        Assert.Single(await Store().AttemptsAsync(lesson.SubjectId, lesson.NodeKey));
    }

    [Fact]
    public async Task ConcurrentReaderProjection_UsesCommittedWinner()
    {
        var (_, lesson) = await Publish();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => Store().GetLessonAsync(lesson.SubjectId, lesson.NodeKey))));
        Assert.All(results, result => Assert.True(result.Ok));
        Assert.All(results, result => Assert.Equal(results[0].Value!.Questions[0].Options, result.Value!.Questions[0].Options));
        Assert.Equal(1L, Sql("SELECT count(*) FROM quiz_option_orders"));
    }

    [Fact]
    public async Task CorruptionAndFutureAlgorithm_FailWithoutRewashingOrLeakingContent()
    {
        var (_, lesson) = await Publish();
        Assert.True((await Store().GetOrCreateOptionOrderAsync(lesson, "q1")).Ok);
        foreach (var broken in new[] { "null", "{}", "{", """{"algorithmVersion":99,"orderedOptionIds":["id0","id1","id2","id3"]}""",
                     """{"algorithmVersion":1,"orderedOptionIds":["id0","id0","id2","id3"]}""" })
        {
            Sql("UPDATE quiz_option_orders SET json='" + broken + "'");
            var result = await Store().GetOrCreateOptionOrderAsync(lesson, "q1");
            Assert.False(result.Ok);
            Assert.Equal(broken, Sql("SELECT json FROM quiz_option_orders"));
        }
    }

    [Fact]
    public async Task CancellationStaleIdentityAndFailedWrite_DoNotPublishOrder()
    {
        var (_, lesson) = await Publish();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().GetOrCreateOptionOrderAsync(lesson, "q1", cancelled.Token));
        Assert.False((await Store().GetOrCreateOptionOrderAsync(lesson with { LessonHash = new string('B', 64) }, "q1")).Ok);
        Assert.False((await Store().GetOrCreateOptionOrderAsync(lesson, "missing")).Ok);
        Sql("CREATE TRIGGER fail_order BEFORE INSERT ON quiz_option_orders BEGIN SELECT RAISE(ABORT,'synthetic'); END");
        Assert.False((await Store().GetOrCreateOptionOrderAsync(lesson, "q1")).Ok);
        Assert.Equal(0L, Sql("SELECT count(*) FROM quiz_option_orders"));
        Sql("DROP TRIGGER fail_order");
        Assert.True((await Store().GetOrCreateOptionOrderAsync(lesson, "q1")).Ok);
    }

    [Fact]
    public async Task CancellationAfterSampling_RollsBackInsertedMapping()
    {
        var (_, lesson) = await Publish();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Store(new CancellingRandom(cancellation)).GetOrCreateOptionOrderAsync(lesson, "q1", cancellation.Token));
        Assert.Equal(0L, Sql("SELECT count(*) FROM quiz_option_orders"));
        Assert.True((await Store().GetOrCreateOptionOrderAsync(lesson, "q1")).Ok);
    }

    [Fact]
    public async Task Migration_BackupRetainsV7BytesAndFailureRollsBack()
    {
        var (_, lesson) = await Publish();
        var original = Sql("SELECT json FROM lessons");
        Sql("DROP TABLE quiz_option_orders; PRAGMA user_version=7; CREATE VIEW quiz_option_orders AS SELECT json FROM lessons");
        Assert.Throws<SqliteException>(() => Store());
        Assert.Equal(7L, Sql("PRAGMA user_version"));
        Assert.Equal(original, Sql("SELECT json FROM lessons"));
        Assert.NotEmpty(Directory.GetFiles(root, "learning.db.v7-*.bak.verified"));
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Directory.GetFiles(root, "learning.db.v7-*.bak").Single(), Pooling = false }.ToString()))
        {
            backup.Open(); using var check = backup.CreateCommand();
            check.CommandText = "PRAGMA user_version"; Assert.Equal(7L, check.ExecuteScalar());
            check.CommandText = "SELECT json FROM lessons"; Assert.Equal(original, check.ExecuteScalar());
            check.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", check.ExecuteScalar());
        }
        Sql("DROP VIEW quiz_option_orders");
        Assert.True((await Store().GetOrCreateOptionOrderAsync(lesson, "q1")).Ok);
        Assert.Equal(8L, Sql("PRAGMA user_version"));
        Assert.Equal(original, Sql("SELECT json FROM lessons"));
        var backupCount = Directory.GetFiles(root, "learning.db.v7-*.bak").Length;
        _ = Store();
        Assert.Equal(backupCount, Directory.GetFiles(root, "learning.db.v7-*.bak").Length);
    }

    [Fact]
    public async Task SubjectDeletion_IsAtomicAndDoesNotDeleteOtherOrders()
    {
        var (plan, lesson) = await Publish();
        Assert.True((await Store().GetOrCreateOptionOrderAsync(lesson, "q1")).Ok);
        Sql("INSERT INTO quiz_option_orders VALUES('other','A','hash','q1','{}')");
        Sql("CREATE TRIGGER fail_delete BEFORE DELETE ON quiz_option_orders BEGIN SELECT RAISE(ABORT,'synthetic'); END");
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteSubjectLifecycle(root).DeleteAsync(plan.Id, plan.Revision, true));
        Assert.Equal(2L, Sql("SELECT count(*) FROM quiz_option_orders"));
        Assert.Equal(0L, Sql("SELECT count(*) FROM deleted_subjects"));
        Sql("DROP TRIGGER fail_delete");
        Assert.True((await new SqliteSubjectLifecycle(root).DeleteAsync(plan.Id, plan.Revision, true)).Ok);
        Assert.Equal("other", Sql("SELECT subject FROM quiz_option_orders"));
        Assert.Equal(ErrorCode.SUBJECT_DELETING, (await Store().GetOrCreateOptionOrderAsync(lesson, "q1")).Error!.Code);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
