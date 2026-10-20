using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface ISubjectLifecycle
{
    Task<Result<bool>> DeleteAsync(Guid subject, long revision, bool confirmed, CancellationToken ct = default);
}
