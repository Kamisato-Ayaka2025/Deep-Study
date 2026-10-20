using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface ILearningStore
{
    Task<Result<LessonReader>> GetLessonAsync(Guid subjectId, string nodeKey, CancellationToken ct = default);
    Task<IReadOnlyList<LessonHistory>> HistoryAsync(Guid subjectId, string nodeKey, CancellationToken ct = default);
    Task<Result<AnswerResult>> SubmitAnswerAsync(Command<SubmitAnswer> command, CancellationToken ct = default);
    Task<Result<ReadingPosition>> SavePositionAsync(Command<SaveReadingPosition> command, CancellationToken ct = default);
    Task<IReadOnlySet<string>> CompletedNodesAsync(Guid subjectId, CancellationToken ct = default);
    Task<IReadOnlyList<LearningAttempt>> AttemptsAsync(Guid subjectId, string nodeKey, CancellationToken ct = default);
    Task<IReadOnlyList<LearningEvent>> PendingEventsAsync(CancellationToken ct = default);
    Task AcknowledgeEventAsync(Guid eventId, CancellationToken ct = default);
}

// Host-only publishing port for M06. Sources must be the actual read-and-sent set from
// the authorized dispatch, not model claims. Not exposed to the Desktop/Reader API.
// P4-002 must make cancellation/task-state checks atomic with this publication.
public interface ITheoryPublisher
{
    Task<Result<LessonIdentity>> PublishAsync(Command<TheoryLesson> command, Guid subjectId, string nodeKey,
        string planHash, IReadOnlyList<PlanSource> sentSources, CancellationToken ct = default);
}
