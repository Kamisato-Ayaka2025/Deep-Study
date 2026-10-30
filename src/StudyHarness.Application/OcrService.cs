using System.Security.Cryptography;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed class OcrReview
{
    internal OcrReview(object owner, Guid library, string model, IReadOnlyList<RenderedPage> pages)
    { Owner = owner; Library = library; Model = model; Pages = pages.Select(p => p with { Png = p.Png.ToArray() }).ToArray(); }
    internal object Owner { get; }
    internal Guid Library { get; }
    internal IReadOnlyList<RenderedPage> Pages { get; }
    public string Model { get; }
    public string Preview => $"将向DeepSeek发送资料页图，模型{Model}；页序：{string.Join(",", Pages.Select(p => p.Page))}。每页1次请求，临时错误最多重试2次；最多{Pages.Count * 3}次派发。费用未知，按服务商实际计费。重启不自动继续。\n" +
        string.Join("\n", Pages.Select(p => $"页{p.Page}：{p.Png.Length}字节，图像hash={p.ImageHash}")) + "\n结果默认未经人工核验，公式/图表需对照原页。";
}
public sealed class OcrService(IOcrKnowledge knowledge, SettingsService settings, IModelCapabilitySource capabilities, IOcrProvider provider, int concurrency = 1)
{
    public const string PromptVersion = "ocr-v2";
    private readonly object owner = new();
    private readonly SemaphoreSlim worker = new(1, 1);
    internal Task<Result<int>> RunPreparedPageAsync(Guid library, string model, RenderedPage page, CancellationToken ct)
    {
        if (page.Png.Length is < 1 or > 4194304 || Convert.ToHexString(SHA256.HashData(page.Png)) != page.ImageHash)
            return Task.FromResult(Result<int>.Fail(ErrorCode.CONSENT_DENIED, "页图与授权摘要不符。"));
        return RunAsync(new OcrReview(owner, library, model, [page]), true, ct: ct);
    }
    public async Task<Result<OcrReview>> PrepareAsync(Guid library, Guid collection, IReadOnlyList<int> pages, CancellationToken ct = default)
    {
        if (pages.Count is < 1 or > 20 || pages.Distinct().Count() != pages.Count || pages.Any(p => p is < 1 or > 10000))
            return Result<OcrReview>.Fail(ErrorCode.VALIDATION_ERROR, "每批请选择1至20个不同的资料页序。");
        var config = await settings.GetAsync(ct); var capability = await capabilities.GetAsync(config.Model, ct);
        if (!AdaptiveBudgetPolicy.IsFresh(capability, config.Model, DateTimeOffset.UtcNow) || !capability.SupportsImages || capability.MaxOutputTokens < 8192)
            return Result<OcrReview>.Fail(ErrorCode.MODEL_INVALID, "当前模型视觉能力未确认，请检查模型配置后重试。");
        IReadOnlyList<RenderedPage> rendered;
        try { rendered = await knowledge.RenderPagesAsync(library, collection, pages, ct); }
        catch (OperationCanceledException) { throw; }
        catch (PdfPreparationException e) { return Result<OcrReview>.Fail(ErrorCode.SOURCE_UNAVAILABLE, e.Message); }
        catch (FileNotFoundException) { return Result<OcrReview>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "PDF_SOURCE_MISSING：原资料已移动或删除，请恢复原文件后继续扫描。"); }
        catch (UnauthorizedAccessException) { return Result<OcrReview>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "PDF_ACCESS：原资料或资料库无法读取，请检查文件权限。"); }
        catch (InvalidDataException) { return Result<OcrReview>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "PDF_SOURCE_INVALID：原资料已变化、页范围无效或文档损坏；请恢复原文件，或重新导入已修改的文件。"); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { return Result<OcrReview>.Fail(ErrorCode.SOURCE_UNAVAILABLE, $"PDF_PREPARE：本批页图准备失败（错误码{e.HResult:X8}），尚未发送本批；已完成进度保留。"); }
        if (rendered.Count == 0 || rendered.Select(p => p.Page).Distinct().Count() != rendered.Count ||
            rendered.Select(p => p.SourceFingerprint).Distinct().Count() != 1 ||
            rendered.Any(p => p.CollectionId != collection || !pages.Contains(p.Page)) || rendered.Any(p => p.Png.Length is < 1 or > 4194304 || Convert.ToHexString(SHA256.HashData(p.Png)) != p.ImageHash))
            return Result<OcrReview>.Fail(ErrorCode.VALIDATION_ERROR, "页图超出预算或不完整，请缩小范围。");
        return Result<OcrReview>.Success(new(owner, library, config.Model, rendered));
    }
    public async Task<Result<int>> RunAsync(OcrReview review, bool allow, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!allow || !ReferenceEquals(review.Owner, owner)) return Result<int>.Fail(ErrorCode.CONSENT_DENIED, "未授权本批页图外发。");
        if ((await settings.GetAsync(ct)).Model != review.Model) return Result<int>.Fail(ErrorCode.CONSENT_REQUIRED, "模型已变，请重新预览页范围。");
        if (!await worker.WaitAsync(0, ct)) return Result<int>.Fail(ErrorCode.TASK_ACTIVE, "已有识别任务。");
        try
        {
            var completed = 0;
            var next = -1;
            Result<int>? failure = null;
            var gate = new object();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            async Task WorkAsync()
            {
                try
                {
                    while (true)
                    {
                        RenderedPage page;
                        lock (gate)
                        {
                            if (failure is not null || stopping.IsCancellationRequested || ++next >= review.Pages.Count) return;
                            page = review.Pages[next];
                        }
                        var result = await ProcessPageAsync(page, stopping.Token);
                        lock (gate)
                        {
                            if (!result.Ok) { failure ??= result; return; }
                            completed++;
                        }
                    }
                }
                catch { stopping.Cancel(); throw; }
            }
            async Task<Result<int>> ProcessPageAsync(RenderedPage page, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (await knowledge.CachedOcrAsync(review.Library, page, review.Model, PromptVersion, ct) is not null) return Result<int>.Success(1);
                progress?.Report($"正在识别第{page.Page}页，可取消。");
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(300));
                    var started = DateTimeOffset.UtcNow;
                    Result<OcrPageResult> result;
                    try { result = await provider.RecognizeAsync(new(review.Model, page), deadline.Token); }
                    catch (OperationCanceledException)
                    {
                        await knowledge.RecordOcrAttemptAsync(review.Library, page, review.Model, attempt, started, "RUN_CANCELLED", null); throw;
                    }
                    await knowledge.RecordOcrAttemptAsync(review.Library, page, review.Model, attempt, started,
                        deadline.IsCancellationRequested ? "RUN_CANCELLED" : result.Ok ? "received" : result.Error!.Code.ToString(), result.Value?.Usage);
                    ct.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) return Result<int>.Fail(ErrorCode.RUN_CANCELLED, "本页识别超时，迟到结果未发布；可缩小范围后重试。");
                    if (result.Ok)
                    {
                        if (string.IsNullOrWhiteSpace(result.Value!.Text) || result.Value.Text.Length > 65536 || result.Value.Model != review.Model || result.Value.PromptVersion != PromptVersion)
                            return Result<int>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "页识别结果不完整，未计入覆盖。");
                        await knowledge.SaveOcrAsync(review.Library, page, result.Value with { QualityFlags = result.Value.QualityFlags.Concat(new[] { "unverified", "visualReviewRequired" }).Distinct().ToArray() }, ct);
                        return Result<int>.Success(1);
                    }
                    if (result.Error!.Code != ErrorCode.TRANSIENT_PROVIDER_ERROR || attempt == 3) return new(false, completed, result.Error);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                }
                throw new InvalidOperationException("OCR attempt limit");
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Clamp(concurrency, 1, 8)).Select(_ => WorkAsync()));
            ct.ThrowIfCancellationRequested();
            return failure is null ? Result<int>.Success(completed) : new(false, completed, failure.Error);
        }
        finally { worker.Release(); }
    }
}
