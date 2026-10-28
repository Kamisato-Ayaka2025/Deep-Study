using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P23Harness
{
    private sealed class Capabilities : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(model, 1048576, 16384, true, DateTimeOffset.UtcNow, true)); }
    private sealed class FakeOcr : IOcrProvider
    {
        public int Calls;
        public Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage page, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result<OcrPageResult>.Success(new("Synthetic scan replay: page " + page.Page.Page, [], new(1, 1, 2), page.Model, OcrService.PromptVersion)));
        }
    }
    internal static async Task<int> Run(string[] args)
    {
        string Arg(string key) => Path.GetFullPath(args[Array.IndexOf(args, key) + 1]);
        var root = Arg("--evidence"); var source = Arg("--presentations");
        if (args.Contains("--scan-replay"))
        {
            if (!File.Exists(Path.Combine(root, "p23-isolated.marker"))) throw new IOException("Isolated probe marker required");
            var restored = new KnowledgeStore(root); var replayLibrary = (await restored.CurrentAsync())!;
            var original = (await restored.CollectionsAsync(replayLibrary.Id)).Single();
            var before = await restored.ScanStateAsync(replayLibrary.Id, original.Collection.Id, default);
            var fake = new FakeOcr(); var scan = new AutomaticPdfScanService(restored, new SettingsService(new SqliteSettingsStore(root)), new Capabilities(), fake);
            var result = await scan.RunAsync(replayLibrary.Id, original.Collection.Id, true);
            if (!result.Ok) throw new IOException(result.Error!.Message);
            if (fake.Calls != before.PendingPages.Count || result.Value!.PendingPages.Count != 0) throw new IOException("Resume did not complete only missing pages");
            foreach (var reference in original.References) if ((await restored.ResolveAsync(reference)).Text is null) throw new IOException("Old reference lost");
            var calls = fake.Calls;
            var again = await scan.RunAsync(replayLibrary.Id, original.Collection.Id, true);
            if (!again.Ok || fake.Calls != calls) throw new IOException("Completed pages scanned twice");
            await File.WriteAllTextAsync(Path.Combine(root, "scan-replay.json"), JsonSerializer.Serialize(new { passed = true, initiallyReady = before.ReadyPages.Count, finalReady = result.Value.ReadyPages.Count, fakeCalls = calls, repeatCalls = fake.Calls - calls, realRemoteCalls = 0 }, Wire.Options));
            Console.WriteLine($"Restored {before.ReadyPages.Count}/{before.TotalPages} -> {result.Value.ReadyPages.Count}/{before.TotalPages}; fake calls {calls}; repeat zero"); return 0;
        }
        Directory.CreateDirectory(root);
        var libraryPath = Path.Combine(root, "synthetic-library");
        if (Directory.Exists(libraryPath)) throw new IOException("Fresh isolated library required");
        Directory.CreateDirectory(libraryPath);
        var store = new KnowledgeStore(root);
        var library = (await store.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(libraryPath)))).Value!.Id;
        var reports = new List<object>();
        foreach (var path in (File.Exists(source) ? new[] { source } : Directory.GetFiles(source)).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".pptx" or ".ppt"))
        {
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(path)); var clock = Stopwatch.StartNew();
            var imported = await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library);
            if (!imported.Ok) throw new IOException(imported.Error!.Message);
            var elapsed = clock.Elapsed.TotalSeconds; var snapshot = imported.Value!;
            clock.Restart(); var repeated = await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library);
            var repeat = clock.Elapsed.TotalSeconds;
            if (!repeated.Ok || repeated.Value!.Collection.Version != snapshot.Collection.Version) throw new IOException("Repeat mismatch");
            var textCount = 0;
            foreach (var reference in snapshot.References)
                textCount += (await store.ResolveAsync(reference)).Text?.Length ?? throw new IOException("Lost text");
            var state = await store.ScanStateAsync(library, snapshot.Collection.Id, default);
            double? renderSeconds = null; int? renderedPage = null;
            if (args.Contains("--render") && state.PendingPages.Count > 0)
            {
                clock.Restart(); renderedPage = state.PendingPages[0];
                var pages = args.Contains("--all-pages") ? state.PendingPages.ToArray() : args.Contains("--batch") ? state.PendingPages.Take(20).ToArray() : [renderedPage.Value];
                var images = await store.RenderPagesAsync(library, snapshot.Collection.Id, pages, default);
                renderSeconds = clock.Elapsed.TotalSeconds;
                if (images.Count != pages.Length || images.Any(p => p.Png.Length == 0)) throw new IOException("Render missing");
                Console.WriteLine($"Rendered {images.Count} pages in {renderSeconds:F2}s");
                await File.WriteAllBytesAsync(Path.Combine(root, Path.GetFileNameWithoutExtension(path) + ".png"), images[0].Png);
            }
            var afterHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
            if (!hash.SequenceEqual(afterHash)) throw new IOException("Source modified");
            reports.Add(new { file = Path.GetFileName(path), pages = snapshot.Collection.EntryCount, nativeReady = state.ReadyPages.Count,
                visualPages = state.PendingPages.Count, characters = textCount, importSeconds = elapsed, repeatSeconds = repeat, renderedPage, renderSeconds });
            Console.WriteLine($"{Path.GetFileName(path)}: {snapshot.Collection.EntryCount} pages; {state.ReadyPages.Count} native ready; import {elapsed:F2}s; repeat {repeat:F2}s");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, realRemoteCalls = 0, files = reports }, Wire.Options));
        return 0;
    }
}
