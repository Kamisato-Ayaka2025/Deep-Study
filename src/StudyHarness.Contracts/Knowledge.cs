namespace StudyHarness.Contracts;

public sealed record SourceLocator(string SourceLabel, int? Page, string? Heading, string Block);
public sealed record SourceRef(Guid LibraryId, Guid NodeId, Guid VersionId, string ContentHash, SourceLocator Locator, int SchemaVersion = 1);
public sealed record KnowledgeFragment(SourceRef Reference, Guid SourceId, string Text, bool SourceMissing);
public sealed record LibraryDto(Guid Id, string Root, long Revision, int SchemaVersion = 1);
public sealed record ImportFileResult(string SourceLabel, bool Ok, int Added, int Updated, int Unchanged, string? Error);
public sealed record ImportReport(Guid JobId, IReadOnlyList<ImportFileResult> Files, int SchemaVersion = 1, IReadOnlyList<SourceRef>? References = null);
public sealed record KnowledgeSearch(IReadOnlyList<KnowledgeFragment> Fragments, bool Insufficient, string Message);
public sealed record ResolvedSource(SourceRef Reference, string? Text, string? UnavailableReason);
public sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
