namespace StudyHarness.Contracts;

// Host-frozen evidence. Model labels never grant access to additional files.
public sealed record BookKnowledgePoint(string Key, string Title, string Detail, IReadOnlyList<string> EvidenceIds);
public sealed record BookKnowledgeUnit(string Key, string Fingerprint, IReadOnlyList<PlanSource> Sources,
    IReadOnlyList<BookKnowledgePoint> Points, IReadOnlyList<string> NonTeachingIds);
public sealed record KnowledgeCoverage(string KnowledgeKey, string State, IReadOnlyList<string> NodeKeys, string? Reason,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ContextKeys = null);
