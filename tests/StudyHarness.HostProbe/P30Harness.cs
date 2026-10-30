using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P30Harness
{
    private const string BookId = "40422c12-4fff-3948-91cd-0ac399ecfecd";
    private sealed class Knowledge(CollectionSnapshot snapshot, Dictionary<SourceRef, string> texts) : IKnowledgeStore, ICollectionKnowledge
    {
        public Task<ResolvedSource> ResolveAsync(SourceRef r, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new ResolvedSource(r, texts.GetValueOrDefault(r), null)); }
        public Task<IReadOnlyList<CollectionSnapshot>> CollectionsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionSnapshot>>([snapshot]);
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveAsync(Guid l, string q, IReadOnlyList<Guid> c, int b, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KnowledgeFragment>>([]);
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection c, string q, IReadOnlyList<string> e, int b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryDto?> CurrentAsync(CancellationToken ct = default) => Task.FromResult<LibraryDto?>(null);
        public Task<Result<LibraryDto>> SwitchAsync(Command<HostSelection> c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ImportReport>> ImportAsync(Command<HostSelection> c, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnowledgeSearch> SearchAsync(Guid l, string q, IReadOnlyCollection<Guid> p, int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CollectionSnapshot>> ImportCollectionAsync(HostSelection s, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed record Charge(int Number, string Purpose, decimal Reserved, decimal ChargedUpper, TokenUsage? Usage, string Status);
    private sealed class Meter(IModelProvider inner, string root, bool replayFirst = false, string? replayTheory = null) : IModelProvider
    {
        private bool replayed;
        public string Destination => inner.Destination;
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            if (replayTheory is not null && !replayed && request.Sources[0].Id == "theory-v3-context")
            {
                replayed = true;
                Console.WriteLine("Revalidating saved lesson response with unchanged source snapshot; no model charge.");
                return JsonSerializer.Deserialize<Result<ProviderReply>>(await File.ReadAllTextAsync(Path.Combine(root, replayTheory), ct), Wire.Options)!;
            }
            if (replayFirst && !replayed && request.Sources[0].Id == "book-knowledge")
            {
                replayed = true;
                Console.WriteLine("Replaying unchanged first frozen-batch response; original failure/cost retained.");
                return JsonSerializer.Deserialize<Result<ProviderReply>>(await File.ReadAllTextAsync(Path.Combine(root, "reply-001.json"), ct), Wire.Options)!;
            }
            if (request.Model != "deepseek-flash") throw new InvalidOperationException("Price approval covers deepseek-flash only");
            var path = Path.Combine(root, "cost-ledger.json");
            var ledger = File.Exists(path) ? JsonSerializer.Deserialize<List<Charge>>(await File.ReadAllTextAsync(path, ct), Wire.Options)! : [];
            var inputBound = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.Sources)) + 16000;
            var reserve = (inputBound * 2m + request.MaxOutputTokens * 8m) / 1000000;
            if (ledger.Sum(c => c.ChargedUpper) + reserve > 20m) throw new InvalidOperationException("20 CNY total budget would be exceeded; no dispatch");
            var number = ledger.Count + 1;
            ledger.Add(new(number, request.Sources[0].Id, reserve, reserve, null, "reserved-before-dispatch"));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ledger, Wire.Options), ct);
            var reply = await inner.SendAsync(request, ct);
            var usage = reply.Value?.Usage;
            ledger[^1] = ledger[^1] with { Usage = usage, ChargedUpper = usage is null ? reserve : (usage.PromptTokens * 2m + usage.CompletionTokens * 8m) / 1000000, Status = reply.Ok ? "received" : reply.Error!.Code.ToString() };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ledger, Wire.Options), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(root, $"reply-{number:D3}.json"), JsonSerializer.Serialize(reply, Wire.Options), CancellationToken.None);
            Console.WriteLine($"dispatch {number} {request.Sources[0].Id}: {ledger[^1].Status}; cumulative upper CNY {ledger.Sum(c => c.ChargedUpper):F4}");
            return reply;
        }
    }
    private static SqliteConnection ReadOnly(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open(); return db;
    }
    internal static async Task<int> Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = Path.GetFullPath(args[Array.IndexOf(args, "--evidence") + 1]);
        Directory.CreateDirectory(root);
        var sourceRoot = Path.GetFullPath("data");
        var marker = Path.Combine(root, "p30-isolated.marker");
        if (!File.Exists(marker) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Isolated P30 directory required");
        await File.WriteAllTextAsync(marker, "Only approved book; no user learning mutations.");
        var local = Path.Combine(root, "source-collections.db");
        if (!File.Exists(local))
        {
            using var source = ReadOnly(Path.Combine(sourceRoot, "library", "collections.db"));
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = local, Pooling = false }.ToString()); target.Open(); source.BackupDatabase(target);
            using var appSource = ReadOnly(Path.Combine(sourceRoot, "app.db"));
            using var appTarget = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "app.db"), Pooling = false }.ToString()); appTarget.Open(); appSource.BackupDatabase(appTarget);
        }
        using var db = ReadOnly(local); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM collections WHERE id=$id"; cmd.Parameters.AddWithValue("$id", BookId);
        var collection = JsonSerializer.Deserialize<SourceCollection>((string)cmd.ExecuteScalar()!, Wire.Options)!;
        cmd.CommandText = "SELECT manifest FROM collection_versions WHERE collection_id=$id AND version=$version"; cmd.Parameters.AddWithValue("$version", collection.Version);
        var manifest = JsonSerializer.Deserialize<CoverageManifest>((string)cmd.ExecuteScalar()!, Wire.Options)!;
        cmd.CommandText = "SELECT text,reference FROM chunks WHERE collection_id=$id AND version=$version ORDER BY rowid";
        var texts = new Dictionary<SourceRef, string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) texts.Add(JsonSerializer.Deserialize<SourceRef>(reader.GetString(1), Wire.Options)!, reader.GetString(0));
        var snapshot = new CollectionSnapshot(collection, manifest, texts.Keys.ToArray());
        var knowledge = new Knowledge(snapshot, texts); var store = new SqliteSettingsStore(root); var settings = new SettingsService(store);
        var config = await settings.GetAsync();
        var caps = new DeepSeekCapabilities(root, store, DeepSeekCapabilities.CreateClient());
        var plans = new SqlitePlanningStore(root);
        var service = new PlanningService(plans, knowledge, settings, new(new Meter(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()), root, args.Contains("--replay-first")), caps), new PlanParser(), explicitSourcesOnly: true, knowledgeCache: new BookKnowledgeCache(root));
        service.Progress += Console.WriteLine;
        var draft = (await plans.ListAsync()).FirstOrDefault();
        if (draft is null) draft = (await service.StartAsync(new(Guid.NewGuid(), 0,
            new PlanningInput("按所选《数字逻辑与计算机组成》全书逐章深入学习，不遗漏独立机制、必要推导和计算训练。", "学过C语言，数字逻辑与计算机组成基础较弱", "理解各章原理，能够解释机制并完成计算与推演题", 300, "Adaptive", [texts.Keys.First()])))).Value!;
        if (draft.Plan is null)
        {
            var prepared = await service.PrepareAsync(draft.Id, draft.Revision);
            if (!prepared.Ok) throw new IOException(prepared.Error!.Message);
            var preview = prepared.Value!;
            if (args.Contains("--restore-inventory"))
            {
                var restore = Array.IndexOf(args, "--restore-inventory");
                var batch = preview.KnowledgeBatches.Single(b => b.Key == args[restore + 1]);
                var response = JsonSerializer.Deserialize<Result<ProviderReply>>(await File.ReadAllTextAsync(Path.Combine(root, args[restore + 2])), Wire.Options)!;
                if (!response.Ok) throw new IOException("Only a received reply can be revalidated");
                await new BookKnowledgeCache(root).SaveKnowledgeAsync(BookKnowledge.Parse(batch, response.Value!.Text), default);
                Console.WriteLine("Revalidated saved inventory reply: " + batch.Key);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "preflight.json"), JsonSerializer.Serialize(new { collection.EntryCount, collection.ReadyCount, characters = texts.Values.Sum(t => t.Length), chunks = texts.Count, model = config.Model, batches = preview.KnowledgeBatches.Count,
                batchInputBytes = preview.KnowledgeBatches.Sum(b => b.Review.Budget!.InputUpperBound), maxBatchOutput = preview.KnowledgeBatches.Sum(b => b.Review.MaxOutputTokens), sourceFingerprint = collection.SourceFingerprint }, Wire.Options));
            if (!args.Contains("--real")) { Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(root, "preflight.json"))); return 0; }
            var result = await service.ProposeAsync(preview, true);
            await File.WriteAllTextAsync(Path.Combine(root, "plan-result.json"), JsonSerializer.Serialize(result, Wire.Options));
            if (!result.Ok) throw new IOException(result.Error!.Message);
            draft = result.Value!;
        }
        await File.WriteAllTextAsync(Path.Combine(root, "coverage.txt"), PlanTeachingCoverage.Describe(draft.Plan!));
        if (!args.Contains("--lessons") && !args.Contains("--audit-lessons")) return 0;
        if (args.Contains("--lessons") && !args.Contains("--real")) throw new InvalidOperationException("Explicit real flag required");
        if (!draft.Confirmed) draft = (await service.ConfirmAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, draft.Plan!.Hash, true)))).Value!;
        var learning = new SqliteLearningStore(root, plans);
        var actualKnowledge = new KnowledgeStore(root);
        foreach (var source in texts)
            if ((await actualKnowledge.ResolveAsync(source.Key)).Text != source.Value) throw new IOException("Isolated production source resolution differs");
        var generation = new GenerationService(plans, learning, learning, actualKnowledge, settings,
            new(new Meter(new DeepSeekTheoryProvider(store, DeepSeekProvider.CreateClient()), root, replayTheory: args.Contains("--replay-theory") ? args[Array.IndexOf(args, "--replay-theory") + 1] : null), caps), new TheoryLessonParser());
        var nodes = draft.Plan!.Candidate.Nodes.Where(n => n.Type == "theory").ToArray();
        if (args.Contains("--repair-theory"))
        {
            // Explicit offline reviewer feedback; separate evidence, never an automatic quality-pass claim.
            var index = Array.IndexOf(args, "--repair-theory");
            var node = nodes.Single(n => n.Key == args[index + 1]);
            var prior = JsonSerializer.Deserialize<Result<ProviderReply>>(await File.ReadAllTextAsync(Path.Combine(root, args[index + 2])), Wire.Options)!;
            var feedback = await File.ReadAllTextAsync(Path.Combine(root, args[index + 3]));
            var prepared = await generation.PrepareAsync(draft.Id, node.Key);
            if (!prepared.Ok) throw new IOException(prepared.Error!.Message);
            var original = prepared.Value!.Reviews[node.Key];
            var repair = new ModelEgress(new Meter(new DeepSeekTheoryProvider(store, DeepSeekProvider.CreateClient()), root), caps);
            var review = await repair.PrepareAdaptiveAsync(original.Model, original.Sources.Concat(new[] {
                new ModelSource("review-feedback", 1, ModelSourceKind.UserText,
                    "以下是审阅发现的具体错误。仅修正错误及直接相关的说明，保留其余已正确内容、知识覆盖与引用。返回完整课件JSON。\n" + feedback + "\n待修订JSON：\n" + prior.Value!.Text) }), false);
            var result = await repair.SendAsync(review, repair.Approve(review, ConsentMode.Once));
            if (!result.Ok) throw new IOException(result.Error!.Message);
            return 0;
        }
        if (args.Contains("--inspect-theory"))
        {
            var index = Array.IndexOf(args, "--inspect-theory");
            var node = nodes.Single(n => n.Key == args[index + 1]);
            var sent = await CourseMaterials.SelectAsync(actualKnowledge, draft.Plan, node, default);
            var reply = JsonSerializer.Deserialize<Result<ProviderReply>>(await File.ReadAllTextAsync(Path.Combine(root, args[index + 2])), Wire.Options)!;
            var parsed = new TheoryLessonParser().Parse(reply.Value!.Text, sent);
            Console.WriteLine(JsonSerializer.Serialize(new { parsed.Ok, parsed.Error, contextChecksAdvisory = true }, Wire.Options));
            return 0;
        }
        if (args.Contains("--audit-lessons"))
        {
            var audit = new List<object>();
            foreach (var node in nodes)
            {
                var prepared = await generation.PrepareAsync(draft.Id, node.Key);
                audit.Add(new { node.Key, node.Title, prepared.Ok, prepared.Error,
                    sources = prepared.Value?.Reviews[node.Key].Sources.Count - 1, input = prepared.Value?.Reviews[node.Key].Budget?.InputUpperBound });
            }
            await File.WriteAllTextAsync(Path.Combine(root, "lesson-context-audit.json"), JsonSerializer.Serialize(audit, Wire.Options));
            if (!args.Contains("--lessons")) return 0;
        }
        foreach (var node in new[] { nodes.First(), nodes.First(n => n.Title.Contains("加法器") && n.Title.Contains("标志")),
            nodes.First(n => n.Title.StartsWith("直接映射", StringComparison.Ordinal)), nodes.First(n => n.Title.StartsWith("DMA", StringComparison.Ordinal)) }.Distinct())
        {
            if ((await learning.GetLessonAsync(draft.Id, node.Key)).Ok) continue;
            var task = await generation.GenerateAsync(draft.Id, node.Key);
            if (!task.Ok) throw new IOException(task.Error!.Message);
            while (await generation.TickAsync()) { }
            var lesson = await learning.GetLessonAsync(draft.Id, node.Key);
            await File.WriteAllTextAsync(Path.Combine(root, "lesson-" + node.Key + ".json"), JsonSerializer.Serialize(lesson, Wire.Options));
            if (!lesson.Ok) throw new IOException(lesson.Error!.Message);
        }
        return 0;
    }
}
