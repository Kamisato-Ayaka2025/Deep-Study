using StudyHarness.Contracts;
namespace StudyHarness.Application;
public interface ISettingsStore
{
    Task<SettingsDto> ReadAsync(CancellationToken cancellationToken);
    Task<Result<SettingsDto>> SaveAsync(Command<SaveSettings> command, CancellationToken cancellationToken);
}
public interface IModelProvider
{
    string Destination { get; }
    Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken cancellationToken);
}
public sealed class SettingsService(ISettingsStore store, IKnowledgeStore? knowledge = null)
{
    public async Task<SettingsDto> GetAsync(CancellationToken ct = default)
    {
        var value = await store.ReadAsync(ct);
        var library = knowledge is null ? null : await knowledge.CurrentAsync(ct);
        return value with { LibraryId = library?.Id, LibraryPath = library?.Root };
    }
    public Task<Result<SettingsDto>> SaveAsync(Command<SaveSettings> command, CancellationToken ct = default)
    {
        if (command is null || command.Payload is null || command.SchemaVersion != 1 || command.RequestId == Guid.Empty ||
            command.ExpectedRevision < 0 || !Domain.SettingsPolicy.IsValidModel(command.Payload.Model) ||
            !Enum.IsDefined(command.Payload.GenerationMode) || !Enum.IsDefined(command.Payload.ConsentMode) ||
            command.Payload.NewApiKey is { Length: > 4096 } ||
            command.Payload.NewApiKey is not null && string.IsNullOrWhiteSpace(command.Payload.NewApiKey))
            return Task.FromResult(Result<SettingsDto>.Fail(ErrorCode.VALIDATION_ERROR, "请输入有效模型名称及设置；密钥留空表示保留。"));
        return store.SaveAsync(command, ct);
    }
}
