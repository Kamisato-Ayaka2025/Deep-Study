using System.Text;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P25Harness
{
    private sealed class Caps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string m, CancellationToken ct) => Task.FromResult(new ModelCapabilities(m, 1048576, 32768, true, DateTimeOffset.UtcNow, true)); }
    internal static async Task<int> Run(string[] args)
    {
        var root = Path.GetFullPath(args[Array.IndexOf(args, "--evidence") + 1]);
        if (Directory.Exists(root)) throw new IOException("Fresh evidence root required");
        Directory.CreateDirectory(root); var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var knowledge = new KnowledgeStore(root);
        var id = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)))).Value!.Id;
        foreach (var path in Directory.GetFiles(@"E:\Code\Test_For_StudyHarness\PPT", "*.pptx"))
        {
            var imported = await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(path), id);
            if (!imported.Ok) throw new IOException(imported.Error!.Message);
        }
        var snapshots = await knowledge.CollectionsAsync(id);
        var collections = snapshots.Select(c => new PlanCollection(id, c.Manifest, c.References, c.Structure)).ToArray();
        var previews = snapshots.SelectMany(c => (c.Summaries ?? []).Select(s => ((c.Collection.Id, s.EntryKey), s.Text[..Math.Min(80, s.Text.Length)]))).ToDictionary(x => x.Item1, x => x.Item2);
        var legacy = PlanningDirectory.Build(collections, previews, new HashSet<Guid>());
        var compact = PlanningDirectory.Build(collections, previews, snapshots.Select(c => c.Collection.Id).ToHashSet());
        var settings = new SettingsService(new SqliteSettingsStore(root)); var provider = new FakeProvider();
        var service = new PlanningService(new SqlitePlanningStore(root), knowledge, settings, new(provider, new Caps()), new PlanParser(), explicitSourcesOnly: true);
        var draft = (await service.StartAsync(new(Guid.NewGuid(), 0, new PlanningInput("学习全部计算机网络课件，逐章深入学习", "零基础", "理解并计算各章机制", 300, "Adaptive", snapshots.Select(c => c.References.First()).ToArray())))).Value!;
        var result = await service.PrepareAsync(draft.Id, draft.Revision);
        if (!result.Ok) throw new IOException(result.Error!.Message);
        var text = result.Value!.Review.Sources[0].Text;
        var start = text.IndexOf("计划正文取样分布", StringComparison.Ordinal);
        var end = text.IndexOf("冻结完整目录", start < 0 ? 0 : start, StringComparison.Ordinal);
        var distribution = start >= 0 && end > start ? text[start..end] : "";
        var report = new { distribution, slides = snapshots.Sum(c => c.Manifest.Entries.Count), ready = snapshots.Sum(c => c.Collection.ReadyCount), legacyDirectoryBytes = Encoding.UTF8.GetByteCount(legacy), compactDirectoryBytes = Encoding.UTF8.GetByteCount(compact), inputBytes = result.Value!.Review.Budget!.InputUpperBound, largestBlockCharacters = result.Value.Review.Sources.Max(s => s.Text.Length), remoteCalls = 0, note = "Real PPTX native import only; existing user OCR cache was not read. Unrecognized pages remain unavailable." };
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, Wire.Options));
        Console.WriteLine(JsonSerializer.Serialize(report, Wire.Options)); return 0;
    }
}
