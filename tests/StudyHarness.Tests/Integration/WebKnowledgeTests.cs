using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class WebKnowledgeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-web-knowledge", Guid.NewGuid().ToString("N"));
    private sealed class Caps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(model, 1048576, 32768, true, DateTimeOffset.UtcNow, true)); }
    private sealed class Provider : IModelProvider
    {
        public int Inventories, Lessons; public int FailAt; public readonly List<ApprovedModelRequest> Requests = [];
        public string Destination => "local:web-knowledge-fixture";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Sources[0].Id == "web-knowledge")
            {
                Inventories++;
                if (Inventories == FailAt) return Task.FromResult(Result<ProviderReply>.Fail(ErrorCode.TRANSIENT_PROVIDER_ERROR, "synthetic failure"));
                var body = request.Sources.Skip(1).Where(s => !s.Text.Contains("PROMO_ONLY")).ToArray();
                return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(new
                {
                    points = body.Select(s => new { title = "集合定义", detail = "集合是确定对象的全体，重复元素不改变集合。", evidenceIds = new[] { s.Id } }),
                    nonTeachingIds = request.Sources.Skip(1).Except(body).Select(s => s.Id).ToArray()
                }), false, Usage: new(10, 10, 20))));
            }
            Lessons++;
            var ids = request.Sources.Skip(1).Select(s => s.Id).ToArray();
            return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(TheoryFixtureProvider.NewLesson(ids[0]), Wire.Options), false)));
        }
    }
    private async Task<(KnowledgeStore Store, Guid Library)> Setup(IWebSources? web = null)
    {
        Directory.CreateDirectory(Path.Combine(root, "library"));
        var store = new KnowledgeStore(root, web);
        var selected = await store.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(Path.Combine(root, "library"))));
        Assert.True(selected.Ok); return (store, selected.Value!.Id);
    }
    [Fact]
    public void Cleaner_RemovesFurnitureButPreservesCookieExplanationCodeMathAndReferences()
    {
        var page = new WebPage("https://example.org/cookie", "Cookie协议", "# Cookie协议\nCookie是用于保存会话状态的机制。\n[规范](https://example.org/spec)\n```text\n广告: this is a code example\n```\n$$x=2$$\n## 推荐阅读\n立即购买优惠课程\n## Cookie例子\n例如会话标识可以保存在Cookie中。\n广告：领取优惠券\n", DateTimeOffset.UnixEpoch);
        var cleaned = TeachingPages.Clean(page, "Cookie");
        Assert.DoesNotContain("优惠", cleaned.Content); Assert.Contains("会话标识", cleaned.Content);
        Assert.Contains("广告: this is a code example", cleaned.Content); Assert.Contains("[规范]", cleaned.Content); Assert.Contains("$$x=2$$", cleaned.Content);
        Assert.Equal(page.Content, cleaned.OriginalContent);
        Assert.Equal(cleaned.Content, TeachingPages.Clean(cleaned, "Cookie").Content);
    }
    [Theory]
    [InlineData("web")]
    [InlineData("pdf")]
    public void Planning_AllowsUnmappedIntroductionAndNoExtractedPoints(string kind)
    {
        var reference = new SourceRef(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "hash", new("source", 1, "正文", "page-1:0-10"));
        var unit = new BookKnowledgeUnit("web-d1u1", "cache", [new("b1", reference, false)], [], ["b1"]);
        var collection = new PlanCollection(reference.LibraryId,
            new(Guid.NewGuid(), "v1", "directory", true, [new("page-1", "1", "正文", "textReady", "hash", null, [])]),
            [reference], KnowledgeUnits: [unit], SourceKind: kind);
        var plan = new PlanCandidate("课程", "基础", "目标", 60, "Adaptive", [], [], [], [],
            [new("n1", "导论", "theory", ["理解学科目标"], [], 15, null), new("n2", "综合复习", "theory", ["联系课程知识"], [], 15, null)], 2,
            Coverage: [new("d1/page-1", "included", ["n1", "n2"], null)], KnowledgeCoverage: []);
        Assert.Null(BookKnowledge.Validate(plan, [collection]));
        var bound = BookKnowledge.BindMappedPages(plan, [collection]);
        Assert.Equal(plan.Nodes, bound.Nodes); Assert.Equal(plan.Coverage, bound.Coverage);
        Assert.NotNull(BookKnowledge.Validate(plan with { KnowledgeCoverage = [new("invented", "included", ["n1"], null)] }, [collection]));
    }
    [Fact]
    public async Task SavedWebProjectsIntoInventory_UnknownBlocksAreNotInventedOrMarkedComplete()
    {
        var (store, library) = await Setup();
        var refs = await store.SaveWebAsync(library, [new("https://example.org/set", "集合教程", "# 集合\n集合是确定对象的全体。\n" + string.Concat(Enumerable.Repeat("# 小节\n集合定义与例子。\n", 30)), DateTimeOffset.UnixEpoch)], "集合", default);
        var snapshot = Assert.Single(await store.CollectionsAsync(library)); Assert.Equal("web", snapshot.Collection.SourceKind);
        var provider = new Provider(); var egress = new ModelEgress(provider, new Caps());
        var batches = await BookKnowledge.PrepareAsync(store, [new(library, snapshot.Manifest, snapshot.References, SourceKind: "web")], egress, "fixture", default);
        Assert.NotEmpty(batches); Assert.Empty(provider.Requests);
        var batch = batches[0]; Assert.True(batch.Sources.Count > 1);
        var unit = BookKnowledge.Parse(batch, JsonSerializer.Serialize(new { points = new[] { new { title = "集合", detail = "集合定义", evidenceIds = new[] { batch.Sources[0].Key } } }, nonTeachingIds = Array.Empty<string>() }));
        Assert.Single(unit.Points); Assert.DoesNotContain(unit.Points, p => p.Title.Contains("原文待展开"));
        await new BookKnowledgeCache(root).SaveKnowledgeAsync(unit, default);
        var item = Assert.Single((await new CacheCatalog(root).ReadAsync(1)).Items);
        Assert.Equal("集合教程 · example.org", item.Name); Assert.False(item.KnowledgeReady);
        Assert.Equal(refs, await store.WebPageReferencesAsync(refs[0], default));
    }
    [Fact]
    public async Task FailedLaterBatch_RestartReusesCompletedBatchesAndChangedBodyInvalidates()
    {
        var (store, library) = await Setup();
        var refs = await store.SaveWebAsync(library, [new("https://example.org/set", "集合教程", "集合定义。" + string.Concat(Enumerable.Range(0, 8).Select(i => "\n# 小节" + i + "\n" + new string('甲', 5500) + "\n")), DateTimeOffset.UnixEpoch)], "集合", default);
        var provider = new Provider { FailAt = 2 }; var egress = new ModelEgress(provider, new Caps());
        var batches = await WebKnowledge.PrepareAsync(store, refs, egress, "fixture", 0, default);
        Assert.True(batches.Count >= 3);
        await Assert.ThrowsAsync<IOException>(() => WebKnowledge.CompleteAsync(batches, new BookKnowledgeCache(root), egress, "fixture", null, default));
        Assert.NotNull(await new BookKnowledgeCache(root).ReadKnowledgeAsync(batches[0].Fingerprint, default));
        var resumed = new Provider(); var resumedEgress = new ModelEgress(resumed, new Caps());
        var again = await WebKnowledge.PrepareAsync(new KnowledgeStore(root), refs, resumedEgress, "fixture", 0, default);
        await WebKnowledge.CompleteAsync(again, new BookKnowledgeCache(root), resumedEgress, "fixture", null, default);
        Assert.Equal(batches.Count - 1, resumed.Inventories);
        Assert.True(Assert.Single((await new CacheCatalog(root).ReadAsync(1)).Items).KnowledgeReady);
        var updated = await store.SaveWebAsync(library, [new("https://example.org/set", "集合教程", "新的集合定义。", DateTimeOffset.UnixEpoch.AddDays(1))], "集合", default);
        Assert.False(Assert.Single((await new CacheCatalog(root).ReadAsync(1)).Items).KnowledgeReady);
        Assert.NotEqual(batches[0].Fingerprint, (await WebKnowledge.PrepareAsync(store, updated, resumedEgress, "fixture", 0, default))[0].Fingerprint);
        Assert.NotNull((await store.ResolveAsync(refs[0])).Text);
    }
    [Fact]
    public async Task Lesson_RequiresAuthorization_UsesInventoryExcludesPromoAndReusesCache()
    {
        var (store, library) = await Setup();
        var refs = await store.SaveWebAsync(library, [new("https://example.org/set", "集合教程", "# 推广模块\n" + string.Concat(Enumerable.Repeat("PROMO_ONLY购买。", 500)) + "\n# 集合\n" + string.Concat(Enumerable.Repeat("集合是确定对象的全体。", 500)), DateTimeOffset.UnixEpoch)], "集合", default);
        var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans); var settings = new SettingsService(new SqliteSettingsStore(root));
        var input = new PlanningInput("学习集合", "零基础", "理解集合", 120, "Math", refs);
        var draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
        var sources = refs.Select((r, i) => new PlanSource("s" + (i + 1), r, true)).ToArray();
        var candidate = new PlanCandidate("集合", "零基础", "理解集合", 120, "Math", [], [], [], sources.Select(s => s.Key).ToArray(), [new("A", "集合", "theory", ["理解集合"], [], 20, null)]);
        var validated = PlanRules.Validate(draft.Id, input, candidate, sources, null); Assert.True(validated.Ok, validated.Error?.Message);
        var proposed = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, validated.Value!), draft.Id)).Value!;
        Assert.True((await plans.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, validated.Value!.Hash, true)))).Ok);
        var provider = new Provider();
        GenerationService Create() => new(plans, learning, learning, store, settings, new(provider, new Caps()), new TheoryLessonParser(), knowledgeCache: new BookKnowledgeCache(root));
        var service = Create(); var preview = await service.PrepareAsync(draft.Id, "A");
        Assert.True(preview.Ok, preview.Error?.Message); Assert.Contains("网页知识整理", preview.Value!.Preview); Assert.Empty(provider.Requests);
        Assert.True((await service.GenerateAsync(draft.Id, "A")).Ok); Assert.True(await service.TickAsync());
        var task = (await learning.TasksAsync()).Last(); Assert.True(task.Status == GenerationStatus.Ready, $"{task.Status}: {task.Failure}; inventories={provider.Inventories}, lessons={provider.Lessons}");
        var final = provider.Requests.Last(); Assert.DoesNotContain("PROMO_ONLY", string.Join('\n', final.Sources.Select(s => s.Text)));
        var inventories = provider.Inventories; Assert.True(inventories > 0);
        var restarted = Create(); Assert.True((await restarted.GenerateAsync(draft.Id, "A", true)).Ok); await restarted.TickAsync();
        Assert.Equal(inventories, provider.Inventories); Assert.Equal(2, provider.Lessons);
        Assert.Equal(validated.Value.Hash, (await plans.GetAsync(draft.Id))!.Plan!.Hash);
    }
    private sealed class Web : IWebSources
    {
        public int Calls;
        public Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct) => throw new NotSupportedException();
        public Task<WebPage> FetchAsync(string url, CancellationToken ct)
        { Calls++; return Task.FromResult(new WebPage(url, "集合与关系", "# 集合与关系\n集合是确定对象的全体。关系表示集合中元素之间的对应。\n广告：立即购买\n", DateTimeOffset.UnixEpoch)); }
    }
    [Fact]
    public async Task OnDemandSite_CleansAndListsActualPages_NotWholeDirectory()
    {
        var web = new Web(); var (store, library) = await Setup(web);
        var snapshot = await store.SaveSiteAsync(library, new("site-test", DateTimeOffset.UnixEpoch,
            [new("集合定义", "https://oi.wiki/math/set/"), new("关系", "https://oi.wiki/math/relation/")], 2, 2), [], default);
        var collection = new PlanCollection(library, snapshot.Manifest, snapshot.References, SourceKind: "site");
        var fragments = await store.RetrieveFrozenAsync(collection, "集合", ["url-1"], 10000);
        Assert.Single(fragments); Assert.DoesNotContain("立即购买", fragments[0].Text);
        var refs = await store.WebPageReferencesAsync(fragments[0].Reference, default); Assert.Single(refs);
        var catalog = await new CacheCatalog(root).ReadAsync(1);
        Assert.Single(catalog.Items); Assert.Contains("集合与关系", catalog.Items[0].Name); Assert.Contains("目录2项，已缓存正文1篇，已整理0篇", catalog.Warning);
        var provider = new Provider(); var egress = new ModelEgress(provider, new Caps());
        await WebKnowledge.CompleteAsync(await WebKnowledge.PrepareAsync(store, refs, egress, "fixture", 0, default), new BookKnowledgeCache(root), egress, "fixture", null, default);
        Assert.True(Assert.Single((await new CacheCatalog(root).ReadAsync(1)).Items).KnowledgeReady);
        await store.RetrieveFrozenAsync(collection, "集合", ["url-1"], 10000); Assert.Equal(1, web.Calls);
        var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans);
        collection = collection with { References = refs };
        var input = new PlanningInput("学习集合与关系", "零基础", "理解集合", 120, "Adaptive", refs);
        var draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
        var candidate = new PlanCandidate("集合与关系", "零基础", "理解集合", 120, "Adaptive", [], [], [], ["s1"],
            [new("A", "集合与关系", "theory", ["理解集合和关系"], [], 30, null)], 2,
            Strategy: new("数学", ["theory"], "按站点章节学习", [], [], []),
            Coverage: [new("d1/url-1", "excluded", [], "本课学习第二章"), new("d1/url-2", "included", ["A"], null)]);
        var validation = PlanRules.Validate(draft.Id, input, candidate, [new("s1", refs[0], true)], null, collections: [collection]);
        Assert.True(validation.Ok, validation.Error?.Message);
        var proposed = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, validation.Value!), draft.Id)).Value!;
        Assert.True((await plans.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, validation.Value!.Hash, true)))).Ok);
        var generation = new GenerationService(plans, learning, learning, store, new(new SqliteSettingsStore(root)), egress, new TheoryLessonParser(), knowledgeCache: new BookKnowledgeCache(root));
        var before = provider.Inventories;
        Assert.True((await generation.GenerateAsync(draft.Id, "A")).Ok);
        Assert.Equal(before, provider.Inventories); Assert.Equal(2, web.Calls);
        await generation.TickAsync();
        Assert.Equal(before + 1, provider.Inventories);
        Assert.True((await learning.GetLessonAsync(draft.Id, "A")).Ok);
        Assert.Contains("已缓存正文2篇，已整理2篇", (await new CacheCatalog(root).ReadAsync(1)).Warning);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
