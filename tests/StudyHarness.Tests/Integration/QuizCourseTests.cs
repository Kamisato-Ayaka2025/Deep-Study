using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class QuizCourseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-quiz", Guid.NewGuid().ToString("N"));
    private static Command<T> Cmd<T>(T value) => new(Guid.NewGuid(), 0, value);
    private static readonly PlanSource[] Sources = [new("s1", new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new('A', 64), new("exam.md", 1, "试卷", "1")), false)];
    private static PlanningInput Input => new("复习", "基础", "考试", 120, "Adaptive", [], ExamCollectionIds: [Guid.NewGuid()]);
    private static PlanCandidate Candidate => new("复习", "基础", "考试", 120, "Adaptive", [], [], [], ["s1"],
        [new("n1", "期末测验", "quiz", ["计算地址数量"], [], 40, null)], 2,
        Strategy: new("计网", ["quiz"], "原题测验", [], [], []), Coverage: []);
    private static string Reply => JsonSerializer.Serialize(new
    {
        blocks = new[] { new { id = "unwanted", kind = "paragraph", text = "不应显示的正文" } },
        questions = new LessonQuestion[] {
            new("original1", "原题Q1：/24地址数量，含网络和广播地址？", [], "", "按32-24计算。", "written", "参考答案标记：256"),
            new("original2", "IPv4地址位数？", [new("a","32"),new("b","16"),new("c","64"),new("d","128")], "a", "IPv4为32位。", "choice", "") },
        sourceKeys = new[] { "s1" }, schemaVersion = 5
    }, Wire.Options);

    [Fact]
    public void QuizProtocol_PreservesWrittenQuestions_DropsProseAndRecoversNonstandardChoices()
    {
        var result = QuizLesson.Parse(Reply, Sources); Assert.True(result.Ok, result.Error?.Message);
        Assert.All(result.Value!.Blocks, b => Assert.Equal("question", b.Kind));
        Assert.Equal("written", result.Value.Questions[0].Kind); Assert.Empty(result.Value.Questions[0].Options);
        Assert.Null(result.Value.Questions[1].Kind);
        var fallback = QuizLesson.Parse(Reply.Replace("\"correctOptionId\":\"a\"", "\"correctOptionId\":\"missing\""), Sources);
        Assert.True(fallback.Ok); Assert.Equal("written", fallback.Value!.Questions[1].Kind);
        Assert.Contains("32", fallback.Value.Questions[1].Prompt);
    }

    [Fact]
    public void NewPlans_AllowOptionalLabsAndQuizCount_LocalPaperConversionPreservesDependencies()
    {
        var parsed = new PlanParser().Parse(JsonSerializer.Serialize(Candidate, Wire.Options));
        Assert.True(parsed.Ok, parsed.Error?.Message);
        var many = Candidate with { Nodes = Enumerable.Range(1, 3).Select(i => Candidate.Nodes[0] with { Key = "n" + i }).ToArray() };
        Assert.True(PlanRules.Validate(Guid.NewGuid(), Input, many, Sources, null).Ok);
        var without = PlanCandidateNormalization.Apply(Candidate, Input with { ExamCollectionIds = [] }, Sources);
        Assert.Equal("theory", without.Nodes[0].Type); Assert.Equal(["theory"], without.Strategy!.TeachingModes);
        var paper = Candidate with { Nodes = [Candidate.Nodes[0] with { Type = "lab", Lab = new("independent", null, "paper-exercise", ["s1"], "手算", "exercise") },
            new("n2", "工程实践", "lab", ["配置路由器"], ["n1"], 30, new("continuous", "n1", "manual-project", ["s1"], "操作", "project"))] };
        var normalized = PlanCandidateNormalization.Apply(paper, Input, Sources);
        Assert.Equal("theory", normalized.Nodes[0].Type); Assert.Null(normalized.Nodes[0].Lab);
        Assert.Null(normalized.Nodes[1].Lab!.PreviousLabKey); Assert.Equal(["n1"], normalized.Nodes[1].PrerequisiteKeys);
        Assert.True(PlanRules.Validate(Guid.NewGuid(), Input, normalized, Sources, null).Ok);
    }

    [Fact]
    public async Task MixedQuiz_HidesAnswers_PersistsResponsesAndSelfAssessmentAcrossRestart()
    {
        var plans = new SqlitePlanningStore(root); var draft = (await plans.StartAsync(Cmd(Input))).Value!;
        var doc = PlanRules.Validate(draft.Id, draft.Input, Candidate, Sources, null); Assert.True(doc.Ok, doc.Error?.Message);
        draft = (await plans.SaveProposalAsync(Cmd(doc.Value!), draft.Id)).Value!;
        draft = (await plans.ConfirmAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, doc.Value!.Hash, true)))).Value!;
        SqliteLearningStore Store() => new(root, new SqlitePlanningStore(root));
        var lesson = QuizLesson.Parse(Reply, Sources).Value!;
        var published = await Store().PublishAsync(Cmd(lesson), draft.Id, "n1", doc.Value!.Hash, Sources); Assert.True(published.Ok, published.Error?.Message);
        var identity = published.Value!;
        var reader = (await Store().GetLessonAsync(draft.Id, "n1")).Value!;
        Assert.Equal("quiz", reader.CourseType); Assert.DoesNotContain("参考答案标记", JsonSerializer.Serialize(reader, Wire.Options));
        Assert.False((await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(identity, "q1", "self-correct")))).Ok);
        var reveal = await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(identity, "q1", "reveal", "2的8次方=256")));
        Assert.True(reveal.Ok); Assert.False(reveal.Value!.Completed); Assert.Contains("参考答案标记", reveal.Value.Answer!.Explanation);
        Assert.True((await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(identity, "q1", "self-review")))).Ok);
        Assert.False((await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(identity, "q2", "a")))).Value!.Completed);
        var result = await Store().SubmitAnswerAsync(Cmd(new SubmitAnswer(identity, "q1", "self-correct")));
        Assert.True(result.Value!.Completed);
        reader = (await Store().GetLessonAsync(draft.Id, "n1")).Value!;
        Assert.Equal("2的8次方=256", reader.Questions[0].ResponseText); Assert.NotNull(reader.CompletedAt);
        var attempts = await Store().AttemptsAsync(draft.Id, "n1");
        Assert.Contains(attempts, a => a.Kind == "written-response"); Assert.Contains(attempts, a => a.Kind == "self-assessed" && a.Correct);
        var task = (await Store().RequestAsync(draft.Id, Cmd(new RequestGeneration("n1", GenerationTrigger.Regenerate)))).Value!;
        task = (await Store().DispatchAsync(task.Id, task.Revision, "deepseek-flash")).Value!;
        var next = lesson with { Questions = [lesson.Questions[0] with { Prompt = "重生成Q1：/24地址数？" }, lesson.Questions[1]] };
        Assert.True((await Store().CompleteAsync(task.Id, task.Revision, next, Sources, null)).Ok);
        var history = Assert.Single(await Store().HistoryAsync(draft.Id, "n1"));
        Assert.Equal("quiz", history.Reader.CourseType); Assert.False(history.Reader.CanAnswer);
        Assert.Equal("2的8次方=256", history.Reader.Questions[0].ResponseText);
        Assert.Contains("参考答案标记", history.Reader.Questions[0].Answer!.Explanation);
        Assert.Null((await Store().GetLessonAsync(draft.Id, "n1")).Value!.Questions[0].Answer);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
