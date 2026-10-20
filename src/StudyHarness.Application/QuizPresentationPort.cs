using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface IOptionRandomSource
{
    // Uniform integer in [0, exclusiveUpperBound), without modulo bias.
    int NextIndex(int exclusiveUpperBound);
}

public interface IQuizPresentationStore
{
    // Only a committed, validated mapping may be returned. Existing mappings win.
    Task<Result<QuizOptionOrder>> GetOrCreateOptionOrderAsync(
        LessonIdentity lesson, string questionId, CancellationToken ct = default);
}
