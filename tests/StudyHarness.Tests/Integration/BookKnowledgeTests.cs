using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class BookKnowledgeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-P30", Guid.NewGuid().ToString("N"));
    internal sealed class Caps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string m, CancellationToken ct) => Task.FromResult(new ModelCapabilities(m, 1048576, 32768, true, DateTimeOffset.UtcNow, true)); }
    internal sealed class Knowledge : IKnowledgeStore, ICollectionKnowledge
    {
        public Dictionary<SourceRef, string> Texts { get; } = [];
        public CollectionSnapshot Snapshot { get; }
        public Knowledge(int pages = 12, int length = 4000, string sourceKind = "pdf")
        {
            var library = Guid.NewGuid(); var id = Guid.NewGuid();
            for (var p = 1; p <= pages; p++)
            {
                var text = $"# 教学小节{p}\n定义条件和跨页符号说明。" + new string('文', length) + $"\n页尾重要机制{p}：零标志位与溢出标志位不同。";
                var r = new SourceRef(library, Guid.NewGuid(), Guid.NewGuid(), PlanRules.Hash(text), new("synthetic.pdf", p, "小节", $"page-{p}:0-{text.Length}"));
                Texts[r] = text;
            }
            Snapshot = new(new(id, sourceKind, "fingerprint", DateTimeOffset.UtcNow, "synthetic.pdf", "v1", pages, pages),
                new(id, "v1", "directory", true, Texts.Keys.Select(r => new CollectionEntry("page-" + r.Locator.Page, r.Locator.Page.ToString()!, "教学小节", "textReady", r.ContentHash, null, [])).ToArray()), Texts.Keys.ToArray());
        }
        public PlanCollection Collection => new(Snapshot.References[0].LibraryId, Snapshot.Manifest, Snapshot.References, SourceKind: Snapshot.Collection.SourceKind);
        public Task<ResolvedSource> ResolveAsync(SourceRef r, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new ResolvedSource(r, Texts.GetValueOrDefault(r), null)); }
        public Task<IReadOnlyList<CollectionSnapshot>> CollectionsAsync(Guid l, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionSnapshot>>([Snapshot]);
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveAsync(Guid l, string q, IReadOnlyList<Guid> c, int b, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KnowledgeFragment>>([]);
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection c, string q, IReadOnlyList<string> e, int b, CancellationToken ct = default) => throw new NotSupportedException("New mapped lessons must not fall back to keyword retrieval");
        public Task<LibraryDto?> CurrentAsync(CancellationToken ct = default) => Task.FromResult<LibraryDto?>(null);
        public Task<Result<LibraryDto>> SwitchAsync(Command<HostSelection> c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ImportReport>> ImportAsync(Command<HostSelection> c, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnowledgeSearch> SearchAsync(Guid l, string q, IReadOnlyCollection<Guid> p, int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CollectionSnapshot>> ImportCollectionAsync(HostSelection s, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Provider : IModelProvider
    {
        public int Calls; public int? FailAt; public string? InventoryReply; public string Destination => "local:deterministic-fake";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Calls++;
            if (Calls == FailAt) return Task.FromResult(Result<ProviderReply>.Fail(ErrorCode.TRANSIENT_PROVIDER_ERROR, "synthetic failure"));
            if (request.Sources[0].Id != "book-knowledge") return Task.FromResult(Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "stop before plan"));
            return Task.FromResult(Result<ProviderReply>.Success(new(InventoryReply ?? Reply(request.Sources), false)));
        }
    }
    private static string Reply(IEnumerable<ModelSource> sources) => JsonSerializer.Serialize(new
    {
        points = sources.Where(s => s.Id.StartsWith('b') && s.Id != "book-knowledge").Select(s => new { title = "页尾机制", detail = "解释零标志与溢出标志的区别及其条件", evidenceIds = new[] { s.Id }, quote = "零标志位与溢出标志位不同。" }),
        nonTeachingIds = Array.Empty<string>()
    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    [Fact]
    public async Task FullBook_IncludesEveryTailAndCompleteChunk_NoDispatchDuringPreparation()
    {
        var k = new Knowledge(384, 300); var provider = new Provider();
        var batches = await BookKnowledge.PrepareAsync(k, [k.Collection], new(provider, new Caps()), "deepseek-flash", default);
        Assert.Equal(0, provider.Calls);
        Assert.True(k.Texts.Keys.ToHashSet().SetEquals(batches.SelectMany(b => b.Sources).Select(s => s.Reference)));
        foreach (var batch in batches)
        {
            Assert.True(batch.Sources.Count <= 82);
            foreach (var source in batch.Sources) Assert.Contains(k.Texts[source.Reference], batch.Review.Sources.Single(s => s.Id == source.Key).Text);
            Assert.Equal(batch.Sources.Count, BookKnowledge.Parse(batch, Reply(batch.Review.Sources)).Points.Count);
        }
    }
    [Fact]
    public async Task SparsePresentation1500Slides_UsesLargerCompleteBatchesAndDedicatedCache()
    {
        var k = new Knowledge(1500, 80, "presentation"); var provider = new Provider();
        var gate = new ModelEgress(provider, new Caps());
        var ppt = await BookKnowledge.PrepareAsync(k, [k.Collection], gate, "deepseek-flash", default);
        var pdf = await BookKnowledge.PrepareAsync(k, [k.Collection with { SourceKind = "pdf" }], gate, "deepseek-flash", default);
        Assert.True(ppt.Count < pdf.Count);
        Assert.Equal(0, provider.Calls);
        Assert.True(k.Texts.Keys.ToHashSet().SetEquals(ppt.SelectMany(b => b.Sources).Select(s => s.Reference)));
        Assert.All(ppt, b => { Assert.True(b.Sources.Count <= 96); Assert.Contains("幻灯片专用", b.Review.Sources[0].Text); });
        Assert.Contains(ppt[^1].Sources, s => s.Reference.Locator.Page == 1500);
        var again = await BookKnowledge.PrepareAsync(k, [k.Collection], gate, "deepseek-flash", default);
        Assert.Equal(ppt.Select(b => b.Fingerprint), again.Select(b => b.Fingerprint));
        Assert.NotEqual(ppt[0].Fingerprint, pdf[0].Fingerprint);
        // A repair with the full prior candidate and error feedback still fits the source-count limit.
        await gate.PrepareAdaptiveAsync("deepseek-flash", ppt[0].Review.Sources.Concat([
            new("invalid-candidate", 0, ModelSourceKind.OriginalMaterial, "{}"),
            new("inventory-repair", 0, ModelSourceKind.UserText, "修复")]), false);
    }
    [Theory]
    [InlineData(1000)]
    [InlineData(8000)]
    public async Task PresentationSlideChunks_RemainCompleteAndOversizedSlidesAdvance(int length)
    {
        var k = new Knowledge(32, length, "presentation");
        var entries = k.Texts.ToArray(); k.Texts.Clear();
        foreach (var (pair, i) in entries.Select((p, i) => (p, i)))
            k.Texts.Add(pair.Key with { Locator = pair.Key.Locator with { Page = i / 4 + 1 } }, pair.Value);
        var collection = k.Collection with { References = k.Texts.Keys.ToArray() };
        var batches = await BookKnowledge.PrepareAsync(k, [collection], new(new Provider(), new Caps()), "deepseek-flash", default);
        Assert.True(k.Texts.Keys.ToHashSet().SetEquals(batches.SelectMany(b => b.Sources).Select(s => s.Reference)));
        if (length == 1000) Assert.Equal(1, batches[0].Sources.Count(s => s.Reference.Locator.Page == 6));
        var units = batches.Select(b => BookKnowledge.Parse(b, Reply(b.Review.Sources))).ToArray();
        var point = units[0].Points[1];
        var node = new PlannedNode("n1", "同主题", "theory", ["目标"], [], 30, null);
        var plan = new PlanDocument(new("PPT", "基础", "目标", 60, "Adaptive", [], [], [], [], [node], 2,
            KnowledgeCoverage: [new(point.Key, "included", ["n1"], null)]), [], [], "hash", null,
            Collections: [collection with { KnowledgeUnits = units }]);
        var selected = await CourseMaterials.SelectAsync(k, plan, node, default);
        Assert.Equal(4, selected.Count(s => s.Reference.Locator.Page == 1));
        Assert.DoesNotContain(selected, s => s.Reference.Locator.Page >= 3);
    }
    [Fact]
    public async Task MissingLabelsRetainOriginals_UnknownReferencesAndEmptyClaimsAreRejected()
    {
        var k = new Knowledge(2); var batch = Assert.Single(await BookKnowledge.PrepareAsync(k, [k.Collection], new(new Provider(), new Caps()), "deepseek-flash", default));
        var valid = Reply(batch.Review.Sources);
        using (var candidate = System.Text.Json.JsonDocument.Parse(valid))
        {
            var mixed = System.Text.Json.JsonSerializer.Serialize(new { points = candidate.RootElement.GetProperty("points"), nonTeachingIds = new[] { "b1" } });
            var normalized = BookKnowledge.Parse(batch, mixed);
            Assert.DoesNotContain("b1", normalized.NonTeachingIds);
            Assert.Contains(normalized.Points, p => p.EvidenceIds.Contains("b1"));
        }
        Assert.Throws<InvalidDataException>(() => BookKnowledge.Parse(batch, valid.Replace("b1", "unknown")));
        var fallback = BookKnowledge.Parse(batch, "{\"points\":[],\"nonTeachingIds\":[]}");
        Assert.Equal(2, fallback.Points.Count);
        foreach (var point in fallback.Points)
        {
            Assert.Contains("并非模型提取结论", point.Detail);
            Assert.Contains(k.Texts[batch.Sources.Single(s => s.Key == point.EvidenceIds.Single()).Reference], point.Detail);
        }
        Assert.Throws<InvalidDataException>(() => BookKnowledge.Parse(batch, valid.Replace("解释零标志与溢出标志的区别及其条件", "")));
        var first = BookKnowledge.Parse(batch with { Sources = [batch.Sources[0]] }, Reply(batch.Review.Sources.Where(s => s.Kind == ModelSourceKind.UserText || s.Id == batch.Sources[0].Key).ToArray()));
        var restBatch = batch with { Sources = batch.Sources.Skip(1).ToArray() };
        var rest = BookKnowledge.Parse(restBatch, Reply(batch.Review.Sources.Where(s => s.Kind == ModelSourceKind.UserText || restBatch.Sources.Any(x => x.Key == s.Id)).ToArray()));
        Assert.NotEmpty(BookKnowledge.Parse(batch, BookKnowledge.Merge(first, rest)).Points);
    }
    [Fact]
    public async Task DenseInventoryAndRepeatedLabelsAreAcceptedWithoutDroppingPoints()
    {
        var k = new Knowledge(2);
        var batch = Assert.Single(await BookKnowledge.PrepareAsync(k, [k.Collection], new(new Provider(), new Caps()), "deepseek-flash", default));
        var text = JsonSerializer.Serialize(new { points = Enumerable.Range(1, 120).Select(i => new {
            title = "机制" + i, detail = new string('文', 1601), evidenceIds = new[] { "b1", "b1" } }), nonTeachingIds = new[] { "b1", "b2", "b2" } });
        var parsed = BookKnowledge.Parse(batch, "```json\n" + text + "\n```");
        Assert.Equal(120, parsed.Points.Count);
        Assert.All(parsed.Points, p => Assert.Single(p.EvidenceIds));
        Assert.Equal(new[] { "b2" }, parsed.NonTeachingIds);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingLabelsUseAtMostOneInventoryCall_AndPersistResponseForResume(bool cached)
    {
        var k = new Knowledge(2); var provider = new Provider();
        var cache = new BookKnowledgeCache(root);
        var settings = new SettingsService(new SqliteSettingsStore(root));
        var service = new PlanningService(new SqlitePlanningStore(root), k, settings, new(provider, new Caps()), new PlanParser(), knowledgeCache: cache);
        var draft = (await service.StartAsync(new(Guid.NewGuid(), 0, new PlanningInput("学习全部", "基础", "掌握", 60, "Adaptive", [k.Snapshot.References[0]])))).Value!;
        var preview = (await service.PrepareAsync(draft.Id, draft.Revision)).Value!;
        var batch = Assert.Single(preview.KnowledgeBatches);
        provider.InventoryReply = "{\"points\":[{\"title\":\"机制\",\"detail\":\"具体条件\",\"evidenceIds\":[\"b1\"]}]}";
        if (cached) await cache.SaveDerivedAsync("inventory-response:" + batch.Fingerprint, provider.InventoryReply, default);
        Assert.False((await service.ProposeAsync(preview, true)).Ok); // Fake stops at curriculum, after inventory.
        Assert.Equal(cached ? 1 : 2, provider.Calls);
        Assert.Equal(provider.InventoryReply, await cache.ReadDerivedAsync("inventory-response:" + batch.Fingerprint, default));
        var unit = await new BookKnowledgeCache(root).ReadKnowledgeAsync(batch.Fingerprint, default);
        Assert.NotNull(unit); Assert.Equal(2, unit.Points.Count);
        Assert.Contains(k.Texts[batch.Sources[1].Reference], unit.Points[1].Detail);
    }
    [Fact]
    public async Task Cache_Reopens_AndFingerprintChangesWithVersionOrModel()
    {
        var k = new Knowledge(2); var egress = new ModelEgress(new Provider(), new Caps());
        var batch = Assert.Single(await BookKnowledge.PrepareAsync(k, [k.Collection], egress, "deepseek-flash", default));
        var unit = BookKnowledge.Parse(batch, Reply(batch.Review.Sources));
        await new BookKnowledgeCache(root).SaveKnowledgeAsync(unit, default);
        Assert.Equal(unit.Fingerprint, (await new BookKnowledgeCache(root).ReadKnowledgeAsync(batch.Fingerprint, default))!.Fingerprint);
        var changed = Assert.Single(await BookKnowledge.PrepareAsync(k, [k.Collection with { Manifest = k.Collection.Manifest with { Version = "v2" } }], egress, "deepseek-flash", default));
        Assert.Null(await new BookKnowledgeCache(root).ReadKnowledgeAsync(changed.Fingerprint, default));
        Assert.NotEqual(batch.Fingerprint, Assert.Single(await BookKnowledge.PrepareAsync(k, [k.Collection], egress, "deepseek-v4-pro", default)).Fingerprint);
    }
    [Fact]
    public async Task MappedLesson_UsesEvidenceAndNeighboursWithoutUnrelatedBatchContent()
    {
        var k = new Knowledge(12); var batches = await BookKnowledge.PrepareAsync(k, [k.Collection], new(new Provider(), new Caps()), "deepseek-flash", default);
        var units = batches.Select(b => BookKnowledge.Parse(b, Reply(b.Review.Sources))).ToArray();
        var node = new PlannedNode("n1", "同义词目标完全不匹配", "theory", ["理解 ZF 与 OF"], [], 30, null);
        var maps = units.SelectMany(u => u.Points).Select(p => new KnowledgeCoverage(p.Key, "excluded", [], "后续学习")).ToArray();
        maps[2] = maps[2] with { State = "included", NodeKeys = ["n1"], Reason = null, ContextKeys = [maps[^1].KnowledgeKey] };
        var candidate = new PlanCandidate("合成", "基础", "目标", 60, "Adaptive", [], [], [], [], [node], 2, KnowledgeCoverage: maps);
        var plan = new PlanDocument(candidate, [], [], "hash", null, Collections: [k.Collection with { KnowledgeUnits = units }]);
        Assert.Null(BookKnowledge.Validate(candidate, plan.Collections));
        var selected = await CourseMaterials.SelectAsync(k, plan, node, default);
        var expected = k.Texts.Keys.Where(r => r.Locator.Page is 2 or 3 or 4 or 11 or 12).ToHashSet();
        Assert.True(expected.SetEquals(selected.Select(s => s.Reference)));
        Assert.True(expected.SetEquals((await CourseMaterials.SelectAsync(k, plan, node with { Type = "lab" }, default)).Select(s => s.Reference)));
        Assert.All(selected, s => Assert.True(CourseMaterials.Allowed(plan, node, s)));
        Assert.NotNull(BookKnowledge.Validate(candidate with { KnowledgeCoverage = maps.Skip(1).ToArray() }, plan.Collections));
        Assert.NotNull(BookKnowledge.Validate(candidate with { KnowledgeCoverage = maps.Select(m => m with { ContextKeys = ["unknown"] }).ToArray() }, plan.Collections));
        k.Texts.Remove(selected[0].Reference);
        await Assert.ThrowsAsync<IOException>(() => CourseMaterials.SelectAsync(k, plan, node, default));
    }
    [Theory]
    [InlineData("pdf")]
    [InlineData("presentation")]
    public async Task ConsentDeniedSendsNothing_FailedBatchResumesFromPersistentCache(string sourceKind)
    {
        var k = new Knowledge(sourceKind: sourceKind); var provider = new Provider { FailAt = 2 };
        var settings = new SettingsService(new SqliteSettingsStore(root));
        var service = new PlanningService(new SqlitePlanningStore(root), k, settings, new(provider, new Caps()), new PlanParser(), knowledgeCache: new BookKnowledgeCache(root));
        var draft = (await service.StartAsync(new(Guid.NewGuid(), 0, new PlanningInput("学习全部", "基础", "掌握", 60, "Adaptive", [k.Snapshot.References[0]])))).Value!;
        var preview = (await service.PrepareAsync(draft.Id, draft.Revision)).Value!;
        Assert.False((await service.ProposeAsync(preview, false)).Ok); Assert.Equal(0, provider.Calls);
        Assert.False((await service.ProposeAsync(preview, true)).Ok); Assert.Equal(2, provider.Calls);
        Assert.NotNull(await new BookKnowledgeCache(root).ReadKnowledgeAsync(preview.KnowledgeBatches[0].Fingerprint, default));
        provider.FailAt = 3;
        var retry = (await service.PrepareAsync(draft.Id, draft.Revision)).Value!;
        Assert.False((await service.ProposeAsync(retry, true)).Ok); Assert.Equal(3, provider.Calls);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProposeAsync(retry, true, stop.Token));
        Assert.Equal(3, provider.Calls);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[{\"key\":\"objective-1\",\"supported\":false,\"sourceKeys\":[],\"blockIds\":[],\"explanation\":\"原文未说明\"}]")]
    public void ModelSupplementDoesNotRequireSourceOrContextChecklist(string checks)
    {
        var node = new PlannedNode("n1", "IP地址与子网", "theory", ["理解CIDR与最长前缀匹配"], [], 30, null);
        Assert.True(TheoryKnowledgePolicy.CanUseKnowledgeOnly(node));
        Assert.Null(TheoryKnowledgePolicy.MissingEvidence(node, []));
        var lesson = new TheoryLesson([new("b1", "paragraph", "讲解"), new("b2", "example", "例子"), new("b3", "question", null, "q1")],
            [new("q1", "问题", [new("a", "甲"), new("b", "乙"), new("c", "丙"), new("d", "丁")], "a", "解释")], [], 3);
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(lesson, Wire.Options))!;
        json["contextChecks"] = System.Text.Json.Nodes.JsonNode.Parse(checks);
        var parsed = new TheoryLessonParser().Parse(json.ToJsonString(), []);
        Assert.True(parsed.Ok, parsed.Error?.Message);
        Assert.Null(parsed.Value!.ContextChecks);
        Assert.Empty(parsed.Value.SourceKeys);
        Assert.Contains("未引用外部资料", TheoryKnowledgePolicy.Label(parsed.Value).Blocks[0].Text);
        Assert.False(LessonRules.Validate(lesson with { SourceKeys = ["invented"] }, []).Ok);
        Assert.False(LessonRules.Validate(lesson with { Questions = [] }, []).Ok);
    }
    [Theory]
    [InlineData(30, true)]
    [InlineData(45, false)]
    public async Task LargerLessonContextIsAcceptedWithoutSilentlyDroppingEvidence(int pages, bool fits)
    {
        var k = new Knowledge(pages, 5000);
        var batches = await BookKnowledge.PrepareAsync(k, [k.Collection], new(new Provider(), new Caps()), "deepseek-flash", default);
        var units = batches.Select(b => BookKnowledge.Parse(b, Reply(b.Review.Sources))).ToArray();
        var node = new PlannedNode("n1", "全部", "theory", ["全部"], [], 60, null);
        var candidate = new PlanCandidate("合成", "基础", "目标", 60, "Adaptive", [], [], [], [], [node], 2,
            KnowledgeCoverage: units.SelectMany(u => u.Points).Select(p => new KnowledgeCoverage(p.Key, "included", ["n1"], null)).ToArray());
        var plan = new PlanDocument(candidate, [], [], "hash", null, Collections: [k.Collection with { KnowledgeUnits = units }]);
        if (fits)
        {
            var selected = await CourseMaterials.SelectAsync(k, plan, node, default);
            Assert.Equal(pages, selected.Count);
            var materials = selected.Select(s => new ModelSource(s.Key, 0, ModelSourceKind.OriginalMaterial, k.Texts[s.Reference]));
            var review = await new ModelEgress(new Provider(), new Caps()).PrepareAdaptiveAsync("deepseek-flash", new[] { new ModelSource("theory-v3-context", 0, ModelSourceKind.UserText, "简明讲解") }.Concat(CourseMaterials.PackContext(materials.ToArray())), false);
            Assert.True(review.Budget!.InputUpperBound > 393216);
            return;
        }
        var error = await Assert.ThrowsAsync<SourcePreparationException>(() => CourseMaterials.SelectAsync(k, plan, node, default));
        Assert.Contains("未删减上下文", error.Message);
    }
    [Fact]
    public void MappingBatch_AcceptsBroadBindingsAndOptionalContextTypos_ButRequiresRealCourses()
    {
        var points = new[] { new BookKnowledgePoint("d1u1k1", "条件", "符号与条件", ["b1"]), new BookKnowledgePoint("d1u1k2", "例子", "推导", ["b2"]) };
        var nodes = Enumerable.Range(1, 4).Select(i => new PlannedNode("n" + i, "课" + i, "theory", ["目标"], [], 30, null)).ToArray();
        var plan = new PlanCandidate("课程", "基础", "目标", 60, "Adaptive", [], [], [], [], nodes, 2);
        var maps = points.Select(p => new KnowledgeCoverage(p.Key, "included", ["n1"], null, [])).ToArray();
        string Json(IEnumerable<KnowledgeCoverage> value) => System.Text.Json.JsonSerializer.Serialize(new { knowledgeCoverage = value }, Wire.Options);
        var keys = points.Select(p => p.Key).ToHashSet();
        Assert.Equal(2, BookKnowledgeMapping.Parse(Json(maps), points, plan, keys).Count);
        Assert.Throws<InvalidDataException>(() => BookKnowledgeMapping.Parse(Json(maps.Take(1)), points, plan, keys));
        Assert.All(BookKnowledgeMapping.Parse(Json(maps.Select(m => m with { NodeKeys = ["n1", "n2", "n3", "n4"] })), points, plan, keys), m => Assert.Equal(4, m.NodeKeys.Count));
        Assert.All(BookKnowledgeMapping.Parse(Json(maps.Select(m => m with { ContextKeys = ["unknown"] })), points, plan, keys), m => Assert.Empty(m.ContextKeys!));
        Assert.Throws<InvalidDataException>(() => BookKnowledgeMapping.Parse(Json(maps.Select(m => m with { NodeKeys = ["unknown"] })), points, plan, keys));
    }
    [Fact]
    public async Task MappingNormalizationAndSavedResponseResumeAvoidPaidRetry()
    {
        var point = new BookKnowledgePoint("d1u27k32", "机制", "定义与条件", ["b1"]);
        var k = new Knowledge(1);
        var collections = new[] { k.Collection with { KnowledgeUnits = [new("d1u27", "fp", [], [point], [])] } };
        var nodes = Enumerable.Range(1, 4).Select(i => new PlannedNode("n" + i, "课" + i, "theory", ["目标"], [], 30, null)).ToArray();
        var plan = new PlanCandidate("课程", "基础", "目标", 60, "Adaptive", [], [], [], [], nodes, 2);
        var raw = JsonSerializer.Serialize(new { knowledgeCoverage = new[] {
            new KnowledgeCoverage(" D1U27K32 ", "included", [" n1 ", "N2", "n2", "unknown"], null, ["bad", point.Key]),
            new KnowledgeCoverage(point.Key, "excluded", ["n3", "n4"], "冲突说明", []) } }, Wire.Options);
        var points = new[] { point }; var index = point.Key + " " + point.Title; var model = "deepseek-flash";
        var fingerprint = "mapping-v2-" + PlanRules.Hash(new { model, candidate = plan, points, index });
        var cache = new BookKnowledgeCache(root);
        await cache.SaveDerivedAsync("mapping-response:" + fingerprint, raw, default);
        var provider = new MappingProvider();
        var result = await BookKnowledgeMapping.CreateAsync(plan, collections, model, new(provider, new Caps()), cache, _ => { }, _ => { }, _ => Task.FromResult(true), default);
        Assert.True(result.Ok, result.Error?.Message); Assert.Equal(0, provider.Calls);
        var map = Assert.Single(result.Value!); Assert.Equal(4, map.NodeKeys.Count); Assert.Equal("included", map.State); Assert.Null(map.Reason); Assert.Empty(map.ContextKeys!);
        var messages = new List<string>();
        var repaired = plan with { WeeklyMinutes = 120, Coverage = [new("d1/page-1", "included", ["n1"], null)] };
        Assert.True((await BookKnowledgeMapping.CreateAsync(repaired, collections, model, new(provider, new Caps()), cache, messages.Add, _ => { }, _ => Task.FromResult(true), default)).Ok);
        Assert.Equal(0, provider.Calls);
        Assert.Contains(messages, m => m.Contains("已复用") && m.Contains("未调用模型"));
        Assert.Null(BookKnowledge.Validate(plan with { KnowledgeCoverage = result.Value }, collections));
    }
    [Fact]
    public async Task ExamAnalysis_UsesSeparatePromptAndCache_AndPreservesOriginalForLessons()
    {
        var k = new Knowledge(1);
        var textbook = k.Collection;
        var exam = textbook with { Purpose = "exam" };
        var egress = new ModelEgress(new Provider(), new Caps());
        var regular = Assert.Single(await BookKnowledge.PrepareAsync(k, [textbook], egress, "deepseek-flash", default));
        var batch = Assert.Single(await BookKnowledge.PrepareAsync(k, [exam], egress, "deepseek-flash", default));
        Assert.NotEqual(regular.Fingerprint, batch.Fingerprint);
        Assert.Equal(regular.Sources, batch.Sources);
        Assert.Contains("exam-reference-v1", batch.Review.Sources[0].Text);
        Assert.Contains("中英对照", batch.Review.Sources[0].Text);
        Assert.Contains("没有答案仍正常分析", batch.Review.Sources[0].Text);
        var originalRef = textbook.References[0];
        var markdownRef = originalRef with { Locator = originalRef.Locator with { Page = null, SourceLabel = "textbook.md" } };
        k.Texts[markdownRef] = "教材定义和解题方法";
        var markdown = textbook with { References = [markdownRef], SourceKind = "markdown" };
        var mixed = await BookKnowledge.PrepareAsync(k, [markdown, exam], egress, "deepseek-flash", default);
        Assert.Equal(2, mixed.Count);
        Assert.Contains(mixed[0].Review.Sources, s => s.Text.Contains("教材定义和解题方法"));
        var unit = BookKnowledge.Parse(batch, Reply(batch.Review.Sources.Skip(1)));
        var point = Assert.Single(unit.Points);
        var node = new PlannedNode("n1", "解题方法", "theory", ["计算"], [], 30, null);
        var candidate = new PlanCandidate("课程", "基础", "期末", 60, "Adaptive", [], [], [], [], [node], 2,
            KnowledgeCoverage: [new(point.Key, "included", ["n1"], null, [])]);
        var document = new PlanDocument(candidate, [], [], "hash", null, Collections: [exam with { KnowledgeUnits = [unit] }]);
        Assert.Contains("模型推导，非官方答案", ExamReference.LessonGuidance(document, node));
        Assert.Equal(batch.Sources.Select(s => s.Reference), BookKnowledge.LessonSources(document, node).Select(s => s.Reference));
        Assert.Contains("考题参照", CourseMaterials.WithQuality(document.Collections, batch.Sources[0], "原题条件"));
        Assert.DoesNotContain("考题参照", CourseMaterials.WithQuality([textbook], batch.Sources[0], "教材正文"));
        var old = JsonSerializer.Serialize(textbook, Wire.Options);
        Assert.DoesNotContain("purpose", old);
        Assert.Null(JsonSerializer.Deserialize<PlanCollection>(old, Wire.Options)!.Purpose);
    }
    [Fact]
    public void CompletedMapping_OnlyReusedForUnchangedCoursesOrUnmappedDeletion()
    {
        var n1 = new PlannedNode("n1", "实际知识课", "theory", ["讲清机制"], [], 30, null);
        var n64 = new PlannedNode("n64", "总复习", "theory", ["回顾"], [], 30, null);
        var plan = new PlanCandidate("课程", "基础", "目标", 60, "Adaptive", [], [], [], [], [n1, n64], 2);
        KnowledgeCoverage[] maps = [new("k1", "included", ["n1"], null, [])];
        Assert.True(BookKnowledgeMapping.CanReuse(plan, plan with { Coverage = [new("d1/page-1", "included", ["n1", "n64"], null)] }, maps));
        Assert.True(BookKnowledgeMapping.CanReuse(plan, plan with { Nodes = [n1] }, maps));
        Assert.True(BookKnowledgeMapping.CanReuse(plan with { Nodes = [n1 with { PrerequisiteKeys = ["n64"] }, n64] },
            plan with { Nodes = [n1 with { EstimatedMinutes = 45 }] }, maps));
        Assert.False(BookKnowledgeMapping.CanReuse(plan, plan with { Nodes = [n64] }, maps));
        Assert.False(BookKnowledgeMapping.CanReuse(plan, plan with { Nodes = [n1 with { Objectives = ["另一个机制"] }, n64] }, maps));
        Assert.False(BookKnowledgeMapping.CanReuse(plan, plan with { Nodes = [n1, n64, n1 with { Key = "n65" }] }, maps));
        Assert.False(BookKnowledgeMapping.CanReuse(plan, plan with { Goal = "另一个目标" }, maps));
    }
    [Fact]
    public async Task MappingFailure_ResumesCachedBatches_ChangedLayoutDoesNotReuseThem()
    {
        var k = new Knowledge(1);
        var points = Enumerable.Range(1, 81).Select(i => new BookKnowledgePoint("d1u1k" + i, "机制" + i, "定义与条件", ["b1"])).ToArray();
        var collection = k.Collection with { KnowledgeUnits = [new("d1u1", "fingerprint", [], points, [])] };
        var node = new PlannedNode("n1", "课", "theory", ["目标"], [], 30, null);
        var plan = new PlanCandidate("课程", "基础", "目标", 60, "Adaptive", [], [], [], [], [node], 2);
        var provider = new MappingProvider();
        var cache = new BookKnowledgeCache(root);
        var egress = new ModelEgress(provider, new Caps());
        var failed = await BookKnowledgeMapping.CreateAsync(plan, [collection], "deepseek-flash", egress, cache, _ => { }, _ => { }, _ => Task.FromResult(true), default);
        Assert.False(failed.Ok); Assert.Equal(2, provider.Calls);
        var resumed = await BookKnowledgeMapping.CreateAsync(plan, [collection], "deepseek-flash", egress, new BookKnowledgeCache(root), _ => { }, _ => { }, _ => Task.FromResult(true), default);
        Assert.True(resumed.Ok); Assert.Equal(81, resumed.Value!.Count); Assert.Equal(4, provider.Calls);
        var repaired = plan with { Nodes = [node with { EstimatedMinutes = 45 }] };
        var restored = await BookKnowledgeMapping.CreateAsync(repaired, [collection], "deepseek-flash", egress, new BookKnowledgeCache(root), _ => { }, _ => { }, _ => Task.FromResult(true), default);
        Assert.True(restored.Ok); Assert.Equal(81, restored.Value!.Count); Assert.Equal(4, provider.Calls);
        Assert.True((await BookKnowledgeMapping.CreateAsync(plan with { Goal = "新目标" }, [collection], "deepseek-flash", egress, cache, _ => { }, _ => { }, _ => Task.FromResult(true), default)).Ok);
        Assert.Equal(7, provider.Calls);
    }
    private sealed class MappingProvider : IModelProvider
    {
        public int Calls;
        public string Destination => "local:deterministic-fake";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            Assert.Contains("JSON", request.Sources[0].Text);
            if (Calls == 2) return Task.FromResult(Result<ProviderReply>.Fail(ErrorCode.TRANSIENT_PROVIDER_ERROR, "one batch failed"));
            var points = System.Text.Json.JsonSerializer.Deserialize<BookKnowledgePoint[]>(request.Sources.Single(s => s.Id == "points").Text, Wire.Options)!;
            var text = System.Text.Json.JsonSerializer.Serialize(new { knowledgeCoverage = points.Select(p => new KnowledgeCoverage(p.Key, "included", ["n1"], null, [])) }, Wire.Options);
            return Task.FromResult(Result<ProviderReply>.Success(new(text, true)));
        }
    }
    [Fact]
    public void ManySmallContextChunks_KeepEveryOriginalCitationAndBody()
    {
        var sources = Enumerable.Range(1, 143).Select(i => new ModelSource("c" + i, 0, ModelSourceKind.OriginalMaterial,
            $"[资料id=c{i}]\n完整公式与条件{i}；页尾续文{i}。")).ToArray();
        var packed = CourseMaterials.PackContext(sources);
        Assert.True(packed.Count < 99);
        var text = string.Join('\n', packed.Select(s => s.Text));
        Assert.All(sources, source => Assert.Contains(source.Text, text));
    }
    [Fact]
    public async Task MappedKnowledgeRepairsMissingPageBindingWithoutInventingPages()
    {
        var k = new Knowledge(2);
        var batch = Assert.Single(await BookKnowledge.PrepareAsync(k, [k.Collection], new(new Provider(), new Caps()), "deepseek-flash", default));
        var unit = BookKnowledge.Parse(batch, Reply(batch.Review.Sources));
        var node = new PlannedNode("n54", "综合回顾", "theory", ["跨层机制"], [], 60, null);
        var plan = new PlanCandidate("课程", "基础", "目标", 60, "Adaptive", [], [], [], [], [node], 2,
            Coverage: [new("d1/page-1", "excluded", [], "旧页面标签"), new("d1/page-2", "unavailable", [], "未取得")],
            KnowledgeCoverage: unit.Points.Select(p => new KnowledgeCoverage(p.Key, "included", ["n54"], null)).ToArray());
        var collections = new[] { k.Collection with { KnowledgeUnits = [unit] } };
        var repaired = BookKnowledge.BindMappedPages(plan, collections);
        Assert.Equal(2, repaired.Coverage!.Count);
        Assert.Equal("included", repaired.Coverage[0].State); Assert.Equal(new[] { "n54" }, repaired.Coverage[0].NodeKeys);
        Assert.Equal("unavailable", repaired.Coverage[1].State);
        Assert.Null(CourseMaterials.MissingBindingError(new(repaired, [], [], "hash", null, Collections: collections)));
        Assert.Empty(plan.Coverage![0].NodeKeys); // Frozen input was not mutated.
    }
    [Fact]
    public async Task BundledContext_PublishesUsingOriginalSourceIds()
    {
        var k = new Knowledge(105, 5);
        var batches = await BookKnowledge.PrepareAsync(k, [k.Collection], new(new Provider(), new Caps()), "deepseek-flash", default);
        var units = batches.Select(b => new BookKnowledgeUnit(b.Key, b.Fingerprint, b.Sources,
            [new(b.Key + "k1", "标志位定义", "区分零标志与溢出标志", b.Sources.Select(s => s.Key).ToArray())], [])).ToArray();
        var plans = new SqlitePlanningStore(root); var settings = new SettingsService(new SqliteSettingsStore(root));
        var input = new PlanningInput("学习标志", "基础", "理解", 60, "Adaptive", [k.Snapshot.References[0]]);
        var draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
        var node = new PlannedNode("n1", "标志", "theory", ["区分零与溢出"], [], 30, null);
        var candidate = new PlanCandidate("标志", "基础", "理解", 60, "Adaptive", [], [], [], ["s1"], [node], 2,
            Strategy: new("计算机组成", ["theory"], "先定义再例子", [], [], []),
            Coverage: k.Collection.Manifest.Entries.Select(e => new PlanCoverage("d1/" + e.Key, "included", ["n1"], null)).ToArray(),
            KnowledgeCoverage: units.SelectMany(u => u.Points).Select(p => new KnowledgeCoverage(p.Key, "included", ["n1"], null)).ToArray());
        var validated = PlanRules.Validate(draft.Id, input, candidate, [new("s1", k.Snapshot.References[0], true)], null, [k.Collection with { KnowledgeUnits = units }]);
        Assert.True(validated.Ok, validated.Error?.Message);
        var proposed = (await plans.SaveProposalAsync(new(Guid.NewGuid(), draft.Revision, validated.Value!), draft.Id)).Value!;
        Assert.True((await plans.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, proposed.Plan!.Hash, true)))).Ok);
        var key = "c" + k.Snapshot.References[0].VersionId.ToString("N");
        var lesson = new TheoryLesson([new("p", "paragraph", "零标志与溢出标志不同。"), new("e", "example", "7+1超出四位补码的上界7，发生溢出。"), new("q", "question", null, "q1")],
            [new("q1", "四位补码7+1会怎样？", [new("a", "溢出"), new("b", "结果0"), new("c", "结果7"), new("d", "结果1")], "a", "超出四位补码范围。")], [key], 3,
            units.SelectMany(u => u.Points).Select(p => p.Key).Append("objective-1").Select(id => new LessonEvidenceCheck(id, true, [key], ["p", "e"], "原文定义与例子")).ToArray());
        var store = new SqliteLearningStore(root, plans);
        var generation = new GenerationService(plans, store, store, k, settings, new(new BundledProvider(lesson), new Caps()), new TheoryLessonParser());
        Assert.True((await generation.GenerateAsync(draft.Id, "n1")).Ok);
        while (await generation.TickAsync()) { }
        Assert.True((await store.GetLessonAsync(draft.Id, "n1")).Ok);
    }
    private sealed class BundledProvider(TheoryLesson lesson) : IModelProvider
    {
        public string Destination => "local:deterministic-fake";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Assert.Contains(request.Sources, s => s.Id.StartsWith("context-bundle-", StringComparison.Ordinal));
            Assert.True(request.Sources.Count < 100);
            return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(lesson, Wire.Options), true)));
        }
    }
    [Fact]
    public void ContextOutputReserve_RespectsVerifiedModelAndLeavesLegacyBudgetsUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var caps = new ModelCapabilities("m", 1048576, 131072, true, now, true);
        Assert.Equal(65536, AdaptiveBudgetPolicy.Create(caps, "m", "{}", false, now, true, true).OutputReserve);
        Assert.Equal(32768, AdaptiveBudgetPolicy.Create(caps, "m", "{}", false, now, true).OutputReserve);
        Assert.Equal(32768, AdaptiveBudgetPolicy.Create(caps, "m", "{}", true, now).OutputReserve);
        Assert.Equal(16384, AdaptiveBudgetPolicy.Create(caps with { MaxOutputTokens = 16384 }, "m", "{}", false, now, true, true).OutputReserve);
    }
    [Fact]
    public void LegacyDocuments_DoNotAcquireNewSerializedFields()
    {
        var plan = new PlanCandidate("旧计划", "基础", "目标", 60, "Math", [], [], [], [], [], 1);
        Assert.DoesNotContain("knowledgeCoverage", JsonSerializer.Serialize(plan, Wire.Options));
        Assert.DoesNotContain("contextChecks", JsonSerializer.Serialize(new TheoryLesson([], [], []), Wire.Options));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
