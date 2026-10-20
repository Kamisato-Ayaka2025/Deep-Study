namespace StudyHarness.Contracts;

public sealed record LabFile(string Path, string Text);
public sealed record LabCase(string Id, string Input, string Expected);
public sealed record LabMaterial(string Instructions, IReadOnlyList<LabFile> SourceFiles,
    IReadOnlyList<LabFile> TestFiles, IReadOnlyList<LabCase> Cases, string Assessment,
    string SafetyNotes, IReadOnlyList<string> SourceKeys, int SchemaVersion = 2, string? ExperimentKind = null, string? ClassificationReason = null,
    IReadOnlyList<LabCase>? ManualChecks = null);
public sealed record LabPackage(Guid SubjectId, string NodeKey, string PlanHash, string Hash,
    LabMaterial Material, IReadOnlyList<PlanSource> Sources, DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] GeneralPractice? Practice = null);
public sealed record LabJob(Guid Id, Guid SubjectId, string NodeKey, string Status, int Attempts,
    string? Failure, TokenUsage? Usage, DateTimeOffset StartedAt);
public sealed record LabAttestation(string PackageHash, DateTimeOffset At, string CompletionKind = "userAttested");
public sealed record ConfirmLab(string NodeKey, string PackageHash, bool AcknowledgeSelfTest);
