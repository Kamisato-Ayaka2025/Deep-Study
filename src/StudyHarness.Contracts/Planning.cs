using System.Text.Json.Serialization;

namespace StudyHarness.Contracts;

public sealed record PlanningInput(string Request, string? Background, string? Goal, int? WeeklyMinutes,
    string Scope, IReadOnlyList<SourceRef> PreferredReferences,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Guid>? ExamCollectionIds = null);
public sealed record PlanDefault(string Field, string Value, string Reason);
public sealed record PlannedLab(string Mode, string? PreviousLabKey, string Toolchain,
    IReadOnlyList<string> SourceKeys, string Rationale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);
public sealed record PlannedNode(string Key, string Title, string Type, IReadOnlyList<string> Objectives,
    IReadOnlyList<string> PrerequisiteKeys, int EstimatedMinutes, PlannedLab? Lab,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AlgorithmKeys = null);
public sealed record PlannedAlgorithm(string Key, string Name);
public sealed record PlanCandidate(string Title, string Background, string Goal, int WeeklyMinutes,
    string Scope, IReadOnlyList<PlanDefault> Defaults, IReadOnlyList<string> Clarifications,
    IReadOnlyList<string> Limitations, IReadOnlyList<string> SourceKeys, IReadOnlyList<PlannedNode> Nodes,
    int SchemaVersion = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PlannedAlgorithm>? Algorithms = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StrategyDecision? Strategy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PlanCoverage>? Coverage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<KnowledgeCoverage>? KnowledgeCoverage = null);
public sealed record PlanCoverage(string EntryKey, string State, IReadOnlyList<string> NodeKeys, string? Reason);
public sealed record PlanSource(string Key, SourceRef Reference, bool UserPreferred);
public sealed record PlanWeek(int Index, IReadOnlyList<string> NodeKeys, int EstimatedMinutes);
public sealed record PlanDocument(PlanCandidate Candidate, IReadOnlyList<PlanSource> Sources,
    IReadOnlyList<PlanWeek> Weeks, string Hash, TokenUsage? Usage, IReadOnlyList<string>? ReadSourceIds = null, int ToolCalls = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PlanCollection>? Collections = null);
public sealed record PlanningDraft(Guid Id, long Revision, PlanningInput Input, PlanDocument? Plan,
    bool Confirmed, DateTimeOffset? ConfirmedAt, int SchemaVersion = 1);
public sealed record ConfirmPlan(Guid DraftId, string PlanHash, bool AckFrozen);
public sealed record RevisePlan(Guid DraftId, PlanningInput Input);
public sealed record CourseNodeView(string Key, string Title, string Type, string Objectives, int Minutes,
    int Layer, IReadOnlyList<string> Prerequisites, bool Eligible, string Status);
