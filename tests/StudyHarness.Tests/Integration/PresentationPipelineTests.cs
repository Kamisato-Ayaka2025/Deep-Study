using System.Collections.Concurrent;
using System.Security.Cryptography;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class PresentationPipelineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-pipeline-" + Guid.NewGuid().ToString("N"));
    private sealed class Caps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string m, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(m, 1048576, 16384, true, DateTimeOffset.UtcNow, true)); }
    private sealed class Source(int total) : IAutomaticPdfKnowledge, IStreamingPresentationKnowledge
    {
        public readonly Guid Id = Guid.NewGuid();
        public ConcurrentDictionary<int, OcrPageResult> Saved { get; } = new();
        public ConcurrentDictionary<int, OcrPageResult> Staged { get; } = new();
        public int Opens, Produced, Closed, PendingImagesPeak;
        public bool WrongIdentity;
        public int FinishedCalls;
        public async Task ProducePresentationPagesAsync(Guid l, Guid c, string hash, IReadOnlyList<int> pages, Func<RenderedPage, CancellationToken, Task> publish, CancellationToken ct)
        {
            Opens++;
            try
            {
                foreach (var page in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    Produced++;
                    PendingImagesPeak = Math.Max(PendingImagesPeak, Produced - Volatile.Read(ref FinishedCalls));
                    await publish(new(WrongIdentity ? Guid.NewGuid() : Id, hash, page, "test", [1], Convert.ToHexString(SHA256.HashData(new byte[] { 1 }))), ct);
                }
            }
            finally { Closed++; }
        }
        public Task<PdfScanState> ScanStateAsync(Guid l, Guid c, CancellationToken ct) => Task.FromResult(new PdfScanState(Id, "hash", total, Saved.Keys.Order().ToArray(), Enumerable.Range(1, total).Except(Saved.Keys).ToArray(), "presentation"));
        public Task StageOcrAsync(Guid l, RenderedPage p, OcrPageResult r, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Staged[p.Page] = r; return Task.CompletedTask; }
        public Task CommitStagedOcrAsync(Guid l, Guid c, CancellationToken ct) { foreach (var item in Staged) if (Staged.TryRemove(item.Key, out var result)) Saved[item.Key] = result; return Task.CompletedTask; }
        public Task<IReadOnlyList<RenderedPage>> RenderPagesAsync(Guid l, Guid c, IReadOnlyList<int> p, CancellationToken ct) => throw new InvalidOperationException("Batch renderer must not be used");
        public Task<OcrPageResult?> CachedOcrAsync(Guid l, RenderedPage p, string m, string v, CancellationToken ct) => Task.FromResult(Saved.GetValueOrDefault(p.Page));
        public Task SaveOcrAsync(Guid l, RenderedPage p, OcrPageResult r, CancellationToken ct) => throw new InvalidOperationException("Must stage first");
        public Task RecordOcrAttemptAsync(Guid l, RenderedPage p, string m, int a, DateTimeOffset s, string o, TokenUsage? u) => Task.CompletedTask;
        public Task DeletePdfAsync(Guid l, Guid c, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Provider(Source source) : IOcrProvider
    {
        public int Calls, Active, Peak;
        public bool Fail;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold;
        public async Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); var active = Interlocked.Increment(ref Active);
            int peak; do { peak = Peak; } while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            Started.TrySetResult();
            try
            {
                if (Hold is not null) await Hold.Task; // Deliberately ignores cancellation: late results must be rejected.
                await Task.Delay(25, ct);
                return Fail ? Result<OcrPageResult>.Fail(ErrorCode.AUTH_INVALID, "fixture failure") : Result<OcrPageResult>.Success(new("Page " + request.Page.Page, [], new(1, 1, 2), request.Model, OcrService.PromptVersion));
            }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref source.FinishedCalls); }
        }
    }
    private AutomaticPdfScanService Service(Source source, Provider provider, int concurrency = 8)
    {
        Directory.CreateDirectory(root);
        return new(source, new SettingsService(new SqliteSettingsStore(root)), new Caps(), provider) { PresentationConcurrency = concurrency };
    }
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public async Task StreamsOnceAtSelectedConcurrencyWithBoundedImagesAndDurablePages(int concurrency)
    {
        var source = new Source(64); var provider = new Provider(source); var service = Service(source, provider, concurrency);
        var result = await service.RunAsync(Guid.Empty, source.Id, true);
        Assert.True(result.Ok, result.Error?.Message); Assert.Equal(concurrency, provider.Peak);
        Assert.Equal(64, source.Saved.Count); Assert.Empty(source.Staged); Assert.Equal(1, source.Opens); Assert.Equal(1, source.Closed);
        Assert.InRange(source.PendingImagesPeak, 1, concurrency + 5);
        Assert.True((await service.RunAsync(Guid.Empty, source.Id, true)).Ok);
        Assert.Equal(64, provider.Calls); Assert.Equal(1, source.Opens);
    }
    [Fact]
    public async Task CancellationJoinsRendererRejectsLateResultsAndResumeUsesSavedPages()
    {
        var source = new Source(60); source.Staged[1] = new("saved before restart", ["unverified"], null, "model", OcrService.PromptVersion);
        var provider = new Provider(source) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var service = Service(source, provider);
        using var stop = new CancellationTokenSource();
        var run = service.RunAsync(Guid.Empty, source.Id, true, ct: stop.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); provider.Hold.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Single(source.Saved); Assert.Equal(0, provider.Active); Assert.Equal(source.Opens, source.Closed);
        var previous = provider.Calls; provider.Hold = null;
        Assert.True((await service.RunAsync(Guid.Empty, source.Id, true)).Ok);
        Assert.Equal(59, provider.Calls - previous); Assert.Equal(60, source.Saved.Count);
    }
    [Fact]
    public async Task FailureStopsQueueAndIdentityAndConsentAreCheckedBeforeDispatch()
    {
        var source = new Source(80); var provider = new Provider(source) { Fail = true }; var service = Service(source, provider);
        Assert.False((await service.RunAsync(Guid.Empty, source.Id, false)).Ok); Assert.Equal(0, source.Opens);
        Assert.False((await service.RunAsync(Guid.Empty, source.Id, true)).Ok);
        Assert.InRange(provider.Calls, 1, 8); Assert.Equal(0, provider.Active); Assert.Equal(source.Opens, source.Closed);
        var wrong = new Source(2) { WrongIdentity = true }; var unused = new Provider(wrong);
        Assert.False((await Service(wrong, unused).RunAsync(Guid.Empty, wrong.Id, true)).Ok); Assert.Equal(0, unused.Calls);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
