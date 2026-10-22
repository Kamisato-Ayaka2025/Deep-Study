using System.Text.Json;
using System.Text.Json.Serialization;
namespace StudyHarness.Contracts;
public enum ErrorCode
{
    VALIDATION_ERROR, PLAN_NOT_CONFIRMED, PLAN_FROZEN, PREREQUISITES_UNMET, ALREADY_READY,
    TASK_ACTIVE, AUTO_PAUSED, REVISION_CONFLICT, IDEMPOTENCY_CONFLICT, CONSENT_REQUIRED,
    CONSENT_DENIED, SOURCE_UNAVAILABLE, INSUFFICIENT_SOURCES, AUTH_INVALID, MODEL_INVALID,
    QUOTA_EXHAUSTED, TRANSIENT_PROVIDER_ERROR, INVALID_MODEL_OUTPUT, TOOLCHAIN_MISSING,
    ISOLATION_UNAVAILABLE, REFERENCE_NOT_VALIDATED, RUN_CANCELLED, SNAPSHOT_UNAVAILABLE,
    IO_FAILURE, SUBJECT_DELETING
}
public enum GenerationMode { Click, Auto }
public enum ConsentMode { Each, Once }
public sealed record ApiError(ErrorCode Code, string Message, bool Retryable, Guid CorrelationId);
public sealed record Result<T>(bool Ok, T? Value, ApiError? Error, int SchemaVersion = 1)
{
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Fail(ErrorCode code, string message) => new(false, default, new(code, message, false, Guid.NewGuid()));
}
public sealed record Command<T>(Guid RequestId, long ExpectedRevision, T Payload, int SchemaVersion = 1);
public sealed record Query<T>(T Payload, int SchemaVersion = 1);
public sealed record SettingsDto(string Model, GenerationMode GenerationMode, ConsentMode ConsentMode,
    bool HasApiKey, long Revision, int SchemaVersion = 1, Guid? LibraryId = null, string? LibraryPath = null);
public sealed record SaveSettings(string Model, GenerationMode GenerationMode, ConsentMode ConsentMode, string? NewApiKey);
public sealed record ProviderReply(string Text, bool IsFake, int SchemaVersion = 1, TokenUsage? Usage = null, string? Model = null, IReadOnlyList<string>? ReadSourceIds = null, int ToolCalls = 0, string? ReportedModel = null);
public static class Wire
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
