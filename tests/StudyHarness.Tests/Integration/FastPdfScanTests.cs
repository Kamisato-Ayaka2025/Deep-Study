using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using UglyToad.PdfPig.Writer;
using Xunit;
using Xunit.Abstractions;

namespace StudyHarness.Tests.Integration;

public sealed class FastPdfScanTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-fast-pdf", Guid.NewGuid().ToString("N"));
    private sealed class Capabilities : IModelCapabilitySource
    {
        public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(model, 1048576, 16384, true, DateTimeOffset.UtcNow, true));
    }
    private sealed class Provider : IOcrProvider
    {
        public int Calls, Active, Peak;
        public Func<ApprovedOcrPage, CancellationToken, Task>? During;
        public bool Fail;
        public async Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active);
            int peak;
            do { peak = Peak; } while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            try
            {
                if (During is not null) await During(request, ct);
                return Fail ? Result<OcrPageResult>.Fail(ErrorCode.AUTH_INVALID, "test failure") : Result<OcrPageResult>.Success(
                    new("Figure: A points to B. Page " + request.Page.Page, [], new(10, 10, 20), request.Model, OcrService.PromptVersion));
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class MemoryKnowledge : IOcrKnowledge
    {
        public ConcurrentDictionary<int, OcrPageResult> Saved { get; } = new();
        public Task<IReadOnlyList<RenderedPage>> RenderPagesAsync(Guid l, Guid c, IReadOnlyList<int> pages, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RenderedPage>>(pages.Select(p => new RenderedPage(c, "source", p, "test", [1], Convert.ToHexString(SHA256.HashData(new byte[] { 1 })))).ToArray());
        public Task<OcrPageResult?> CachedOcrAsync(Guid l, RenderedPage p, string m, string v, CancellationToken ct) => Task.FromResult(Saved.GetValueOrDefault(p.Page));
        public Task SaveOcrAsync(Guid l, RenderedPage p, OcrPageResult r, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Saved[p.Page] = r; return Task.CompletedTask; }
        public Task RecordOcrAttemptAsync(Guid l, RenderedPage p, string m, int a, DateTimeOffset s, string o, TokenUsage? u) => Task.CompletedTask;
    }
    [Fact]
    public async Task FiveHundredPages_BoundedParallelismPreservesEveryPageAndCache()
    {
        Directory.CreateDirectory(root);
        var settings = new SettingsService(new SqliteSettingsStore(root));
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            var knowledge = new MemoryKnowledge();
            var provider = new Provider { During = (_, ct) => Task.Delay(10, ct) };
            var service = new OcrService(knowledge, settings, new Capabilities(), provider, concurrency);
            var clock = Stopwatch.StartNew();
            for (var first = 1; first <= 500; first += 20)
            {
                var review = await service.PrepareAsync(Guid.Empty, Guid.Empty, Enumerable.Range(first, 20).ToArray());
                Assert.True(review.Ok);
                Assert.True((await service.RunAsync(review.Value!, true)).Ok);
                Assert.True((await service.RunAsync(review.Value!, true)).Ok);
            }
            output.WriteLine($"500 pages; simulated provider latency 10 ms; concurrency={concurrency}; elapsedMs={clock.Elapsed.TotalMilliseconds:F0}; peak={provider.Peak}");
            Assert.Equal(concurrency, provider.Peak); Assert.Equal(500, provider.Calls); Assert.Equal(500, knowledge.Saved.Count);
            Assert.Equal(Enumerable.Range(1, 500), knowledge.Saved.Keys.Order());
        }
    }
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ParallelFailureStopsNewPagesAndCancellationRejectsLateReplies(int concurrency)
    {
        Directory.CreateDirectory(root);
        var settings = new SettingsService(new SqliteSettingsStore(root));
        var knowledge = new MemoryKnowledge();
        var provider = new Provider { Fail = true, During = (_, ct) => Task.Delay(20, ct) };
        var service = new OcrService(knowledge, settings, new Capabilities(), provider, concurrency);
        var review = (await service.PrepareAsync(Guid.Empty, Guid.Empty, Enumerable.Range(1, 20).ToArray())).Value!;
        Assert.False((await service.RunAsync(review, true)).Ok); Assert.Equal(concurrency, provider.Calls); Assert.Empty(knowledge.Saved);
        using var stop = new CancellationTokenSource();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Calls = 0; provider.Fail = false;
        provider.During = async (_, _) => { if (provider.Calls == concurrency) allStarted.TrySetResult(); await release.Task; };
        var run = service.RunAsync(review, true, ct: stop.Token);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel(); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(concurrency, provider.Calls); Assert.Equal(0, provider.Active); Assert.Empty(knowledge.Saved);
    }
    [Fact]
    public async Task MixedRasterAndVectorPagesKeepNativeTextAndUpgradeLegacyCoverage()
    {
        var libraryPath = Path.Combine(root, "library"); Directory.CreateDirectory(libraryPath);
        var knowledge = new KnowledgeStore(root);
        var library = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(libraryPath)))).Value!.Id;
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(300, 400).AddText("Native plain text", 12, new(20, 300), font);
        var vector = builder.AddPage(300, 400); vector.AddText("Native vector text", 12, new(20, 300), font); vector.DrawRectangle(new(20, 30), 100, 100);
        using var png = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, png);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, 2, 2, 96, 96, Enumerable.Repeat((byte)150, 16).ToArray());
        await encoder.FlushAsync(); png.Seek(0); using var stream = png.AsStreamForRead();
        var image = new byte[(int)png.Size]; await stream.ReadExactlyAsync(image);
        var raster = builder.AddPage(300, 400); raster.AddText("Native raster text", 12, new(20, 300), font); raster.AddPng(image, new(20, 30, 120, 130));
        var path = Path.Combine(root, "mixed.pdf"); var bytes = builder.Build(); await File.WriteAllBytesAsync(path, bytes);
        var book = (await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Value!;
        Assert.Equal(1, book.Collection.ReadyCount); Assert.Equal(new[] { 2, 3 }, (await knowledge.ScanStateAsync(library, book.Collection.Id, default)).PendingPages);
        var old = book.References.First(r => r.Locator.Page == 2);
        var provider = new Provider(); var settings = new SettingsService(new SqliteSettingsStore(root));
        var ocr = new OcrService(knowledge, settings, new Capabilities(), provider);
        Assert.True((await ocr.RunAsync((await ocr.PrepareAsync(library, book.Collection.Id, [2])).Value!, true)).Ok);
        book = (await knowledge.CollectionsAsync(library)).Single();
        // Emulate a legacy manifest: mixed native text was incorrectly considered complete.
        var legacy = book.Manifest with { Entries = book.Manifest.Entries.Select(e => e with {
            State = e.Locator == "3" ? "textReady" : e.State,
            QualityFlags = e.QualityFlags.Where(f => f != "pdf-visual-v2").ToArray() }).ToArray() };
        using (var db = new SqliteConnection($"Data Source={Path.Combine(libraryPath, "collections.db")};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE collection_versions SET manifest=$m WHERE version=$v";
            command.Parameters.AddWithValue("$m", JsonSerializer.Serialize(legacy, Wire.Options)); command.Parameters.AddWithValue("$v", book.Collection.Version); command.ExecuteNonQuery();
        }
        var progress = new List<ImportProgress>();
        Assert.True((await knowledge.ImportWithProgressAsync(new(Guid.NewGuid(), 0, HostSelection.FromPlanFilePicker(path)), library,
            new InlineProgress<ImportProgress>(progress.Add))).Ok);
        book = (await knowledge.CollectionsAsync(library)).Single();
        Assert.Contains(progress, p => p.Completed == 2 && p.Message.Contains("已复用扫描缓存"));
        Assert.DoesNotContain(progress, p => p.Message.Contains("正在检查第2页"));
        Assert.Equal(2, book.Collection.ReadyCount); Assert.Equal(new[] { 3 }, (await knowledge.ScanStateAsync(library, book.Collection.Id, default)).PendingPages);
        var scanner = new AutomaticPdfScanService(knowledge, settings, new Capabilities(), provider);
        Assert.True((await scanner.RunAsync(library, book.Collection.Id, true)).Ok); Assert.Equal(2, provider.Calls);
        book = (await knowledge.CollectionsAsync(library)).Single(); Assert.Equal(3, book.Collection.ReadyCount);
        var text = string.Join("\n", await Task.WhenAll(book.References.Select(async r => (await knowledge.ResolveAsync(r)).Text)));
        Assert.Contains("Native plain text", text); Assert.Contains("Native vector text", text); Assert.Contains("Native raster text", text); Assert.Contains("A points to B", text);
        Assert.Contains("Native vector text", (await knowledge.ResolveAsync(old)).Text);
        Assert.Equal(book.Collection.Version, (await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Value!.Collection.Version);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task Import505Pages_ReportsPreparationAndCancellationDoesNotPublishPartialCollection()
    {
        var folder = Path.Combine(root, "library"); Directory.CreateDirectory(folder);
        var knowledge = new KnowledgeStore(root);
        var library = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(folder)))).Value!.Id;
        var pdf = new PdfDocumentBuilder(); for (var i = 0; i < 505; i++) pdf.AddPage(150, 200);
        var path = Path.Combine(root, "505.pdf"); await File.WriteAllBytesAsync(path, pdf.Build());
        using var stop = new CancellationTokenSource();
        var seen = new List<ImportProgress>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => knowledge.ImportWithProgressAsync(
            new(Guid.NewGuid(), 0, HostSelection.FromPlanFilePicker(path)), library,
            new InlineProgress<ImportProgress>(p => { seen.Add(p); if (p.Completed == 10) stop.Cancel(); }), stop.Token));
        Assert.Contains(seen, p => p.Total == 505 && p.Completed == 10);
        Assert.Empty(await knowledge.CollectionsAsync(library));
        var result = await knowledge.ImportWithProgressAsync(new(Guid.NewGuid(), 0, HostSelection.FromPlanFilePicker(path)), library, new InlineProgress<ImportProgress>(seen.Add));
        Assert.True(result.Ok);
        Assert.Equal(505, (await knowledge.CollectionsAsync(library)).Single().Collection.EntryCount);
        Assert.Contains(seen, p => p.Completed == 505 && p.Total == 505);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
