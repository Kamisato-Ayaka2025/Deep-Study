using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed record RenderedPage(Guid CollectionId, string SourceFingerprint, int Page, string RenderVersion, byte[] Png, string ImageHash);
public sealed record OcrPageResult(string Text, IReadOnlyList<string> QualityFlags, TokenUsage? Usage, string Model, string PromptVersion);
public interface IOcrKnowledge
{
    Task<IReadOnlyList<RenderedPage>> RenderPagesAsync(Guid library, Guid collection, IReadOnlyList<int> pages, CancellationToken ct);
    Task<OcrPageResult?> CachedOcrAsync(Guid library, RenderedPage page, string model, string promptVersion, CancellationToken ct);
    Task SaveOcrAsync(Guid library, RenderedPage page, OcrPageResult result, CancellationToken ct);
    Task RecordOcrAttemptAsync(Guid library, RenderedPage page, string model, int attempt, DateTimeOffset started, string outcome, TokenUsage? usage);
}
public sealed class ApprovedOcrPage
{
    internal ApprovedOcrPage(string model, RenderedPage page) { Model = model; Page = page; }
    public string Model { get; }
    public RenderedPage Page { get; }
}
public interface IOcrProvider
{
    Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request, CancellationToken ct);
}
