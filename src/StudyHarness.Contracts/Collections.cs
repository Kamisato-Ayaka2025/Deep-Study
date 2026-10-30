namespace StudyHarness.Contracts;

public sealed record SourceCollection(Guid Id, string SourceKind, string SourceFingerprint, DateTimeOffset CreatedAt,
    string Label, string Version, int EntryCount, int ReadyCount);
public sealed record CollectionChunk(Guid Id, Guid VersionId, string EntryKey, string ContentHash, int Start, int End, string Text, SourceRef Reference);
public sealed record CollectionSummary(string EntryKey, IReadOnlyList<string> ParentHashes, string Text);
public sealed record CollectionSnapshot(SourceCollection Collection, CoverageManifest Manifest, IReadOnlyList<SourceRef> References,
    IReadOnlyList<CollectionSummary>? Summaries = null, StructureIndex? Structure = null);
public sealed record PlanCollection(Guid LibraryId, CoverageManifest Manifest, IReadOnlyList<SourceRef> References,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] StructureIndex? Structure = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BookKnowledgeUnit>? KnowledgeUnits = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SourceKind = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Purpose = null);
