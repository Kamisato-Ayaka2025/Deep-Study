using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface IGenerationStore
{
    Task<Result<GenerationTask>> RequestAsync(Guid subjectId, Command<RequestGeneration> command, CancellationToken ct = default);
    Task<IReadOnlyList<GenerationTask>> TasksAsync(CancellationToken ct = default);
    Task<GenerationControl> ControlAsync(CancellationToken ct = default);
    Task SetModeAsync(GenerationMode mode, CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task CancelAsync(Guid taskId, CancellationToken ct = default);
    Task RecoverAsync(CancellationToken ct = default);
    Task<Result<GenerationTask>> DispatchAsync(Guid taskId, long revision, string model, CancellationToken ct = default);
    Task<GenerationTask?> FailAsync(Guid taskId, long revision, ErrorCode code, TokenUsage? usage, CancellationToken ct = default);
    Task<Result<LessonIdentity>> CompleteAsync(Guid taskId, long revision, TheoryLesson lesson,
        IReadOnlyList<PlanSource> sources, TokenUsage? usage, CancellationToken ct = default);
    Task<IReadOnlyList<GenerationAttempt>> DispatchesAsync(Guid taskId, CancellationToken ct = default);
}
public interface ITheoryLessonParser
{
    Result<TheoryLesson> Parse(string text, IReadOnlyList<PlanSource> sources);
}
