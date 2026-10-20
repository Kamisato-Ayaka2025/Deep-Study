using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed record TaskBudgetRecord(Guid SubjectId, Guid TaskId, string Model, ContextBudget Budget, DateTimeOffset StartedAt);
public sealed record TaskTiming(Guid TaskId, string Phase, int Attempt, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Outcome, TokenUsage? Usage);
public interface ITaskTelemetry
{
    Task SaveBudgetAsync(TaskBudgetRecord record, CancellationToken ct = default);
    Task RecordAsync(TaskTiming timing, CancellationToken ct = default);
}

internal sealed class TaskTrace(ITaskTelemetry? telemetry, Guid taskId, DateTimeOffset started, CancellationToken ct) : IAsyncDisposable
{
    public string Outcome { get; set; } = "not_published";
    public TokenUsage? Usage { get; private set; }
    public void Add(TokenUsage? usage)
    { if (usage is not null) Usage = Usage is null ? usage : new(Usage.PromptTokens + usage.PromptTokens, Usage.CompletionTokens + usage.CompletionTokens, Usage.TotalTokens + usage.TotalTokens); }
    public async ValueTask DisposeAsync()
    {
        if (telemetry is not null) await telemetry.RecordAsync(new(taskId, "generation", 0, started, DateTimeOffset.UtcNow,
            ct.IsCancellationRequested ? "RUN_CANCELLED" : Outcome, Usage));
    }
}
