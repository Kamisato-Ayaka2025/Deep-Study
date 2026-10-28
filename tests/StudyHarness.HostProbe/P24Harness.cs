using System.Diagnostics;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P24Harness
{
    private sealed class Caps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string m, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(m, 1048576, 16384, true, DateTimeOffset.UtcNow, true)); }
    private sealed class Provider(Stopwatch clock) : IOcrProvider
    {
        public int Calls, Active, Peak; public double FirstDispatchSeconds;
        public async Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage page, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == 1) FirstDispatchSeconds = clock.Elapsed.TotalSeconds;
            var active = Interlocked.Increment(ref Active); int peak;
            do { peak = Peak; } while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            try { await Task.Delay(1000, ct); return Result<OcrPageResult>.Success(new("Synthetic timed recognition for page " + page.Page.Page, [], new(1, 1, 2), page.Model, OcrService.PromptVersion)); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private class Legacy(KnowledgeStore inner) : IAutomaticPdfKnowledge
    {
        protected KnowledgeStore Store => inner;
        public int OpenSessions;
        public Task<PdfScanState> ScanStateAsync(Guid l, Guid c, CancellationToken ct) => inner.ScanStateAsync(l, c, ct);
        public Task StageOcrAsync(Guid l, RenderedPage p, OcrPageResult r, CancellationToken ct) => inner.StageOcrAsync(l, p, r, ct);
        public Task CommitStagedOcrAsync(Guid l, Guid c, CancellationToken ct) => inner.CommitStagedOcrAsync(l, c, ct);
        public Task DeletePdfAsync(Guid l, Guid c, CancellationToken ct) => inner.DeletePdfAsync(l, c, ct);
        public Task<IReadOnlyList<RenderedPage>> RenderPagesAsync(Guid l, Guid c, IReadOnlyList<int> p, CancellationToken ct) { OpenSessions++; return inner.RenderPagesAsync(l, c, p, ct); }
        public Task<OcrPageResult?> CachedOcrAsync(Guid l, RenderedPage p, string m, string v, CancellationToken ct) => inner.CachedOcrAsync(l, p, m, v, ct);
        public Task SaveOcrAsync(Guid l, RenderedPage p, OcrPageResult r, CancellationToken ct) => inner.SaveOcrAsync(l, p, r, ct);
        public Task RecordOcrAttemptAsync(Guid l, RenderedPage p, string m, int a, DateTimeOffset s, string o, TokenUsage? u) => inner.RecordOcrAttemptAsync(l, p, m, a, s, o, u);
    }
    private sealed class Streaming(KnowledgeStore inner) : Legacy(inner), IStreamingPresentationKnowledge
    {
        public Task ProducePresentationPagesAsync(Guid l, Guid c, string hash, IReadOnlyList<int> pages, Func<RenderedPage, CancellationToken, Task> publish, CancellationToken ct)
        { OpenSessions++; return Store.ProducePresentationPagesAsync(l, c, hash, pages, publish, ct); }
    }
    internal static async Task<int> Run(string[] args)
    {
        string Arg(string name) => Path.GetFullPath(args[Array.IndexOf(args, name) + 1]);
        var pdf = args.Contains("--pdf");
        var evidence = Arg("--evidence"); var source = Arg(pdf ? "--pdf" : "--presentation");
        var rows = new List<object>(); Directory.CreateDirectory(evidence);
        foreach (var concurrency in pdf ? new[] { 4, 8 } : new[] { 4, 8, 16 })
        {
            var root = Path.Combine(evidence, "mode-" + concurrency);
            if (Directory.Exists(root)) throw new IOException("Fresh benchmark roots required");
            var libraryRoot = Path.Combine(root, "library"); Directory.CreateDirectory(libraryRoot);
            var knowledge = new KnowledgeStore(root);
            var library = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(libraryRoot)))).Value!.Id;
            var book = (await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(source), library)).Value!;
            Legacy observed = pdf || concurrency == 4 ? new Legacy(knowledge) : new Streaming(knowledge);
            var clock = new Stopwatch(); var provider = new Provider(clock);
            var scanner = new AutomaticPdfScanService(observed, new SettingsService(new SqliteSettingsStore(root)), new Caps(), provider) { PresentationConcurrency = concurrency, PdfConcurrency = pdf ? concurrency : 4 };
            clock.Start(); var result = await scanner.RunAsync(library, book.Collection.Id, true); clock.Stop();
            if (!result.Ok || result.Value!.ReadyPages.Count != book.Collection.EntryCount) throw new IOException(result.Error?.Message ?? "Missing pages");
            var calls = provider.Calls; var opens = observed.OpenSessions;
            if (!(await scanner.RunAsync(library, book.Collection.Id, true)).Ok || provider.Calls != calls || observed.OpenSessions != opens) throw new IOException("Repeat dispatch");
            rows.Add(new { concurrency, elapsedSeconds = clock.Elapsed.TotalSeconds, provider.FirstDispatchSeconds, provider.Peak, provider.Calls, observed.OpenSessions, pages = result.Value.ReadyPages.Count });
            Console.WriteLine($"{concurrency} workers: {clock.Elapsed.TotalSeconds:F2}s; first dispatch {provider.FirstDispatchSeconds:F2}s; peak {provider.Peak}; sessions {opens}; pages {result.Value.ReadyPages.Count}");
            await File.WriteAllTextAsync(Path.Combine(evidence, "benchmark.json"), JsonSerializer.Serialize(new { realRemoteCalls = 0, simulatedRequestMilliseconds = 1000, realPowerPointRendering = !pdf, realPdfRendering = pdf, results = rows }, Wire.Options));
        }
        return 0;
    }
}
