namespace StudyHarness.Contracts;

// Private draft: reference files must never be passed to a reader or tutor.
// This contract is not a Runner registration or permission to execute code.
public sealed record LabFile(string Path, string Text);
public sealed record LabCase(string Id, string Input, string Expected);
public sealed record LabTodoInsertion(string Path, string Anchor, string Text);
public sealed record LabDraft(string Instructions, IReadOnlyList<LabFile> StarterFiles,
    IReadOnlyList<LabFile> ReferenceFiles, IReadOnlyList<LabCase> Cases,
    IReadOnlyList<LabTodoInsertion> Insertions, IReadOnlyList<string> SourceKeys, int SchemaVersion = 1);
public sealed record LabDraftPreview(string Instructions, IReadOnlyList<LabFile> StarterFiles,
    IReadOnlyList<LabCase> Cases, IReadOnlyList<LabTodoInsertion> Insertions);
public sealed record LabAvailability(bool CanGenerate, bool CanValidateReference, bool CanStart,
    bool CanJudge, bool CanRestore, string Reason);
