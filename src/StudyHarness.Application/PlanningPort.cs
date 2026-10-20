using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface IPlanningStore
{
    Task<PlanningDraft?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PlanningDraft>> ListAsync(CancellationToken ct = default);
    Task<Result<PlanningDraft>> StartAsync(Command<PlanningInput> command, CancellationToken ct = default);
    Task<Result<PlanningDraft>> ReviseAsync(Command<RevisePlan> command, CancellationToken ct = default);
    Task<Result<PlanningDraft>> SaveProposalAsync(Command<PlanDocument> command, Guid id, CancellationToken ct = default);
    Task<Result<PlanningDraft>> ConfirmAsync(Command<ConfirmPlan> command, CancellationToken ct = default);
}
public interface IPlanParser
{
    Result<PlanCandidate> Parse(string text);
}
