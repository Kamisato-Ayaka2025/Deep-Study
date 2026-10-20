using StudyHarness.Contracts;
namespace StudyHarness.Application;

// Only trusted host UI creates selections. These tokens are never deserialized
// from model responses, and are not accepted as remote tool arguments.
public sealed class HostSelection
{
    private HostSelection(string path, bool directory, bool project = false, bool plan = false) => (Path, IsDirectory, IsProject, IsPlanImport) = (System.IO.Path.GetFullPath(path), directory, project, plan);
    public string Path { get; }
    public bool IsDirectory { get; }
    public bool IsProject { get; }
    public bool IsPlanImport { get; }
    public static HostSelection FromPlanFilePicker(string path) => new(path, false, true, true);
    public static HostSelection FromPlanFolderPicker(string path) => new(path, true, true, true);
    public static HostSelection FromProjectPicker(string path) => new(path, true, true);
    public static HostSelection FromFilePicker(string path) => new(path, false);
    public static HostSelection FromFolderPicker(string path) => new(path, true);
}
public interface IKnowledgeStore
{
    Task<LibraryDto?> CurrentAsync(CancellationToken ct = default);
    Task<Result<LibraryDto>> SwitchAsync(Command<HostSelection> command, CancellationToken ct = default);
    Task<Result<ImportReport>> ImportAsync(Command<HostSelection> command, Guid libraryId, CancellationToken ct = default);
    Task<KnowledgeSearch> SearchAsync(Guid libraryId, string topic, IReadOnlyCollection<Guid> preferredSources, int limit = 20, CancellationToken ct = default);
    Task<ResolvedSource> ResolveAsync(SourceRef reference, CancellationToken ct = default);
}
public interface ICredentialReader
{
    Task<string?> ReadKeyAsync(CancellationToken ct);
}
