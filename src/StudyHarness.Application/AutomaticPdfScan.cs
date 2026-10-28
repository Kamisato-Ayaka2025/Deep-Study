using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed record PdfScanState(Guid CollectionId, string SourceFingerprint, int TotalPages, IReadOnlyList<int> ReadyPages,
    IReadOnlyList<int> PendingPages, string SourceKind = "pdf");
public sealed record PdfScanProgress(int Completed, int Total, int Batch, int Batches, string Message);
public interface IAutomaticPdfKnowledge : IOcrKnowledge
{
    Task<PdfScanState> ScanStateAsync(Guid library, Guid collection, CancellationToken ct);
    Task StageOcrAsync(Guid library, RenderedPage page, OcrPageResult result, CancellationToken ct);
    Task CommitStagedOcrAsync(Guid library, Guid collection, CancellationToken ct);
    Task DeletePdfAsync(Guid library, Guid collection, CancellationToken ct);
}

// Successful pages are durable before progress advances. Batch commits build one immutable
// index per 20 pages instead of copying the entire book after every page.
public sealed class AutomaticPdfScanService(IAutomaticPdfKnowledge knowledge, SettingsService settings,
    IModelCapabilitySource capabilities, IOcrProvider provider)
{
    public int PdfConcurrency { get; set; } = 8;
    public int PresentationConcurrency { get; set; } = 8;
    private readonly SemaphoreSlim worker = new(1, 1);
    public async Task<Result<PdfScanState>> RunAsync(Guid library, Guid collection, bool allow,
        IProgress<PdfScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (!allow) return Result<PdfScanState>.Fail(ErrorCode.CONSENT_DENIED, "未授权自动扫描。" );
        if (!await worker.WaitAsync(0, ct)) return Result<PdfScanState>.Fail(ErrorCode.TASK_ACTIVE, "已有扫描任务。" );
        var stage = "恢复已保存扫描页";
        try
        {
            await knowledge.CommitStagedOcrAsync(library, collection, ct);
            stage = "读取扫描状态";
            var state = await knowledge.ScanStateAsync(library, collection, ct);
            var fingerprint = state.SourceFingerprint;
            if (state.SourceKind == "presentation" && state.PendingPages.Count > 0 && knowledge is IStreamingPresentationKnowledge streaming)
                return await new PresentationScanPipeline(knowledge, streaming, settings, capabilities, provider)
                    .RunAsync(library, state, PresentationConcurrency, progress, ct);
            while (state.PendingPages.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (state.SourceFingerprint != fingerprint) return Result<PdfScanState>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "资料已变化，请重新导入。" );
                var batch = state.PendingPages.Take(20).ToArray();
                var completed = state.ReadyPages.Count;
                var batchNumber = (batch[0] - 1) / 20 + 1;
                void Report(string message) => progress?.Report(new(completed, state.TotalPages, batchNumber, (state.TotalPages + 19) / 20, message));
                Report("正在准备扫描页，可随时取消；已完成页已保存。");
                var reporting = new object();
                var staging = new Staging(knowledge, () => { lock (reporting) { completed++; Report("已保存本页识别结果。"); } });
                var ocr = new OcrService(staging, settings, capabilities, provider, concurrency: PdfConcurrency == 4 ? 4 : 8);
                stage = $"准备第{batch[0]}至{batch[^1]}页";
                var prepared = await ocr.PrepareAsync(library, collection, batch, ct);
                if (!prepared.Ok) return new(false, state, prepared.Error);
                Result<int> result;
                stage = $"识别或保存第{batch[0]}至{batch[^1]}页";
                try { result = await ocr.RunAsync(prepared.Value!, true, new InlineProgress<string>(Report), ct); }
                finally { await knowledge.CommitStagedOcrAsync(library, collection, CancellationToken.None); }
                state = await knowledge.ScanStateAsync(library, collection, ct);
                if (!result.Ok) return new(false, state, result.Error);
            }
            progress?.Report(new(state.ReadyPages.Count, state.TotalPages, (state.TotalPages + 19) / 20,
                (state.TotalPages + 19) / 20, "扫描完成，识别文字已保存；图表与公式仍需复核。"));
            return Result<PdfScanState>.Success(state);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { return Result<PdfScanState>.Fail(ErrorCode.IO_FAILURE, $"PDF_SCAN：{stage}失败（错误码{e.HResult:X8}）。已保存页保留；请检查文件和资料库后继续扫描。"); }
        finally { worker.Release(); }
    }
    private sealed class Staging(IAutomaticPdfKnowledge inner, Action saved) : IOcrKnowledge
    {
        public Task<IReadOnlyList<RenderedPage>> RenderPagesAsync(Guid l, Guid c, IReadOnlyList<int> p, CancellationToken ct) => inner.RenderPagesAsync(l,c,p,ct);
        public Task<OcrPageResult?> CachedOcrAsync(Guid l, RenderedPage p, string m, string v, CancellationToken ct) => inner.CachedOcrAsync(l,p,m,v,ct);
        public async Task SaveOcrAsync(Guid l, RenderedPage p, OcrPageResult r, CancellationToken ct) { await inner.StageOcrAsync(l,p,r,ct); saved(); }
        public Task RecordOcrAttemptAsync(Guid l, RenderedPage p, string m, int a, DateTimeOffset s, string o, TokenUsage? u) => inner.RecordOcrAttemptAsync(l,p,m,a,s,o,u);
    }
}
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
