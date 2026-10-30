using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P36Performance
{
    internal static async Task<int> Run(string evidence, string mode)
    {
        Directory.CreateDirectory(evidence);
        var root = Path.Combine(evidence, "synthetic-data");
        var refsPath = Path.Combine(evidence, "references.json");
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(Path.Combine(root, "library")); File.WriteAllText(Path.Combine(root, "p36.marker"), "synthetic");
            var knowledge = new KnowledgeStore(root);
            var library = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(Path.Combine(root, "library"))))).Value!;
            var references = new List<SourceRef>();
            for (var i = 0; i < 40; i++)
                references.AddRange(await knowledge.SaveWebAsync(library.Id, [new($"https://example.org/{i}", $"资料{i:00}", string.Concat(Enumerable.Range(0, 8).Select(n => $"# 概念{n}\n定义与推导。" + new string('文', 5500) + "\n")), DateTimeOffset.UnixEpoch)], "概念", default));
            var file = Path.Combine(root, "book.md");
            File.WriteAllText(file, string.Concat(Enumerable.Range(0, 120).Select(n => $"# 章节{n}\n定义与推导。" + new string('书', 4000) + "\n")));
            references.AddRange((await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(file), library.Id)).Value!.References);
            var cache = new BookKnowledgeCache(root);
            foreach (var (chunk, index) in references.Chunk(40).Select((x, i) => (x, i)))
            {
                var sources = chunk.Select((r, i) => new PlanSource("b" + i, r, false)).ToArray();
                await cache.SaveKnowledgeAsync(new("web-unit" + index, "fingerprint" + index, sources,
                    sources.Select((s, i) => new BookKnowledgePoint("p" + i, "知识" + i, new string('知', 8000), [s.Key])).ToArray(), []), default);
            }
            File.WriteAllText(refsPath, JsonSerializer.Serialize(references, Wire.Options));
        }
        if (!File.Exists(Path.Combine(root, "p36.marker"))) throw new InvalidOperationException("Synthetic root required");
        var refs = JsonSerializer.Deserialize<SourceRef[]>(File.ReadAllText(refsPath), Wire.Options)!;
        var store = new KnowledgeStore(root); var catalog = new CacheCatalog(root);
        var results = new List<object>();
        async Task Measure(string name, Func<Task<string>> operation)
        {
            var samples = new List<double>(); var allocations = new List<long>(); var hashes = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                var allocated = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
                var value = await operation(); timer.Stop();
                samples.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
                hashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
            }
            if (hashes.Distinct().Count() != 1) throw new InvalidOperationException("Unstable result");
            results.Add(new { name, milliseconds = samples, allocatedBytes = allocations, hash = hashes[0] });
        }
        await Measure("resolve-120-local", async () =>
        {
            var text = new StringBuilder();
            foreach (var reference in refs.TakeLast(120)) text.Append((await store.ResolveAsync(reference)).Text ?? throw new IOException("Missing local body"));
            return text.ToString();
        });
        await Measure("resolve-40-web", async () =>
        {
            var text = new StringBuilder();
            foreach (var reference in refs.Take(320).Where((_, i) => i % 8 == 0)) text.Append((await store.ResolveAsync(reference)).Text ?? throw new IOException("Missing web body"));
            return text.ToString();
        });
        await Measure("catalog-first-page", async () => JsonSerializer.Serialize(await catalog.ReadAsync(1), Wire.Options));
        var report = JsonSerializer.Serialize(new { mode, references = refs.Length, results }, new JsonSerializerOptions(Wire.Options) { WriteIndented = true });
        File.WriteAllText(Path.Combine(evidence, mode + ".json"), report); Console.WriteLine(report); return 0;
    }
}
