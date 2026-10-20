namespace StudyHarness.Contracts;

public enum GenerationStatus { Queued, Running, Failed, Cancelled, Ready }
public enum GenerationTrigger { Click, Auto, ManualRetry, Resume, Regenerate }
public sealed record GenerationTask(Guid Id, Guid SubjectId, string NodeKey, string PlanHash, GenerationTrigger Trigger,
    GenerationStatus Status, int Attempts, long Revision, DateTimeOffset CreatedAt, DateTimeOffset? NotBefore,
    string? Failure, TokenUsage? Usage);
public sealed record GenerationControl(GenerationMode Mode, bool AutoPaused, long Revision);
public sealed record RequestGeneration(string NodeKey, GenerationTrigger Trigger);
public sealed record GenerationAttempt(Guid TaskId, int Number, DateTimeOffset DispatchedAt, string Model,
    string? Outcome, TokenUsage? Usage, long TaskRevision = 0);
