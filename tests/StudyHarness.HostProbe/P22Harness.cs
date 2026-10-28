using System.Diagnostics;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P22Harness
{
    public static async Task<int> Run(string[] args)
    {
        string Arg(string key) => Path.GetFullPath(args[Array.IndexOf(args, key) + 1]);
        var root = Arg("--evidence");
        if (!File.Exists(Path.Combine(root, "p22-isolated.marker"))) throw new IOException("Isolated replay required");
        var path = Arg("--pdf");
        var knowledge = new KnowledgeStore(root);
        var library = (await knowledge.CurrentAsync())!;
        var clock = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var result = await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromPlanFilePicker(path)), library.Id, stop.Token);
            var seconds = clock.Elapsed.TotalSeconds;
            if (!result.Ok || result.Value!.Files.Any(f => !f.Ok)) throw new IOException("Import failed");
            var book = (await knowledge.CollectionsAsync(library.Id)).Single(c => c.Collection.SourceKind == "pdf");
            var repeat = Stopwatch.StartNew();
            var again = await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromPlanFilePicker(path)), library.Id, stop.Token);
            if (!again.Ok) throw new IOException("Repeated import failed");
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, seconds, repeatSeconds = repeat.Elapsed.TotalSeconds, book.Collection.EntryCount, book.Collection.ReadyCount, realRemoteCalls = 0 }, Wire.Options));
            Console.WriteLine($"Import {seconds:F2}s; repeat {repeat.Elapsed.TotalSeconds:F2}s; ready {book.Collection.ReadyCount}/{book.Collection.EntryCount}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = false, cancelledAfterSeconds = clock.Elapsed.TotalSeconds }, Wire.Options));
            Console.WriteLine($"Cancelled after {clock.Elapsed.TotalSeconds:F2}s"); return 2;
        }
    }
}
