using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Desktop;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class AdaptivePlanTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-adaptive-plan", Guid.NewGuid().ToString("N"));
    private static readonly Guid Subject = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly PlanSource[] Sources = [new("s1", new(Subject, Subject, Subject, new string('A', 64), new("source.md", null, "集合", "1")), false)];
    private static PlanningInput Input => new("学习集合证明", "零基础", "理解集合", 120, "Adaptive", []);
    private static PlanCandidate Candidate => new("集合", "零基础", "理解集合", 120, "Adaptive", [], [], [], ["s1"],
        [new("n1", "定义", "theory", ["解释集合"], [], 60, null),
         new("n2", "证明", "lab", ["证明集合相等"], ["n1"], 60, new("independent", null, "paper-exercise", ["s1"], "推导训练", "exercise"))],
        2, Strategy: new("集合", ["theory", "exercise"], "先理解再证明", ["paper-exercise"], [], []), Coverage: []);

    [Fact]
    public void OcrQuality_TravelsInReviewedTextOnlyForBoundOcrReference()
    {
        var source = Sources[0] with { Reference = Sources[0].Reference with { Locator = new("scan.pdf", 1, "page", "page-1:0-10") } };
        var collection = new PlanCollection(Subject, new(Subject, "v1", new string('B', 64), true,
            [new("page-1", "1", "page", "ocrReady", new string('A', 64), null, ["unverified"])]), [source.Reference]);
        var text = CourseMaterials.WithQuality([collection], source, "原文");
        Assert.Contains("[宿主来源类型：OCR]", text); Assert.EndsWith("原文", text);
        Assert.Contains("正常用于讲解、例子和测验", text);
        Assert.Contains("同页其他清楚内容仍可使用", text);
        Assert.Contains("不得猜补", text);
        Assert.Equal("原文", CourseMaterials.WithQuality([collection], source with { Reference = source.Reference with { VersionId = Guid.NewGuid() } }, "原文"));
        Assert.Equal("原文", CourseMaterials.WithQuality(null, source, "原文"));
        var mixed = "表格A=12，图中A→B。公式$x^2$。另一处[无法辨认]，图2[图示需人工复核]。";
        Assert.EndsWith(mixed, CourseMaterials.WithQuality([collection], source, mixed));
    }

    [Fact]
    public void WeeklyPace_ChangesWeeksButKeepsAllEightChaptersAndLessons()
    {
        var nodes = Enumerable.Range(1, 64).Select(i => new PlannedNode("n" + i,
            $"第{(i - 1) / 8 + 1}章知识点{i}", "theory", ["定义并逐步计算实例"], [], 60, null)).ToArray();
        var candidate = Candidate with { Nodes = nodes, Strategy = Candidate.Strategy! with { TeachingModes = ["theory"], MaterialCapabilities = [] } };
        var slow = PlanRules.Validate(Subject, Input with { WeeklyMinutes = 60 }, candidate with { WeeklyMinutes = 60 }, Sources, null);
        var fast = PlanRules.Validate(Subject, Input with { WeeklyMinutes = 600 }, candidate with { WeeklyMinutes = 600 }, Sources, null);
        Assert.True(slow.Ok); Assert.True(fast.Ok);
        Assert.Equal(64, slow.Value!.Weeks.Count); Assert.Equal(7, fast.Value!.Weeks.Count);
        Assert.Equal(nodes.Select(n => n.Key), slow.Value.Weeks.SelectMany(w => w.NodeKeys));
        Assert.Equal(nodes.Select(n => n.Key), fast.Value.Weeks.SelectMany(w => w.NodeKeys));
        Assert.Equal(3840, fast.Value.Weeks.Sum(w => w.EstimatedMinutes));
        Assert.Contains("不限制总课数", PlanTeachingCoverage.Describe(fast.Value));
    }

    [Fact]
    public void PlanningGuidance_PreservesTailDepthAndDistinguishesSamplesFromMissingSources()
    {
        Assert.Contains(AdaptivePlanningPrompt.DepthGuidance, AdaptivePlanningPrompt.Text);
        Assert.Contains("不预设40课", AdaptivePlanningPrompt.Text);
        Assert.Contains("从最后一章向前逐章审查", AdaptivePlanningPrompt.Text);
        Assert.Contains("不等于本地未取得整章正文", AdaptivePlanningPrompt.Text);
        Assert.Contains("不套用固定课数", PlanningPrompt.Text);
        Assert.DoesNotContain("通常4至12课", PlanningPrompt.Text);
    }

    [Fact]
    public void CompactCoverage_ExpandsWithoutChangingMeaning_RejectsUnboundedRanges()
    {
        var parser = new PlanParser();
        var candidate = Candidate with { Coverage = [new("d1/page-1..505", "unavailable", [], "尚未识别")] };
        var repeatedPrefix = new PlanParser().Parse(JsonSerializer.Serialize(candidate with { Coverage = [new("d1/page-1..d1/page-505", "unavailable", [], "尚未识别")] }, Wire.Options));
        Assert.True(repeatedPrefix.Ok);
        Assert.Equal(505, repeatedPrefix.Value!.Coverage!.Count);
        var parsed = parser.Parse(JsonSerializer.Serialize(candidate, Wire.Options));
        Assert.True(parsed.Ok, parsed.Error?.Message);
        Assert.Equal(505, parsed.Value!.Coverage!.Count);
        Assert.Equal("d1/page-505", parsed.Value.Coverage[^1].EntryKey);
        Assert.All(parsed.Value.Coverage, e => { Assert.Equal("unavailable", e.State); Assert.Empty(e.NodeKeys); Assert.Equal("尚未识别", e.Reason); });
        foreach (var key in new[] { "d1/page-2..1", "d1/page-1..10001" })
            Assert.False(parser.Parse(JsonSerializer.Serialize(candidate with { Coverage = [new(key, "unavailable", [], "缺口")] }, Wire.Options)).Ok);
        var duplicate = candidate with { Coverage = [new("d1/page-1..2", "unavailable", [], "缺口"), new("d1/page-2", "unavailable", [], "缺口")] };
        Assert.True(AdaptivePlanRules.Valid(parser.Parse(JsonSerializer.Serialize(duplicate, Wire.Options)).Value!));
        Assert.False(AdaptivePlanRules.Valid(parser.ParseRaw(JsonSerializer.Serialize(duplicate, Wire.Options)).Value!));
        var many = Candidate with { SourceKeys = Enumerable.Range(1, 13).Select(i => "s" + i).ToArray() };
        Assert.True(parser.Parse(JsonSerializer.Serialize(many, Wire.Options)).Ok);
        Assert.False(PlanRules.Validate(Subject, Input, many, Sources, null).Ok); // absent source IDs still rejected
    }

    [Fact]
    public void OverlappingCoverage_PreservesAllDeclaredBindings_RejectsConflictsAndUnknownNodes()
    {
        var parser = new PlanParser();
        var candidate = Candidate with { Coverage = [new("d1/page-1..384", "included", ["n1"], null), new("d1/page-350..384", "included", ["n2"], "综合复习")] };
        var parsed = parser.Parse(JsonSerializer.Serialize(candidate, Wire.Options)).Value!;
        Assert.Equal(384, parsed.Coverage!.Count);
        Assert.Equal(["n1", "n2"], parsed.Coverage[^1].NodeKeys);
        Assert.Equal(["n1"], parsed.Coverage[0].NodeKeys);
        Assert.Equal(JsonSerializer.Serialize(candidate.Nodes, Wire.Options), JsonSerializer.Serialize(parsed.Nodes, Wire.Options));
        Assert.True(AdaptivePlanRules.Valid(parsed));
        var conflict = candidate with { Coverage = [candidate.Coverage![0], new("d1/page-384", "excluded", [], "不学习")] };
        Assert.Contains(AdaptivePlanRules.Problems(parser.Parse(JsonSerializer.Serialize(conflict, Wire.Options)).Value!), e => e.Contains("PCOV003") && e.Contains("d1/page-384"));
        var unknown = candidate with { Coverage = [candidate.Coverage![0], new("d1/page-384", "included", ["n99"], null)] };
        Assert.Contains(AdaptivePlanRules.Problems(parser.Parse(JsonSerializer.Serialize(unknown, Wire.Options)).Value!), e => e.Contains("PCOV004"));
        var difference = AdaptivePlanRules.DirectoryDifference(parsed with { Coverage = parsed.Coverage.Skip(1).Append(new("d1/page-999", "excluded", [], "多余")).ToArray() }, Enumerable.Range(1,384).Select(i => "d1/page-"+i));
        Assert.Contains("缺失1项：d1/page-1", difference); Assert.Contains("不存在1项：d1/page-999", difference);
    }
    [Fact]
    public void SchemaAndRules_AgreeAndRejectUnsupportedOrInconsistentStrategies()
    {
        var parser = new PlanParser(); var p = Candidate;
        Assert.True(parser.Parse(JsonSerializer.Serialize(p, Wire.Options)).Ok);
        Assert.True(PlanRules.Validate(Subject, Input, p, Sources, null).Ok);
        Assert.False(PlanRules.Validate(Subject, Input, p with { Strategy = p.Strategy! with { MaterialCapabilities = ["shell"] } }, Sources, null).Ok);
        Assert.False(PlanRules.Validate(Subject, Input, p with { Nodes = [p.Nodes[0]] }, Sources, null).Ok);
        Assert.False(PlanRules.Validate(Subject, Input, p with { Coverage = [new("chapter", "included", ["n99"], null)] }, Sources, null).Ok);
        Assert.False(PlanRules.Validate(Subject, Input, p with { Coverage = [new("chapter", "excluded", [], null)] }, Sources, null).Ok);
        var covered = p with { Coverage = [new("chapter", "included", ["n1", "n2"], null)] };
        Assert.True(AdaptivePlanRules.CoversDirectory(covered, ["chapter"]));
        Assert.False(AdaptivePlanRules.CoversDirectory(covered, ["chapter", "missing"]));
        var json = JsonNode.Parse(JsonSerializer.Serialize(p, Wire.Options))!; json["scope"] = "Math";
        Assert.False(parser.Parse(json.ToJsonString()).Ok);
        json = JsonNode.Parse(JsonSerializer.Serialize(p, Wire.Options))!; json["algorithms"] = new JsonArray();
        Assert.True(parser.Parse(json.ToJsonString()).Ok);
    }

    [Fact]
    public void FrozenDirectory_MustAccountForEveryEntryAndCannotClaimMissingBody()
    {
        var collection = new PlanCollection(Subject, new(Subject, "v1", new string('B', 64), true,
            [new("page-1", "1", "集合", "textReady", new string('A', 64), null, []), new("page-2", "2", "证明", "discovered", null, null, ["ocrRequired"])]), Sources.Select(s => s.Reference).ToArray());
        var candidate = Candidate with { Coverage = [new("d1/page-1", "included", ["n1", "n2"], null), new("d1/page-2", "unavailable", [], "待识别")] };
        var result = PlanRules.Validate(Subject, Input, candidate, Sources, null, [collection]); Assert.True(result.Ok, result.Error?.Message);
        Assert.True(PlanRules.Validate(Subject, Input, candidate with { Coverage = candidate.Coverage!.Select(e => e.State == "included" ? e with { Reason = "本节支持这些课程" } : e).ToArray() }, Sources, null, [collection]).Ok);
        Assert.NotNull(result.Value!.Collections);
        var teaching = PlanTeachingCoverage.Describe(result.Value);
        Assert.Contains("资料页1–1（1页）：理论1课、实践1课", teaching);
        Assert.DoesNotContain("资料页1–2", teaching); // Unavailable pages cannot masquerade as taught pages.
        Assert.False(PlanRules.Validate(Subject, Input, Candidate, Sources, null, [collection]).Ok);
        Assert.False(PlanRules.Validate(Subject, Input, candidate with { Coverage = candidate.Coverage!.Select(e => e with { State = "included", NodeKeys = ["n1"], Reason = null }).ToArray() }, Sources, null, [collection]).Ok);
        var changed = collection with { Manifest = collection.Manifest with { Version = "v2" } };
        Assert.NotEqual(result.Value.Hash, PlanRules.Validate(Subject, Input, candidate, Sources, null, [changed]).Value!.Hash);
    }
    [Fact]
    public void CppCapability_CannotBypassAlgorithmCoverageWithGenericPractice()
    {
        var p = Candidate with { Strategy = Candidate.Strategy! with { MaterialCapabilities = ["portable-cpp"] },
            Nodes = [Candidate.Nodes[0], Candidate.Nodes[1] with { Lab = Candidate.Nodes[1].Lab! with { Toolchain = "portable-cpp" } }] };
        Assert.False(PlanRules.Validate(Subject, Input, p, Sources, null).Ok);
        p = p with { Algorithms = [new("a1", "二分")], Nodes = p.Nodes.Select(n => n with { AlgorithmKeys = ["a1"] }).ToArray() };
        Assert.True(PlanRules.Validate(Subject, Input, p, Sources, null).Ok);
    }

    [Fact]
    public void LegacyCandidate_ExactlyMatchesOriginalSerializationAndHash()
    {
        var old = Candidate with { SchemaVersion = 1, Scope = "Math", Strategy = null, Coverage = null, Nodes = [Candidate.Nodes[0]] };
        var original = new { old.Title, old.Background, old.Goal, old.WeeklyMinutes, old.Scope, old.Defaults,
            old.Clarifications, old.Limitations, old.SourceKeys, old.Nodes, old.SchemaVersion };
        Assert.Equal(JsonSerializer.Serialize(original, Wire.Options), JsonSerializer.Serialize(old, Wire.Options));
        Assert.Equal(PlanRules.Hash(original), PlanRules.Hash(old));
        Assert.True(new PlanParser().Parse(JsonSerializer.Serialize(old, Wire.Options)).Ok);
    }

    [Fact]
    public async Task V2Metadata_RevisionClearsAndExplicitConfirmationFreezes()
    {
        var store = new SqlitePlanningStore(root);
        var draft = (await store.StartAsync(new(Guid.NewGuid(), 0, Input))).Value!;
        var document = PlanRules.Validate(draft.Id, Input, Candidate, Sources, null).Value!;
        var saved = (await store.SaveProposalAsync(new(Guid.NewGuid(), 0, document), draft.Id)).Value!;
        Assert.Equal(1L, CountMetadata());
        var revised = (await store.ReviseAsync(new(Guid.NewGuid(), saved.Revision, new(draft.Id, Input)))).Value!;
        Assert.Equal(0L, CountMetadata());
        saved = (await store.SaveProposalAsync(new(Guid.NewGuid(), revised.Revision, document), draft.Id)).Value!;
        Assert.False((await store.ConfirmAsync(new(Guid.NewGuid(), saved.Revision, new(draft.Id, document.Hash, false)))).Ok);
        Assert.True((await store.ConfirmAsync(new(Guid.NewGuid(), saved.Revision, new(draft.Id, document.Hash, true)))).Ok);
        var restored = (await new SqlitePlanningStore(root).GetAsync(draft.Id))!;
        Assert.True(restored.Confirmed); Assert.Equal(document.Hash, restored.Plan!.Hash); Assert.Equal(1L, CountMetadata());
    }
    [Theory]
    [InlineData(120)]
    [InlineData(384)]
    [InlineData(505)]
    public async Task LongBookOverlap_ValidatesConfirmsAndRestoresEveryPageBinding(int pages)
    {
        var collection = new PlanCollection(Subject, new(Subject, "v1", new string('B',64),true,
            Enumerable.Range(1,pages).Select(i => new CollectionEntry("page-"+i,i.ToString(),"合成第"+i+"页","textReady",new string('A',64),null,[])).ToArray()),Sources.Select(s=>s.Reference).ToArray());
        var candidate = Candidate with { Nodes = [..Candidate.Nodes, new("n3","综合推演","theory",["计算并证明集合相等"],["n1"],60,null)],
            Coverage = [new($"d1/page-1..{pages}","included",["n1","n2"],null),new($"d1/page-{pages-10}..{pages}","included",["n3"],null)] };
        var parsed = new PlanParser().Parse(JsonSerializer.Serialize(candidate,Wire.Options)); Assert.True(parsed.Ok);
        var store = new SqlitePlanningStore(root); var draft=(await store.StartAsync(new(Guid.NewGuid(),0,Input))).Value!;
        var valid=PlanRules.Validate(draft.Id,Input,parsed.Value!,Sources,null,[collection]); Assert.True(valid.Ok,valid.Error?.Message);
        Assert.Null(CourseMaterials.MissingBindingError(valid.Value!));
        var saved=await store.SaveProposalAsync(new(Guid.NewGuid(),0,valid.Value!),draft.Id); Assert.True(saved.Ok);
        Assert.True((await store.ConfirmAsync(new(Guid.NewGuid(),saved.Value!.Revision,new(draft.Id,valid.Value!.Hash,true)))).Ok);
        var restored=(await new SqlitePlanningStore(root).GetAsync(draft.Id))!;
        Assert.True(restored.Confirmed); Assert.Equal(valid.Value.Hash,restored.Plan!.Hash);
        Assert.Equal(pages,restored.Plan.Candidate.Coverage!.Count);
        Assert.Equal(["n1","n2","n3"],restored.Plan.Candidate.Coverage[^1].NodeKeys);
        Assert.Equal(3,restored.Plan.Candidate.Nodes.Count);
    }
    private long CountMetadata()
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "planning.db")};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM plan_v2_metadata"; return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task HoursInput_RejectsLossyValuesAndPreservesLegacyMinute()
    {
        var store = new SqlitePlanningStore(root);
        var service = new PlanningService(store, new KnowledgeStore(root), new(new SqliteSettingsStore(root)), new(new FakeProvider()), new PlanParser());
        var vm = new PlanningViewModel(service) { Request = "集合", Hours = "0.05" };
        await vm.SaveAsync([]); Assert.Equal(3, Assert.Single(await store.ListAsync()).Input.WeeklyMinutes);
        vm.Hours = "0.01"; await vm.SaveAsync([]); Assert.Contains("VALIDATION_ERROR", vm.Status);
        Assert.Equal(3, Assert.Single(await store.ListAsync()).Input.WeeklyMinutes);
        var legacy = (await store.StartAsync(new(Guid.NewGuid(), 0, Input with { Scope = "Math", WeeklyMinutes = 1 }))).Value!;
        await vm.LoadAsync(); vm.Selected = vm.Drafts.Single(d => d.Draft.Id == legacy.Id);
        Assert.Contains("原值 1 分钟", vm.Hours);
        vm.Request = "修正目标"; await vm.SaveAsync([]);
        Assert.Equal(1, (await store.GetAsync(legacy.Id))!.Input.WeeklyMinutes);
    }
    private sealed class FakeProvider : IModelProvider
    {
        public string Destination => "local:adaptive-test";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct) => throw new InvalidOperationException();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
