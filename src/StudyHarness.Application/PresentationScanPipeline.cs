using System.Threading.Channels;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

internal sealed class PresentationScanPipeline(IAutomaticPdfKnowledge knowledge, IStreamingPresentationKnowledge renderer,
    SettingsService settings, IModelCapabilitySource capabilities, IOcrProvider provider)
{
    internal async Task<Result<PdfScanState>> RunAsync(Guid library, PdfScanState initial, int concurrency,
        IProgress<PdfScanProgress>? progress, CancellationToken ct)
    {
        concurrency = concurrency == 16 ? 16 : 8;
        var config = await settings.GetAsync(ct);
        var capability = await capabilities.GetAsync(config.Model, ct);
        if (!AdaptiveBudgetPolicy.IsFresh(capability, config.Model, DateTimeOffset.UtcNow) || !capability.SupportsImages || capability.MaxOutputTokens < 8192)
            return Result<PdfScanState>.Fail(ErrorCode.MODEL_INVALID, "当前模型视觉能力未确认，请检查模型配置后重试。");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = Channel.CreateBounded<RenderedPage>(new BoundedChannelOptions(4) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var expected = initial.PendingPages.ToHashSet();
        var seen = new HashSet<int>();
        Result<int>? failure = null;
        Exception? renderFailure = null;
        var staged = new Staging(knowledge, initial, progress);
        async Task Produce()
        {
            try
            {
                await renderer.ProducePresentationPagesAsync(library, initial.CollectionId, initial.SourceFingerprint, initial.PendingPages, async (page, token) =>
                {
                    if (page.CollectionId != initial.CollectionId || page.SourceFingerprint != initial.SourceFingerprint || !expected.Contains(page.Page) || !seen.Add(page.Page))
                        throw new InvalidDataException("PPT页图身份或页序不符。");
                    await queue.Writer.WriteAsync(page, token);
                }, stopping.Token);
                if (seen.Count != expected.Count) throw new InvalidDataException("PPT渲染页不完整。");
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception e) { renderFailure = e; stopping.Cancel(); }
            finally { queue.Writer.TryComplete(); }
        }
        async Task Consume()
        {
            var ocr = new OcrService(staged, settings, capabilities, provider);
            try
            {
                await foreach (var page in queue.Reader.ReadAllAsync(stopping.Token))
                {
                    stopping.Token.ThrowIfCancellationRequested();
                    var result = await ocr.RunPreparedPageAsync(library, config.Model, page, stopping.Token);
                    if (!result.Ok) { Interlocked.CompareExchange(ref failure, result, null); stopping.Cancel(); break; }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception e) { Interlocked.CompareExchange(ref renderFailure, e, null); stopping.Cancel(); }
        }
        progress?.Report(new(initial.ReadyPages.Count, initial.TotalPages, 0, 0, $"PPT连续扫描：最多{concurrency}页并行，边渲染边识别；可取消并保留进度。"));
        try { await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Consume()).Append(Produce())); }
        finally { await knowledge.CommitStagedOcrAsync(library, initial.CollectionId, CancellationToken.None); }
        ct.ThrowIfCancellationRequested();
        var state = await knowledge.ScanStateAsync(library, initial.CollectionId, ct);
        if (renderFailure is not null)
            return Result<PdfScanState>.Fail(ErrorCode.SOURCE_UNAVAILABLE, renderFailure is PdfPreparationException ? renderFailure.Message : $"PPT_PIPELINE：页图处理失败（错误码{renderFailure.HResult:X8}），已保存进度保留。");
        if (failure is not null) return new(false, state, failure.Error);
        if (state.PendingPages.Count > 0) return Result<PdfScanState>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "仍有未完成页，已保存进度保留。");
        progress?.Report(new(state.ReadyPages.Count, state.TotalPages, 0, 0, "扫描完成，识别文字已保存；图表与公式仍需复核。"));
        return Result<PdfScanState>.Success(state);
    }
    private sealed class Staging(IAutomaticPdfKnowledge inner, PdfScanState initial, IProgress<PdfScanProgress>? progress) : IOcrKnowledge
    {
        private readonly SemaphoreSlim saving = new(1, 1);
        private int saved;
        public Task<IReadOnlyList<RenderedPage>> RenderPagesAsync(Guid l, Guid c, IReadOnlyList<int> p, CancellationToken ct) => inner.RenderPagesAsync(l, c, p, ct);
        public Task<OcrPageResult?> CachedOcrAsync(Guid l, RenderedPage p, string m, string v, CancellationToken ct) => inner.CachedOcrAsync(l, p, m, v, ct);
        public Task RecordOcrAttemptAsync(Guid l, RenderedPage p, string m, int a, DateTimeOffset s, string o, TokenUsage? u) => inner.RecordOcrAttemptAsync(l, p, m, a, s, o, u);
        public async Task SaveOcrAsync(Guid library, RenderedPage page, OcrPageResult result, CancellationToken ct)
        {
            await saving.WaitAsync(ct);
            try
            {
                await inner.StageOcrAsync(library, page, result, ct);
                saved++;
                progress?.Report(new(initial.ReadyPages.Count + saved, initial.TotalPages, 0, 0, $"已保存第{page.Page}页；正在连续识别后续页。"));
                if (saved % 20 == 0) await inner.CommitStagedOcrAsync(library, initial.CollectionId, ct);
            }
            finally { saving.Release(); }
        }
    }
}
