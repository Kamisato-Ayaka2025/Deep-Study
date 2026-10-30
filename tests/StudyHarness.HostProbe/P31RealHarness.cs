using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P31RealHarness
{
    private const string BookId = "5b7623a3-03cf-34e0-7d46-7152b4aed693";
    private sealed class Knowledge(CollectionSnapshot[] snapshots, Dictionary<SourceRef, string> texts) : IKnowledgeStore, ICollectionKnowledge
    {
        public Task<ResolvedSource> ResolveAsync(SourceRef r, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new ResolvedSource(r, texts.GetValueOrDefault(r), null)); }
        public Task<IReadOnlyList<CollectionSnapshot>> CollectionsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionSnapshot>>(snapshots.Where(s => s.References[0].LibraryId == id).ToArray());
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveAsync(Guid l, string q, IReadOnlyList<Guid> c, int b, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KnowledgeFragment>>([]);
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection c, string q, IReadOnlyList<string> e, int b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryDto?> CurrentAsync(CancellationToken ct = default) => Task.FromResult<LibraryDto?>(null);
        public Task<Result<LibraryDto>> SwitchAsync(Command<HostSelection> c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ImportReport>> ImportAsync(Command<HostSelection> c, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnowledgeSearch> SearchAsync(Guid l, string q, IReadOnlyCollection<Guid> p, int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CollectionSnapshot>> ImportCollectionAsync(HostSelection s, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed record Charge(int Number, string Purpose, decimal Reserved, decimal ChargedUpper, TokenUsage? Usage, string Status);
    private sealed class Meter(IModelProvider inner, string root, bool replayFirst = false, string? replayTheory = null, decimal baseline = 0) : IModelProvider
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
            if (baseline + ledger.Sum(c => c.ChargedUpper) + reserve > 20m) throw new InvalidOperationException("20 CNY total budget would be exceeded; no dispatch");
            var number = ledger.Count + 1;
            ledger.Add(new(number, request.Sources[0].Id, reserve, reserve, null, "reserved-before-dispatch"));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ledger, Wire.Options), ct);
            await File.WriteAllTextAsync(Path.Combine(root, $"request-{number:D3}.json"), JsonSerializer.Serialize(request.Sources, Wire.Options), ct);
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

    private sealed class NoCalls : IModelProvider
    {
        public string Destination => "local:offline-replay";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest r, CancellationToken ct) => throw new InvalidOperationException("Unexpected dispatch during cached mapping replay");
    }
    private sealed class ReplayCaps : IModelCapabilitySource
    {
        public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct) => Task.FromResult(new ModelCapabilities(model, 1048576, 32768, true, DateTimeOffset.UtcNow, true));
    }
    private sealed class ReplayLesson(string input, string root) : IModelProvider
    {
        private int index;
        public string Destination => "local:offline-replay";
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest r, CancellationToken ct)
        {
            var number = index++ == 0 ? 117 : 119;
            await File.WriteAllTextAsync(Path.Combine(root, $"updated-request-{number}.json"), JsonSerializer.Serialize(r.Sources, Wire.Options), ct);
            return JsonSerializer.Deserialize<Result<ProviderReply>>(await File.ReadAllTextAsync(Path.Combine(input, $"reply-{number}.json"), ct), Wire.Options)!;
        }
    }
    public static async Task<int> Feedback(string[] args)
    {
        var input = Path.GetFullPath("artifacts/acceptance/P31/real-network-exam");
        var root = Path.GetFullPath(args[Array.IndexOf(args, "--evidence") + 1]); Directory.CreateDirectory(root);
        foreach (var name in new[] { "app.db", "planning.db", "book-knowledge.db" }) Backup(Path.Combine(input, name), Path.Combine(root, name));
        PlanCandidate Candidate(int number) => new PlanParser().Parse(JsonSerializer.Deserialize<Result<ProviderReply>>(File.ReadAllText(Path.Combine(input, $"reply-{number:D3}.json")), Wire.Options)!.Value!.Text).Value!;
        var before = Candidate(19); var after = Candidate(66);
        var plans = new SqlitePlanningStore(root); var draft = (await plans.ListAsync()).Single();
        var collections = draft.Plan!.Collections!;
        var egress = new ModelEgress(new NoCalls(), new ReplayCaps());
        var first = await BookKnowledgeMapping.CreateAsync(before, collections, "deepseek-flash", egress, new BookKnowledgeCache(root), Console.WriteLine, _ => { }, _ => Task.FromResult(true), default);
        if (!first.Ok) throw new IOException(first.Error!.Message);
        var second = await BookKnowledgeMapping.CreateAsync(after, collections, "deepseek-flash", egress, new BookKnowledgeCache(root), Console.WriteLine, _ => { }, _ => Task.FromResult(true), default);
        if (!second.Ok || PlanRules.Hash(first.Value!) != PlanRules.Hash(second.Value!)) throw new IOException("Repair mapping did not restore identically");
        var originals = JsonSerializer.Deserialize<ModelSource[]>(File.ReadAllText(Path.Combine(input, "request-119.json")), Wire.Options)!;
        var conflict = ExamGraphConflicts.Describe(originals);
        if (!conflict.Contains("y-z=8") || !conflict.Contains("y-z=12")) throw new IOException("Actual OCR conflict not detected");
        await File.WriteAllTextAsync(Path.Combine(root, "graph-conflict.txt"), conflict);
        var texts = new Dictionary<SourceRef, string>();
        foreach (var path in new[] { "source-collections.db", "exam-library/collections.db" })
        {
            using var db = ReadOnly(Path.Combine(input, path)); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT reference,text FROM chunks";
            using var reader = cmd.ExecuteReader(); while (reader.Read()) texts[JsonSerializer.Deserialize<SourceRef>(reader.GetString(0), Wire.Options)!] = reader.GetString(1);
        }
        var store = new SqliteSettingsStore(root); var settings = new SettingsService(store);
        var baseline = JsonSerializer.Deserialize<List<Charge>>(File.ReadAllText(Path.Combine(input, "cost-ledger.json")), Wire.Options)!.Sum(c => c.ChargedUpper);
        if (args.Contains("--prior-ledger"))
            foreach (var ledger in args[Array.IndexOf(args, "--prior-ledger") + 1].Split('|'))
                baseline += JsonSerializer.Deserialize<List<Charge>>(File.ReadAllText(ledger), Wire.Options)!.Sum(c => c.ChargedUpper);
        IModelProvider provider = args.Contains("--real") ? new Meter(new DeepSeekTheoryProvider(store, DeepSeekProvider.CreateClient()), root, baseline: baseline) : new ReplayLesson(input, root);
        var learning = new SqliteLearningStore(root, plans);
        IModelCapabilitySource capabilities = args.Contains("--real") ? new DeepSeekCapabilities(root, store, DeepSeekCapabilities.CreateClient()) : new ReplayCaps();
        var generation = new GenerationService(plans, learning, learning, new Knowledge([], texts), settings,
            new(provider, capabilities), new TheoryLessonParser());
        var selected = args.Contains("--lesson") ? args[Array.IndexOf(args, "--lesson") + 1].Split(',') : ["n42", "n44"];
        foreach (var key in selected)
        {
            var generated = await generation.GenerateAsync(draft.Id, key); if (!generated.Ok) throw new IOException(generated.Error!.Message);
            while (await generation.TickAsync()) { }
            var lesson = await learning.GetLessonAsync(draft.Id, key);
            await File.WriteAllTextAsync(Path.Combine(root, $"lesson-{key}.json"), JsonSerializer.Serialize(lesson, Wire.Options));
            if (!lesson.Ok) throw new IOException(lesson.Error!.Message);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "feedback-replay.json"), JsonSerializer.Serialize(new { mappingPoints = second.Value!.Count,
            mappingModelCalls = 0, restartRestored = true, ocrConflictDetected = true, originalRepliesStillAccepted = !args.Contains("--real"),
            realLessons = args.Contains("--real") ? selected.Length : 0, priorCostUpper = baseline }, Wire.Options));
        return 0;
    }

    private sealed class OcrMeter(IOcrProvider inner, string root) : IOcrProvider
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage r, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (r.Model != "deepseek-flash") throw new IOException("Unpriced model");
                var path = Path.Combine(root, "cost-ledger.json");
                var ledger = File.Exists(path) ? JsonSerializer.Deserialize<List<Charge>>(await File.ReadAllTextAsync(path, ct), Wire.Options)! : [];
                const decimal reserve = 2.1m; // entire 1M context + 8192 output at peak, conservative image bound
                if (ledger.Sum(x => x.ChargedUpper) + reserve > 20m) throw new IOException("Budget exhausted before OCR dispatch");
                var n = ledger.Count + 1;
                ledger.Add(new(n, "exam-ocr-page-" + r.Page.Page, reserve, reserve, null, "reserved-before-dispatch"));
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ledger, Wire.Options), ct);
                await File.WriteAllBytesAsync(Path.Combine(root, $"exam-page-{r.Page.Page}.png"), r.Page.Png, ct);
                var reply = await inner.RecognizeAsync(r, ct); var u = reply.Value?.Usage;
                ledger[^1] = ledger[^1] with { Usage = u, ChargedUpper = u is null ? reserve : (u.PromptTokens * 2m + u.CompletionTokens * 8m) / 1000000, Status = reply.Ok ? "received" : reply.Error!.Code.ToString() };
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ledger, Wire.Options));
                await File.WriteAllTextAsync(Path.Combine(root, $"ocr-{n:D3}.json"), JsonSerializer.Serialize(reply, Wire.Options));
                Console.WriteLine($"OCR page {r.Page.Page}: {reply.Ok}; upper CNY {ledger.Sum(x => x.ChargedUpper):F4}");
                return reply;
            }
            finally { gate.Release(); }
        }
    }
    private static void Backup(string source, string target)
    {
        if (File.Exists(target)) return;
        using var a = ReadOnly(source); using var b = new SqliteConnection($"Data Source={target};Pooling=False"); b.Open(); a.BackupDatabase(b);
    }
    internal static async Task<int> Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = Path.GetFullPath(args[Array.IndexOf(args, "--evidence") + 1]); Directory.CreateDirectory(root);
        var sourceRoot = Path.GetFullPath("artifacts/acceptance/P30/book-real");
        Backup(Path.Combine(sourceRoot, "app.db"), Path.Combine(root, "app.db"));
        Backup(Path.Combine(sourceRoot, "book-knowledge.db"), Path.Combine(root, "book-knowledge.db"));
        Backup(Path.Combine(sourceRoot, "approved-book-library", "collections.db"), Path.Combine(root, "source-collections.db"));
        using var db = ReadOnly(Path.Combine(root, "source-collections.db")); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM collections WHERE id=$id"; cmd.Parameters.AddWithValue("$id", BookId);
        var book = JsonSerializer.Deserialize<SourceCollection>((string)cmd.ExecuteScalar()!, Wire.Options)!;
        cmd.CommandText = "SELECT manifest FROM collection_versions WHERE collection_id=$id AND version=$version"; cmd.Parameters.AddWithValue("$version", book.Version);
        var manifest = JsonSerializer.Deserialize<CoverageManifest>((string)cmd.ExecuteScalar()!, Wire.Options)!;
        cmd.CommandText = "SELECT text,reference FROM chunks WHERE collection_id=$id AND version=$version ORDER BY rowid";
        var texts = new Dictionary<SourceRef, string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) texts.Add(JsonSerializer.Deserialize<SourceRef>(reader.GetString(1), Wire.Options)!, reader.GetString(0));
        var bookSnapshot = new CollectionSnapshot(book, manifest, texts.Keys.ToArray());
        var local = new KnowledgeStore(root); var folder = Path.Combine(root, "exam-library"); Directory.CreateDirectory(folder);
        var library = await local.CurrentAsync();
        if (library is null) library = (await local.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(folder)))).Value!;
        var exam = (await local.ImportCollectionAsync(HostSelection.FromExamFilePicker(@"E:\Code\Test_For_StudyHarness\计网-2023秋-国际班.pdf"), library.Id)).Value!;
        var store = new SqliteSettingsStore(root); var settings = new SettingsService(store);
        var config = await settings.GetAsync(); if (config.Model != "deepseek-flash") throw new IOException("Expected deepseek-flash");
        var caps = new DeepSeekCapabilities(root, store, DeepSeekCapabilities.CreateClient());
        if (args.Contains("--scan"))
        {
            var scan = new AutomaticPdfScanService(local, settings, caps, new OcrMeter(new DeepSeekOcrProvider(store, DeepSeekProvider.CreateClient()), root));
            var scanned = await scan.RunAsync(library.Id, exam.Collection.Id, true);
            if (!scanned.Ok) throw new IOException(scanned.Error!.Message);
            exam = (await local.CollectionsAsync(library.Id)).Single();
        }
        if (exam.Collection.ReadyCount != exam.Collection.EntryCount) { Console.WriteLine("Exam requires scan; use --scan"); return 0; }
        foreach (var r in exam.References) texts[r] = (await local.ResolveAsync(r)).Text!;
        await File.WriteAllTextAsync(Path.Combine(root, "exam-original.txt"), string.Join("\n\n", exam.References.Select(r => $"PAGE {r.Locator.Page}\n{texts[r]}")));
        var knowledge = new Knowledge([bookSnapshot, exam], texts);
        var cache = new BookKnowledgeCache(root); var plans = new SqlitePlanningStore(root);
        var service = new PlanningService(plans, knowledge, settings, new(new Meter(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()), root), caps), new PlanParser(), explicitSourcesOnly: true, knowledgeCache: cache);
        service.Progress += Console.WriteLine;
        var draft = (await plans.ListAsync()).FirstOrDefault();
        if (draft is null) draft = (await service.StartAsync(new(Guid.NewGuid(), 0, new PlanningInput("结合计算机网络教材和所附期末试卷，规划复习课程，参考考点、题型和难度并补齐必要基础。", "学过课程但概念和计算不熟练", "通过计算机网络期末考试", 300, "Adaptive", [bookSnapshot.References[0], exam.References[0]], ExamCollectionIds: [exam.Collection.Id])))).Value!;
        if (draft.Plan is null)
        {
            var p = await service.PrepareAsync(draft.Id, draft.Revision); if (!p.Ok) throw new IOException(p.Error!.Message);
            int reused = 0; foreach (var b in p.Value!.KnowledgeBatches) if (await cache.ReadKnowledgeAsync(b.Fingerprint, default) is not null) reused++;
            await File.WriteAllTextAsync(Path.Combine(root, "preflight.json"), JsonSerializer.Serialize(new { textbookPages = book.EntryCount, examPages = exam.Collection.EntryCount, batches = p.Value.KnowledgeBatches.Count, reused }, Wire.Options));
            Console.WriteLine($"Batches {p.Value.KnowledgeBatches.Count}, cached {reused}");
            if (!args.Contains("--real")) return 0;
            var result = await service.ProposeAsync(p.Value, true);
            await File.WriteAllTextAsync(Path.Combine(root, "plan-result.json"), JsonSerializer.Serialize(result, Wire.Options));
            if (!result.Ok) throw new IOException(result.Error!.Message); draft = result.Value!;
        }
        await File.WriteAllTextAsync(Path.Combine(root, "plan.json"), JsonSerializer.Serialize(draft, Wire.Options));
        if (!args.Contains("--lesson")) return 0;
        if (!draft.Confirmed) draft = (await service.ConfirmAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, draft.Plan!.Hash, true)))).Value!;
        var nodeKeys = args[Array.IndexOf(args, "--lesson") + 1].Split(',');
        var learning = new SqliteLearningStore(root, plans);
        var generation = new GenerationService(plans, learning, learning, knowledge, settings, new(new Meter(new DeepSeekTheoryProvider(store, DeepSeekProvider.CreateClient()), root), caps), new TheoryLessonParser());
        foreach (var nodeKey in nodeKeys)
        {
        if (!(await learning.GetLessonAsync(draft.Id, nodeKey)).Ok)
        {
            var result = await generation.GenerateAsync(draft.Id, nodeKey); if (!result.Ok) throw new IOException(result.Error!.Message);
            while (await generation.TickAsync()) { }
        }
        var lesson = await learning.GetLessonAsync(draft.Id, nodeKey);
        await File.WriteAllTextAsync(Path.Combine(root, $"lesson-{nodeKey}.json"), JsonSerializer.Serialize(lesson, Wire.Options));
        if (!lesson.Ok) throw new IOException(lesson.Error!.Message);
        }
        return 0;
    }
}
