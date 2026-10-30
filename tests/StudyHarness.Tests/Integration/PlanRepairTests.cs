using System.Text.Json;
using System.Text.Json.Nodes;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class PlanRepairTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-repair", Guid.NewGuid().ToString("N"));
    private static PlanCandidate Candidate => new("集合", "零基础", "理解集合", 120, "Adaptive", [], [], [], ["s1"],
        [new("n1", "定义", "theory", ["解释集合"], [], 60, null),
         new("n2", "证明", "lab", ["证明相等"], ["n1"], 60, new("independent", null, "manual-project", ["s1"], "训练", "project"))],
        2, Strategy: new("集合", ["theory", "project"], "先理解再证明", ["manual-project"], [], []), Coverage: []);
    private static string Json(PlanCandidate p) => JsonSerializer.Serialize(p, Wire.Options);
    private sealed class Provider : IModelProvider
    {
        public string Destination => Transport?.Destination ?? "local:repair-test";
        public List<ApprovedModelRequest> Requests { get; } = [];
        public Func<int, Result<ProviderReply>> Reply { get; set; } = _ => Result<ProviderReply>.Success(new(Json(Candidate), true, Usage: new(10, 20, 30)));
        public Func<Task>? During { get; set; }
        public IModelProvider? Transport { get; set; }
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        { Requests.Add(request); if (During is not null) await During(); ct.ThrowIfCancellationRequested(); return Transport is null ? Reply(Requests.Count) : await Transport.SendAsync(request, ct); }
    }
    private sealed class Telemetry : ITaskTelemetry
    {
        public List<TaskTiming> Rows { get; } = [];
        public Task SaveBudgetAsync(TaskBudgetRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(TaskTiming timing, CancellationToken ct = default) { Rows.Add(timing); return Task.CompletedTask; }
    }
    private sealed class LargeCaps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct) => Task.FromResult(new ModelCapabilities(model, 1048576, 32768, true, DateTimeOffset.UtcNow, true)); }
    private async Task<(PlanningService Service, PlanningReview Review, Provider Provider, Telemetry Telemetry)> Setup(bool redundantMetadataCase = false, bool largeCapacity = false)
    {
        var knowledge = new KnowledgeStore(root); var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var selected = await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)));
        var path = Path.Combine(root, "source.md"); await File.WriteAllTextAsync(path, "# 集合\n集合相等可证明双向包含。");
        await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(path)), selected.Value!.Id);
        if (redundantMetadataCase)
        {
            var second=Path.Combine(root,"practice.md"); await File.WriteAllTextAsync(second,"# 练习\n通过双向包含证明集合相等。");
            await knowledge.ImportAsync(new(Guid.NewGuid(),0,HostSelection.FromFilePicker(second)),selected.Value.Id);
        }
        var refs = (await knowledge.SearchAsync(selected.Value.Id, "", [])).Fragments.Select(f => f.Reference).ToArray();
        var provider = new Provider(); var telemetry = new Telemetry();
        var service = new PlanningService(new SqlitePlanningStore(root), knowledge, new(new SqliteSettingsStore(root)), new(provider, largeCapacity ? new LargeCaps() : null), new PlanParser(), telemetry);
        var draft = (await service.StartAsync(new(Guid.NewGuid(), 0, new("学习集合", redundantMetadataCase ? null : "零基础", "理解集合", 120, "Adaptive", refs)))).Value!;
        return (service, (await service.PrepareAsync(draft.Id, 0)).Value!, provider, telemetry);
    }
    [Fact]
    public async Task ExplicitRetry_RetainsLastCandidateAndErrors_RequiresConsentAndRemainsBounded()
    {
        var (service, review, provider, _) = await Setup();
        var invalid = Json(Candidate with { WeeklyMinutes = 480 });
        provider.Reply = _ => Result<ProviderReply>.Success(new(invalid, true));
        Assert.False((await service.ProposeAsync(review, true)).Ok);
        Assert.True(review.CanRetry); Assert.Equal(2, provider.Requests.Count);
        Assert.False((await service.ProposeAsync(review, false)).Ok);
        Assert.Equal(2, provider.Requests.Count);
        Assert.False((await service.ProposeAsync(review, true)).Ok);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(invalid, provider.Requests[2].PlanRepair!.Candidate);
        Assert.Contains("weeklyMinutes", provider.Requests[2].PlanRepair!.Instructions);
        provider.Reply = _ => Result<ProviderReply>.Success(new(Json(Candidate), true));
        Assert.True((await service.ProposeAsync(review, true)).Ok);
        Assert.Equal(4, provider.Requests.Count); Assert.False(review.CanRetry);
    }
    [Fact]
    public async Task ExplicitRetry_ProviderInvalidWithoutCandidate_SendsOnlyHostErrorFeedback()
    {
        var (service, review, provider, _) = await Setup();
        provider.Reply = _ => Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "最终回复为空");
        Assert.False((await service.ProposeAsync(review, true)).Ok);
        Assert.True(review.CanRetry); Assert.Single(provider.Requests);
        provider.Reply = _ => Result<ProviderReply>.Success(new(Json(Candidate), true));
        Assert.True((await service.ProposeAsync(review, true)).Ok);
        Assert.Equal("", provider.Requests[1].PlanRepair!.Candidate);
        Assert.Contains("最终回复为空", provider.Requests[1].PlanRepair!.Instructions);
    }
    [Fact]
    public async Task OrientationOnlyFirstLesson_UsesBoundedRepairWithoutChangingValidConceptLessons()
    {
        var (service, review, provider, _) = await Setup();
        var weak = Candidate with { Nodes = [Candidate.Nodes[0] with { Title = "用途与资料边界", Objectives = ["了解用途", "明确资料缺口"] }, Candidate.Nodes[1]] };
        provider.Reply = n => Result<ProviderReply>.Success(new(Json(n == 1 ? weak : Candidate), true));
        var result = await service.ProposeAsync(review, true);
        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("PDEPTH001", provider.Requests[1].PlanRepair!.Instructions);
        Assert.Equal("定义", result.Value!.Plan!.Candidate.Nodes[0].Title);
    }
    [Fact]
    public void Derivation_OnlyRedundantSummaries_UnknownCapabilitiesAndWrongKindsRemainInvalid()
    {
        var p = Candidate with { Strategy = Candidate.Strategy! with { TeachingModes = ["theory", "exercise"], MaterialCapabilities = ["paper-exercise"] } };
        var parser = new PlanParser();
        Assert.False(AdaptivePlanRules.Valid(parser.ParseRaw(Json(p)).Value!));
        var derived = parser.Parse(Json(p)); Assert.True(derived.Ok); Assert.True(AdaptivePlanRules.Valid(derived.Value!));
        Assert.Equal(Json(Candidate), Json(derived.Value!));
        Assert.False(parser.Parse(Json(p with { Strategy = p.Strategy! with { MaterialCapabilities = ["shell"] } })).Ok);
        p = Candidate with { Nodes = [Candidate.Nodes[0], Candidate.Nodes[1] with { Lab = Candidate.Nodes[1].Lab! with { Kind = "exercise" } }] };
        Assert.Contains(AdaptivePlanRules.Problems(parser.Parse(Json(p)).Value!), s => s.StartsWith("PSTR005 nodes[1]"));
        var node = JsonNode.Parse(Json(Candidate))!; node["strategy"]!.AsObject().Remove("teachingModes"); node["strategy"]!.AsObject().Remove("materialCapabilities");
        Assert.True(parser.Parse(node.ToJsonString()).Ok); Assert.False(parser.ParseRaw(node.ToJsonString()).Ok);
    }
    [Fact]
    public async Task Feedback_ContainsExactErrorAndCandidate_ValidRepairSavedWithAggregateUsage()
    {
        var (service, review, provider, telemetry) = await Setup();
        provider.Reply = n => Result<ProviderReply>.Success(new(Json(n == 1 ? Candidate with { Coverage = [new("invented", "included", ["n99"], null)] } : Candidate), true, Usage: new(10, 20, 30)));
        var result = await service.ProposeAsync(review, true); Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("PCOV004", provider.Requests[1].Sources[^1].Text); Assert.NotNull(provider.Requests[1].PlanRepair);
        Assert.Contains("PCOV004", provider.Requests[1].PlanRepair!.Instructions);
        Assert.Equal(provider.Requests[0].Sources, provider.Requests[1].Sources.Take(provider.Requests[0].Sources.Count));
        Assert.Equal(new TokenUsage(20, 40, 60), result.Value!.Plan!.Usage);
        Assert.Contains(telemetry.Rows, t => t.Phase == "validation" && t.Outcome.Contains("PCOV004"));
    }
    [Fact]
    public async Task NetworkAndContentFailures_ShareThreeDispatches_ExhaustedNeverPublishes()
    {
        var (service, review, provider, telemetry) = await Setup();
        provider.Reply = n => n == 1 ? Result<ProviderReply>.Fail(ErrorCode.TRANSIENT_PROVIDER_ERROR, "temporary") :
            Result<ProviderReply>.Success(new(Json(Candidate with { WeeklyMinutes = 480 }), true));
        var result = await service.ProposeAsync(review, true);
        Assert.False(result.Ok); Assert.Equal(3, provider.Requests.Count); Assert.Contains("weeklyMinutes", result.Error!.Message);
        Assert.Contains(telemetry.Rows, t => t.Phase == "generation" && t.Outcome.Contains("weeklyMinutes"));
        Assert.All(await service.ListAsync(), d => Assert.Null(d.Plan));
    }
    [Fact]
    public void LabSourceMissingFromTopLevel_ReportsExactNodeAndKey_StillRejects()
    {
        var reference = new SourceRef(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "hash",
            new SourceLocator("source", null, "heading", "block"));
        var sources = new[] { new PlanSource("s1", reference, false), new PlanSource("s2", reference, false) };
        var candidate = Candidate with { SourceKeys = ["s2"] };
        var result = PlanRules.Validate(Guid.NewGuid(), new("学习集合", "零基础", "理解集合", 120, "Adaptive", []), candidate, sources, null);
        Assert.False(result.Ok);
        Assert.Contains("PVAL021", result.Error!.Message);
        Assert.Contains("nodes[1].lab.sourceKeys：s1", result.Error.Message);
        Assert.Contains("顶层sourceKeys", result.Error.Message);
        Assert.True(PlanRules.Validate(Guid.NewGuid(), new("学习集合", "零基础", "理解集合", 120, "Adaptive", []),
            candidate with { SourceKeys = ["s1", "s2"] }, sources, null).Ok);
    }
    [Fact]
    public async Task RedundantSourceAndDefaultMetadata_IsNormalizedInOneDispatch_ThenConfirmsAndRestores()
    {
        var (service,review,provider,_)=await Setup(true);
        var candidate=Candidate with { Defaults=[new("background","入门基础的另一种表述","用户未填写")],
            Nodes=[Candidate.Nodes[0],Candidate.Nodes[1] with { Lab=Candidate.Nodes[1].Lab! with { SourceKeys=["s2"] } }] };
        provider.Reply=_=>Result<ProviderReply>.Success(new(Json(candidate),true));
        var result=await service.ProposeAsync(review,true);
        Assert.True(result.Ok,result.Error?.Message); Assert.Single(provider.Requests);
        var draft=result.Value!; var plan=draft.Plan!;
        Assert.Equal(["s1","s2"],plan.Candidate.SourceKeys);
        Assert.Equal("零基础",Assert.Single(plan.Candidate.Defaults).Value);
        Assert.Equal(JsonSerializer.Serialize(candidate.Nodes,Wire.Options),JsonSerializer.Serialize(plan.Candidate.Nodes,Wire.Options));
        Assert.True((await service.ConfirmAsync(new(Guid.NewGuid(),draft.Revision,new(draft.Id,plan.Hash,true)))).Ok);
        var restored=(await new SqlitePlanningStore(root).GetAsync(draft.Id))!;
        Assert.True(restored.Confirmed); Assert.Equal(plan.Hash,restored.Plan!.Hash);
    }
    [Fact]
    public void Normalization_PreservesUserFieldsUnknownSourcesAndLegacyPlans()
    {
        var input=new PlanningInput("学习集合","已填基础","已填目标",120,"Adaptive",[]);
        var source=new PlanSource("s1",new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"hash",new("source",null,"title","block")),false);
        var candidate=Candidate with { Defaults=[new("background","另一种文字","默认")],
            Nodes=[Candidate.Nodes[0],Candidate.Nodes[1] with {Lab=Candidate.Nodes[1].Lab! with { SourceKeys=["s99"] }}] };
        var normalized=PlanCandidateNormalization.Apply(candidate,input,[source]);
        Assert.Equal(candidate.Defaults,normalized.Defaults); Assert.Equal(candidate.Background,normalized.Background);
        Assert.DoesNotContain("s99",normalized.SourceKeys); Assert.Equal(["s99"],normalized.Nodes[1].Lab!.SourceKeys);
        Assert.False(PlanRules.Validate(Guid.NewGuid(),input,normalized,[source],null).Ok);
        var legacy=candidate with {SchemaVersion=1}; Assert.Same(legacy,PlanCandidateNormalization.Apply(legacy,input,[source]));
    }
    [Fact]
    public void AllLabSourceErrors_AreReportedTogether_WithoutInventingSources()
    {
        var reference=new SourceRef(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"hash",new("source",null,"title","block"));
        var candidate=Candidate with { Nodes=[Candidate.Nodes[0],Candidate.Nodes[1] with {Lab=Candidate.Nodes[1].Lab! with {SourceKeys=["s98"]}},
            Candidate.Nodes[1] with {Key="n3",Lab=Candidate.Nodes[1].Lab! with {SourceKeys=["s99"]}}] };
        var input=new PlanningInput("学习集合","零基础","理解集合",120,"Adaptive",[]);
        var normalized=PlanCandidateNormalization.Apply(candidate,input,[new("s1",reference,false)]);
        var result=PlanRules.Validate(Guid.NewGuid(),input,normalized,[new("s1",reference,false)],null);
        Assert.False(result.Ok); Assert.Contains("共2项",result.Error!.Message);
        Assert.Contains("nodes[1].lab.sourceKeys：s98",result.Error.Message); Assert.Contains("nodes[2].lab.sourceKeys：s99",result.Error.Message);
    }
    [Fact]
    public async Task FirstAttemptModeAndDenial_DoNotRepair()
    {
        var (service, review, provider, _) = await Setup();
        provider.Reply = _ => Result<ProviderReply>.Success(new(Json(Candidate with { WeeklyMinutes = 480 }), true));
        Assert.False((await service.ProposeAsync(review, false)).Ok); Assert.Empty(provider.Requests);
        Assert.False((await service.ProposeAsync(review, true, repairInvalid: false)).Ok); Assert.Single(provider.Requests);
    }
    [Fact]
    public async Task RevisionChange_StopsBeforeRepair_AndCancellationStopsBeforePublication()
    {
        var (service, review, provider, _) = await Setup();
        provider.Reply = _ => Result<ProviderReply>.Success(new(Json(Candidate with { WeeklyMinutes = 480 }), true));
        provider.During = async () => { var d = (await service.ListAsync()).Single(); await service.ReviseAsync(new(Guid.NewGuid(), d.Revision, new(d.Id, d.Input with { Request = "改变目标" }))); };
        Assert.Equal(ErrorCode.REVISION_CONFLICT, (await service.ProposeAsync(review, true)).Error!.Code); Assert.Single(provider.Requests);
        var d = (await service.ListAsync()).Single(); review = (await service.PrepareAsync(d.Id, d.Revision)).Value!;
        using var cts = new CancellationTokenSource(); provider.During = () => { cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProposeAsync(review, true, cts.Token));
        Assert.Null((await service.ListAsync()).Single().Plan);
    }
    [Fact]
    public async Task OversizedCandidate_StopsWithoutTruncatingOrDispatchingRepair()
    {
        var (service, review, provider, _) = await Setup();
        provider.Reply = _ => Result<ProviderReply>.Success(new(new string('x', 524289), true));
        var result = await service.ProposeAsync(review, true);
        Assert.False(result.Ok); Assert.Contains("PREPAIR001", result.Error!.Message); Assert.Single(provider.Requests);
    }
    [Fact]
    public async Task LargeValidJsonCandidate_RepairsWithinVerifiedContextWithoutTruncation()
    {
        var (service, review, provider, _) = await Setup(largeCapacity: true);
        var invalid = new string(' ', 175000) + Json(Candidate with { WeeklyMinutes = 480 });
        provider.Reply = n => Result<ProviderReply>.Success(new(n == 1 ? invalid : Json(Candidate), true));
        Assert.True((await service.ProposeAsync(review, true)).Ok);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(invalid, provider.Requests[1].PlanRepair!.Candidate);
    }
    [Fact]
    public async Task NonTransientProviderFailure_DoesNotRetry()
    {
        var (service, review, provider, _) = await Setup();
        provider.Reply = _ => Result<ProviderReply>.Fail(ErrorCode.AUTH_INVALID, "fixture");
        Assert.False((await service.ProposeAsync(review, true)).Ok); Assert.Single(provider.Requests);
    }
    private sealed class Key : ICredentialReader
    { public Task<string?> ReadKeyAsync(CancellationToken ct) => Task.FromResult<string?>("repair-test-secret"); }
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var text = Json(Bodies.Count == 1 ? Candidate with { WeeklyMinutes = 480 } : Candidate);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { message = new { content = text }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 }
            })) };
        }
    }
    [Fact]
    public async Task RepairHttp_UsesAssistantCandidateThenHostFeedback_NoSecretOrNewSources()
    {
        var (service, _, provider, _) = await Setup();
        using var handler = new Handler(); using var client = new HttpClient(handler);
        provider.Transport = new DeepSeekProvider(new Key(), client);
        var draft = (await service.ListAsync()).Single();
        var review = (await service.PrepareAsync(draft.Id, draft.Revision)).Value!;
        var result = await service.ProposeAsync(review, true); Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(2, handler.Bodies.Count);
        using var body = JsonDocument.Parse(handler.Bodies[1]); var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("assistant", messages[messages.GetArrayLength() - 2].GetProperty("role").GetString());
        Assert.Equal(Json(Candidate with { WeeklyMinutes = 480 }), messages[messages.GetArrayLength() - 2].GetProperty("content").GetString());
        Assert.Equal("user", messages[messages.GetArrayLength() - 1].GetProperty("role").GetString());
        Assert.Contains("weeklyMinutes", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
        Assert.DoesNotContain("repair-test-secret", handler.Bodies[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<ModelSource>)provider.Requests[1].Sources)[0] = new("bad", 0, ModelSourceKind.UserText, "changed"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
