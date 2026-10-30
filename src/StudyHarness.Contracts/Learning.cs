using System.Text.Json.Serialization;

namespace StudyHarness.Contracts;

public sealed record LessonOption(string Id, string Text);
public sealed record LessonQuestion(string Id, string Prompt, IReadOnlyList<LessonOption> Options,
    string CorrectOptionId, string Explanation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReferenceAnswer = null);
public sealed record LessonBlock(string Id, string Kind, string? Text = null, string? QuestionId = null);
// Private host/model contract. Never send this object to a reader or WebView.
public sealed record TheoryLesson(IReadOnlyList<LessonBlock> Blocks, IReadOnlyList<LessonQuestion> Questions,
    IReadOnlyList<string> SourceKeys, int SchemaVersion = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LessonEvidenceCheck>? ContextChecks = null);
public sealed record LessonEvidenceCheck(string Key, bool Supported, IReadOnlyList<string> SourceKeys, IReadOnlyList<string> BlockIds, string Explanation);
public sealed record LessonIdentity(Guid SubjectId, string NodeKey, string LessonHash);
public sealed record SubmitAnswer(LessonIdentity Lesson, string QuestionId, string OptionId, string? ResponseText = null);
public sealed record SaveReadingPosition(LessonIdentity Lesson, string BlockId, int Offset);
public sealed record ReadingPosition(string BlockId, int Offset);
public sealed record AnswerDisclosure(string CorrectOptionId, string Explanation,
    string? CorrectDisplayLabel = null, IReadOnlyList<OptionLabelMapping>? OriginalLabels = null);
public sealed record ReaderQuestion(string Id, string Prompt, IReadOnlyList<PresentedLessonOption> Options, bool EverCorrect,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnswerDisclosure? Answer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResponseText = null);
public sealed record LessonReader(LessonIdentity Identity, string PlanHash, IReadOnlyList<LessonBlock> Blocks,
    IReadOnlyList<ReaderQuestion> Questions, IReadOnlyList<PlanSource> Sources, bool CanAnswer,
    DateTimeOffset? CompletedAt, ReadingPosition? Position, string? CourseType = null);
public sealed record AnswerResult(Guid AttemptId, bool Correct, bool EverCorrect, bool Completed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnswerDisclosure? Answer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResponseText = null);
public sealed record LearningAttempt(Guid Id, string QuestionId, string OptionId, bool Correct, DateTimeOffset At, string? LessonHash = null, string? Kind = null, string? ResponseText = null);
public sealed record LessonHistory(string LessonHash, DateTimeOffset ArchivedAt, LessonReader Reader);
public sealed record LearningEvent(Guid EventId, Guid SubjectId, string NodeKey, string EventType,
    string LessonHash, long AggregateRevision, DateTimeOffset OccurredAt);
