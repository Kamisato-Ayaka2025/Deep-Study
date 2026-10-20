using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class LearningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-learning", Guid.NewGuid().ToString());
    private static readonly PlanSource[] Sources = [new("s1", new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), new("synthetic.md", null, "集合", "1")), false)];
    private SqliteLearningStore Store() => new(root, new SqlitePlanningStore(root));
    private static Command<T> Cmd<T>(T value) => new(Guid.NewGuid(), 0, value);
    private static TheoryLesson Content(int count = 2) => new(
        new[] { new LessonBlock("example", "example", "例如集合 {1,2} 有两个元素。") }
            .Concat(Enumerable.Range(1, count).Select(i => new LessonBlock("b" + i, "question", QuestionId: "q" + i))).ToArray(),
        Enumerable.Range(1, count).Select(i => new LessonQuestion("q" + i, "有几个元素？", [new("a", "2"), new("b", "3")], "a", "集合包含1和2，共两个元素。")).ToArray(), ["s1"]);
    private async Task<PlanningDraft> Plan(bool confirm = true)
    {
        var planning = new SqlitePlanningStore(root);
        var input = new PlanningInput("学习集合", "零基础", "理解集合", 120, "Math", []);
        var draft = (await planning.StartAsync(Cmd(input))).Value!;
        var candidate = new PlanCandidate("集合", "零基础", "理解集合", 120, "Math", [], [], [], ["s1"],
            [new("A", "A", "theory", ["集合"], [], 20, null), new("B", "B", "theory", ["集合"], [], 20, null),
             new("C", "C", "theory", ["集合"], ["A"], 20, null), new("D", "D", "theory", ["集合"], ["A", "B"], 20, null)]);
        var doc = PlanRules.Validate(draft.Id, input, candidate, Sources, null); Assert.True(doc.Ok);
        draft = (await planning.SaveProposalAsync(Cmd(doc.Value!), draft.Id)).Value!;
        return confirm ? (await planning.ConfirmAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, doc.Value!.Hash, true)))).Value! : draft;
    }
    private async Task<LessonIdentity> Publish(PlanningDraft plan, string key = "A", int count = 2)
    {
        var result = await Store().PublishAsync(Cmd(Content(count)), plan.Id, key, plan.Plan!.Hash, Sources);
        Assert.True(result.Ok, result.Error?.Message); return result.Value!;
    }
    private async Task<AnswerResult> Answer(LessonIdentity id, string question, string option)
    {
        var result = await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(id, question, option)));
        Assert.True(result.Ok); return result.Value!;
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(10, true)] [InlineData(11, false)]
    public void QuestionCount_Boundaries(int count, bool valid) => Assert.Equal(valid, LessonRules.Validate(Content(count), Sources).Ok);

    [Fact]
    public void ModelParser_RejectsAuthorityFieldsAndMalformedOutput_WithoutEchoingContent()
    {
        var parser = new TheoryLessonParser();
        var json = JsonSerializer.Serialize(Content(), Wire.Options);
        Assert.True(parser.Parse(json, Sources).Ok);
        foreach (var field in new[] { "completed", "status", "script", "apiKey", "path" })
        {
            var value = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            value[field] = "sensitive-marker";
            var result = parser.Parse(value.ToJsonString(), Sources);
            Assert.False(result.Ok); Assert.DoesNotContain("sensitive-marker", result.Error!.Message);
        }
        foreach (var invalid in new[] { "null", "{", "[]", new string('x', 262145), json.Replace("\"schemaVersion\":1", "\"schemaVersion\":99") })
            Assert.False(parser.Parse(invalid, Sources).Ok);
        Assert.False(parser.Parse(json, []).Ok);
    }

    [Fact]
    public async Task ConcurrentPublication_IsUnique_ReplayAndCancellationDoNotReplaceContent()
    {
        var plan = await Plan(); var command = Cmd(Content());
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            Store().PublishAsync(command with { RequestId = Guid.NewGuid() }, plan.Id, "A", plan.Plan!.Hash, Sources))));
        Assert.Single(results, r => r.Ok);
        Assert.All(results.Where(r => !r.Ok), r => Assert.Equal(ErrorCode.ALREADY_READY, r.Error!.Code));
        var b = await Store().PublishAsync(command, plan.Id, "B", plan.Plan!.Hash, Sources);
        Assert.Equal(b, await Store().PublishAsync(command, plan.Id, "B", plan.Plan!.Hash, Sources));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(b.Value!, "q1", "a")), cancellation.Token));
        Assert.Empty(await Store().AttemptsAsync(plan.Id, "B"));
    }

    [Fact]
    public void InvalidStructureAndInventedSources_AreRejected()
    {
        var lesson = Content();
        // R-MATERIAL-TOL-01 permits a conclusion after the quiz; content and grading remain mandatory.
        Assert.True(LessonRules.Validate(lesson with { Blocks = lesson.Blocks.Append(new("end", "paragraph", "end")).ToArray() }, Sources).Ok);
        TheoryLesson[] invalid = [
            lesson with { Blocks = lesson.Blocks.Skip(1).ToArray() },
            lesson with { Blocks = lesson.Blocks.Append(lesson.Blocks[^1]).ToArray() },
            lesson with { SourceKeys = ["invented"] },
            lesson with { Questions = [lesson.Questions[0] with { CorrectOptionId = "missing" }, lesson.Questions[1]] },
            lesson with { Questions = [lesson.Questions[0] with { Explanation = "" }, lesson.Questions[1]] },
            lesson with { Questions = [lesson.Questions[0] with { Options = [new("a", "same"), new("b", " same ")] }, lesson.Questions[1]] },
            lesson with { Blocks = [new("bad", "html", "<script>alert(1)</script>"), .. lesson.Blocks] },
            lesson with { Blocks = [new("bad", "paragraph", "text", "q1"), .. lesson.Blocks] },
            lesson with { Questions = null! }, lesson with { Blocks = null! }, lesson with { SchemaVersion = 99 }
        ];
        Assert.All(invalid, value => Assert.False(LessonRules.Validate(value, Sources).Ok));
        Assert.False(LessonRules.Validate(lesson, [Sources[0] with { Reference = Sources[0].Reference with { ContentHash = "invalid" } }]).Ok);
        Assert.False(LessonRules.Validate(lesson, [Sources[0] with { Reference = Sources[0].Reference with { VersionId = Guid.Empty } }]).Ok);
    }

    [Fact]
    public async Task CorrectnessIsHostOwned_AnswersHiddenUntilMastery_CompletionNeverReverts()
    {
        var plan = await Plan(); var id = await Publish(plan);
        var reader = (await Store().GetLessonAsync(plan.Id, "A")).Value!;
        var json = JsonSerializer.Serialize(reader, Wire.Options);
        Assert.DoesNotContain("correctOptionId", json); Assert.DoesNotContain("explanation", json); Assert.DoesNotContain("\"answer\"", json);
        Assert.Null((await Answer(id, "q1", "b")).Answer);
        Assert.False((await Answer(id, "q1", "a")).Completed);
        Assert.False((await Answer(id, "q1", "b")).Correct);
        reader = (await Store().GetLessonAsync(plan.Id, "A")).Value!;
        Assert.NotNull(reader.Questions[0].Answer); Assert.Null(reader.Questions[1].Answer);
        Assert.True((await Answer(id, "q2", "a")).Completed);
        var completedAt = (await Store().GetLessonAsync(plan.Id, "A")).Value!.CompletedAt;
        Assert.True((await Answer(id, "q2", "b")).Completed);
        Assert.Equal(completedAt, (await Store().GetLessonAsync(plan.Id, "A")).Value!.CompletedAt);
        Assert.Equal(5, (await Store().AttemptsAsync(plan.Id, "A")).Count);
        Assert.Single(await Store().PendingEventsAsync(), e => e.EventType == "NodeCompleted");
    }

    [Fact]
    public async Task ReplayIsStable_ConflictingPayloadRejected_NewRequestRecordsAttempt()
    {
        var plan = await Plan(); var id = await Publish(plan);
        var command = Cmd(new SubmitAnswer(id, "q1", "b"));
        var first = await Store().SubmitAnswerAsync(command);
        await Answer(id, "q1", "a");
        var replay = await Store().SubmitAnswerAsync(command);
        Assert.Equal(first, replay); Assert.Null(replay.Value!.Answer);
        Assert.Equal(ErrorCode.IDEMPOTENCY_CONFLICT, (await Store().SubmitAnswerAsync(command with { Payload = command.Payload with { OptionId = "a" } })).Error!.Code);
        Assert.True((await Store().SubmitAnswerAsync(command with { RequestId = Guid.NewGuid() })).Ok);
        Assert.Equal(3, (await Store().AttemptsAsync(plan.Id, "A")).Count);
    }

    [Fact]
    public async Task FreeOrderPublication_PreservesConfirmationHashAndImmutablePublication()
    {
        var draft = await Plan(false);
        Assert.Equal(ErrorCode.PLAN_NOT_CONFIRMED, (await Store().PublishAsync(Cmd(Content()), draft.Id, "A", draft.Plan!.Hash, Sources)).Error!.Code);
        var plan = await Plan();
        Assert.True((await Store().PublishAsync(Cmd(Content()), plan.Id, "C", plan.Plan!.Hash, Sources)).Ok);
        Assert.False((await Store().PublishAsync(Cmd(Content()), plan.Id, "A", "stale", Sources)).Ok);
        var id = await Publish(plan);
        Assert.Equal(ErrorCode.ALREADY_READY, (await Store().PublishAsync(Cmd(Content(1)), plan.Id, "A", plan.Plan!.Hash, Sources)).Error!.Code);
        await Answer(id, "q1", "a"); await Answer(id, "q2", "a");
        Assert.True((await Store().GetLessonAsync(plan.Id, "C")).Ok);
        Assert.DoesNotContain("B", await Store().CompletedNodesAsync(plan.Id));
        Assert.True((await Store().PublishAsync(Cmd(Content()), plan.Id, "D", plan.Plan!.Hash, Sources)).Ok);
    }

    [Fact]
    public async Task PositionAndAttempts_RestoreIndependently_InvalidIdentityDoesNotWrite()
    {
        var plan = await Plan(); var id = await Publish(plan);
        var saved = await Store().SavePositionAsync(Cmd(new SaveReadingPosition(id, "example", 4)));
        Assert.True(saved.Ok); Assert.Empty(await Store().CompletedNodesAsync(plan.Id));
        Assert.Equal(saved.Value, (await Store().GetLessonAsync(plan.Id, "A")).Value!.Position);
        foreach (var position in new[] { new SaveReadingPosition(id, "missing", 0), new(id, "example", -1), new(id, "example", 999), new(id with { LessonHash = "stale" }, "example", 1) })
            Assert.False((await Store().SavePositionAsync(Cmd(position))).Ok);
        Assert.False((await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(id with { LessonHash = "stale" }, "q1", "a")))).Ok);
        Assert.False((await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(id, "q1", "invented")))).Ok);
        Assert.False((await Store().SubmitAnswerAsync(Cmd<SubmitAnswer>(null!))).Ok);
        Assert.False((await Store().SavePositionAsync(Cmd<SaveReadingPosition>(null!))).Ok);
        Assert.False((await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(id, "q1", "a")) with { ExpectedRevision = -1 })).Ok);
        Assert.Empty(await Store().AttemptsAsync(plan.Id, "A"));
        Assert.Equal(saved.Value, (await Store().GetLessonAsync(plan.Id, "A")).Value!.Position);
    }

    [Fact]
    public async Task ConcurrentConnections_RecordEveryDistinctAttempt_CompleteExactlyOnce()
    {
        var plan = await Plan(); var id = await Publish(plan, count: 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(id, "q1", "a"))))));
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(12, (await Store().AttemptsAsync(plan.Id, "A")).Count);
        Assert.Single(await Store().PendingEventsAsync(), e => e.EventType == "NodeCompleted");
        var retry = Cmd(new SubmitAnswer(id, "q1", "a"));
        results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store().SubmitAnswerAsync(retry))));
        Assert.Single(results.Select(r => r.Value!.AttemptId).Distinct());
        Assert.Equal(13, (await Store().AttemptsAsync(plan.Id, "A")).Count);
    }

    private void Sql(string sql)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "learning.db")};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task OutboxFailure_RollsBackPublicationAndCompletion_AllowsSameRequestRetry()
    {
        var plan = await Plan(); var store = Store();
        Sql("CREATE TRIGGER fail_event BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT,'injected write failure'); END;");
        var publish = Cmd(Content(1));
        await Assert.ThrowsAsync<SqliteException>(() => store.PublishAsync(publish, plan.Id, "A", plan.Plan!.Hash, Sources));
        Assert.False((await Store().GetLessonAsync(plan.Id, "A")).Ok);
        Assert.Empty(await Store().PendingEventsAsync());
        Sql("DROP TRIGGER fail_event;");
        var id = (await store.PublishAsync(publish, plan.Id, "A", plan.Plan!.Hash, Sources)).Value!;
        Sql("CREATE TRIGGER fail_event BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT,'injected write failure'); END;");
        var answer = Cmd(new SubmitAnswer(id, "q1", "a"));
        await Assert.ThrowsAsync<SqliteException>(() => store.SubmitAnswerAsync(answer));
        Assert.Empty(await Store().AttemptsAsync(plan.Id, "A")); Assert.Empty(await Store().CompletedNodesAsync(plan.Id));
        Assert.Null((await Store().GetLessonAsync(plan.Id, "A")).Value!.Questions[0].Answer);
        Sql("DROP TRIGGER fail_event;");
        Assert.True((await store.SubmitAnswerAsync(answer)).Value!.Completed);
    }

    [Fact]
    public async Task HistoricalLesson_CanAnswerAndSavePositionWithoutPrerequisiteCompletion()
    {
        var plan = await Plan(); var a = await Publish(plan, count: 1); await Answer(a, "q1", "a");
        var c = await Publish(plan, "C");
        // Synthetic historical/imported state only. Product API never removes completion.
        Sql("DELETE FROM completions;");
        Assert.True((await Store().GetLessonAsync(plan.Id, "C")).Value!.CanAnswer);
        Assert.True((await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(c, "q1", "a")))).Ok);
        Assert.True((await Store().SavePositionAsync(Cmd(new SaveReadingPosition(c, "example", 0)))).Ok);
        Assert.Single(await Store().AttemptsAsync(plan.Id, "C"));
        Assert.DoesNotContain("A", await Store().CompletedNodesAsync(plan.Id));
    }

    [Fact]
    public async Task SubjectsAndEvents_AreIndependent_UnknownDatabaseVersionRejected()
    {
        var a = await Plan(); var b = await Plan(); var id = await Publish(a, count: 1); await Publish(b);
        await Answer(id, "q1", "a");
        Assert.Empty(await Store().CompletedNodesAsync(b.Id)); Assert.Empty(await Store().AttemptsAsync(b.Id, "A"));
        var events = await Store().PendingEventsAsync();
        await Store().AcknowledgeEventAsync(events[0].EventId); await Store().AcknowledgeEventAsync(events[0].EventId);
        Assert.Equal(events.Count - 1, (await Store().PendingEventsAsync()).Count);
        Sql("PRAGMA user_version=99;"); Assert.Throws<InvalidDataException>(() => Store());
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
