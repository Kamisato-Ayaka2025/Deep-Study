namespace StudyHarness.Contracts;

public sealed record SourceRegistryEntry(string Id, string Publisher, string CanonicalHost,
    IReadOnlyList<string> AllowedPathPrefixes, IReadOnlyList<string> AllowedSubdomains,
    IReadOnlyList<string> Subjects, IReadOnlyList<string> Languages, IReadOnlyList<string> Levels,
    IReadOnlyList<string> ContentRoles, string AuthorityBasis, string ReviewedAt, string Status, string RegistryVersion, IReadOnlyList<string>? StartingPages = null);
public sealed record PageAssessment(string SnapshotId, string PageKind, IReadOnlyList<string> TopicEvidence,
    bool UsableForTeaching, IReadOnlyList<string> Reasons, IReadOnlyList<string> QualityFlags,
    IReadOnlyList<SourceSpan> TeachingSpans);
public sealed record SourceSpan(string EntryKey, int? Page, int Start, int End);
public sealed record StructureNode(string Id, string? ParentId, string Title, int Level,
    string Confidence, IReadOnlyList<SourceSpan> Spans);
public sealed record SemanticUnit(string Id, string Kind, string? ParentUnitId,
    IReadOnlyList<string> ContinuationIds, IReadOnlyList<string> PrerequisiteUnitIds,
    string ContentHash, IReadOnlyList<SourceSpan> Spans, IReadOnlyList<SourceRef> References);
public sealed record StructureIndex(string SourceHash, string IndexVersion, string ChunkerVersion,
    IReadOnlyList<StructureNode> Nodes, IReadOnlyList<SemanticUnit> Units);
public sealed record LessonSourceSelection(string PlanHash, string LessonKey, string RegistryVersion,
    string IndexVersion, string ChunkerVersion, IReadOnlyList<PlanSource> Sources,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> MissingObjectives,
    IReadOnlyList<string> ExcludedIds, int BodyCharacters, int EstimatedInputTokens);
