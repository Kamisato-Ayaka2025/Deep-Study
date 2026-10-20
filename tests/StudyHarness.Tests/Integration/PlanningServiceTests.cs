using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class PlanningServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-planning-service", Guid.NewGuid().ToString());
    private sealed class Provider : IModelProvider
    {
        public string Destination => "local:planning-test";
        public int Calls { get; private set; }
        public Func<Task>? During { get; set; }
        public string Text { get; set; } = JsonSerializer.Serialize(new PlanCandidate("集合入门", "零基础", "理解集合", 120, "Math",
            [new("background", "零基础", "未提供"), new("goal", "理解集合", "将主题具体化"), new("weeklyMinutes", "120", "未提供")], [], [], ["s1"],
            [new("n1", "集合", "theory", ["解释集合"], [], 30, null)]), Wire.Options);
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Calls++; Assert.Equal(8192, request.MaxOutputTokens);
            if (During is not null) await During();
            ct.ThrowIfCancellationRequested(); return Result<ProviderReply>.Success(new(Text, true, ReadSourceIds: ["s1"], ToolCalls: 1));
        }
    }
    private async Task<(PlanningService Service, Provider Provider, PlanningDraft Draft)> Setup()
    {
        var knowledge = new KnowledgeStore(root); var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var selected = await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)));
        var path = Path.Combine(root, "集合.md"); await File.WriteAllTextAsync(path, "# 集合\n\n集合由不同元素组成。");
        await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(path)), selected.Value!.Id);
        var refs = (await knowledge.SearchAsync(selected.Value.Id, "", [])).Fragments.Select(f => f.Reference).ToArray();
        var provider = new Provider(); var service = new PlanningService(new SqlitePlanningStore(root), knowledge,
            new SettingsService(new SqliteSettingsStore(root)), new ModelEgress(provider), new PlanParser());
        var draft = (await service.StartAsync(new(Guid.NewGuid(), 0, new("学习集合", null, null, null, "Auto", refs)))).Value!;
        return (service, provider, draft);
    }
    [Fact]
    public async Task BroadRequest_DisclosesDefaults_AndConfirmedPlanPersistsWithoutBody()
    {
        var (service, provider, draft) = await Setup();
        var preview = (await service.PrepareAsync(draft.Id, 0)).Value!;
        Assert.Contains("未提供", preview.Review.Sources[0].Text);
        Assert.Contains("学习集合", preview.Review.Sources[0].Text);
        Assert.True(preview.Review.JsonOutput);
        var result = await service.ProposeAsync(preview, true);
        Assert.True(result.Ok); Assert.Equal(3, result.Value!.Plan!.Candidate.Defaults.Count);
        Assert.Empty(result.Value.Plan.Candidate.Clarifications);
        var confirmed = await service.ConfirmAsync(new(Guid.NewGuid(), result.Value.Revision, new(draft.Id, result.Value.Plan.Hash, true)));
        Assert.True(confirmed.Ok); Assert.Equal(1, provider.Calls);
        Assert.Equal(ErrorCode.PLAN_FROZEN, (await service.PrepareAsync(draft.Id, confirmed.Value!.Revision)).Error!.Code);
    }
    [Fact]
    public async Task CompleteRequest_DoesNotAskAgainOrInventDefaults()
    {
        var (service, provider, draft) = await Setup();
        var input = draft.Input with { Background = "零基础", Goal = "理解集合", WeeklyMinutes = 120, Scope = "Math" };
        draft = (await service.ReviseAsync(new(Guid.NewGuid(), 0, new(draft.Id, input)))).Value!;
        var candidate = JsonSerializer.Deserialize<PlanCandidate>(provider.Text, Wire.Options)! with { Defaults = [] };
        provider.Text = JsonSerializer.Serialize(candidate, Wire.Options);
        var result = await service.ProposeAsync((await service.PrepareAsync(draft.Id, draft.Revision)).Value!, true);
        Assert.True(result.Ok); Assert.Empty(result.Value!.Plan!.Candidate.Defaults); Assert.Empty(result.Value.Plan.Candidate.Clarifications);
    }
    [Fact]
    public async Task DenialAndChangedDraft_DoNotSend_AndLateResponseCannotOverwriteRevision()
    {
        var (service, provider, draft) = await Setup(); var preview = (await service.PrepareAsync(draft.Id, 0)).Value!;
        Assert.Equal(ErrorCode.CONSENT_DENIED, (await service.ProposeAsync(preview, false)).Error!.Code); Assert.Equal(0, provider.Calls);
        provider.During = async () => { await service.ReviseAsync(new(Guid.NewGuid(), 0, new(draft.Id, draft.Input with { Request = "更新目标" }))); };
        Assert.Equal(ErrorCode.REVISION_CONFLICT, (await service.ProposeAsync(preview, true)).Error!.Code);
        Assert.Null((await service.GetAsync(draft.Id))!.Plan);
        Assert.Equal(ErrorCode.REVISION_CONFLICT, (await service.ProposeAsync(preview, true)).Error!.Code);
        Assert.Equal(1, provider.Calls);
    }
    [Fact]
    public async Task CancellationAndInvalidOutput_NeverSaveProposal()
    {
        var (service, provider, draft) = await Setup(); var preview = (await service.PrepareAsync(draft.Id, 0)).Value!;
        provider.Text = "{\"completed\":true}";
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, (await service.ProposeAsync(preview, true)).Error!.Code);
        Assert.Null((await service.GetAsync(draft.Id))!.Plan);
        using var cts = new CancellationTokenSource(); provider.During = () => { cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProposeAsync(preview, true, cts.Token));
        Assert.Null((await service.GetAsync(draft.Id))!.Plan);
    }
    [Fact]
    public async Task MissingSourcesAndZeroTime_StopBeforeModel()
    {
        var (service, provider, draft) = await Setup();
        draft = (await service.ReviseAsync(new(Guid.NewGuid(), 0, new(draft.Id, draft.Input with { WeeklyMinutes = 0 })))).Value!;
        Assert.Equal(ErrorCode.VALIDATION_ERROR, (await service.PrepareAsync(draft.Id, draft.Revision)).Error!.Code);
        Assert.Equal(0, provider.Calls);
        var empty = Path.Combine(root, "empty");
        var withoutKnowledge = new PlanningService(new SqlitePlanningStore(empty), new KnowledgeStore(empty), new SettingsService(new SqliteSettingsStore(empty)), new ModelEgress(provider), new PlanParser());
        var bare = (await withoutKnowledge.StartAsync(new(Guid.NewGuid(), 0, new("集合", null, null, null, "Auto", [])))).Value!;
        Assert.Equal(ErrorCode.INSUFFICIENT_SOURCES, (await withoutKnowledge.PrepareAsync(bare.Id, 0)).Error!.Code);
    }
    [Fact]
    public async Task H2Draft_AddKnownTime_RecomputesDefaultPolicyAndWeeks_ZeroStopsBeforeSend()
    {
        var (service, provider, draft) = await Setup();
        var template = JsonSerializer.Deserialize<PlanCandidate>(provider.Text, Wire.Options)! with
        {
            Nodes = Enumerable.Range(1, 6).Select(i => new PlannedNode($"n{i}", $"课{i}", "theory", ["理解集合"], i == 1 ? [] : [$"n{i - 1}"], 60, null)).ToArray()
        };
        provider.Text = JsonSerializer.Serialize(template, Wire.Options);
        draft = (await service.ProposeAsync((await service.PrepareAsync(draft.Id, draft.Revision)).Value!, true)).Value!;
        Assert.Equal(3, draft.Plan!.Candidate.Defaults.Count);
        foreach (var minutes in new[] { 120, 480 })
        {
            draft = (await service.ReviseAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, draft.Input with { WeeklyMinutes = minutes })))).Value!;
            Assert.Null(draft.Plan);
            var preview = (await service.PrepareAsync(draft.Id, draft.Revision)).Value!;
            Assert.Contains("\"allowedDefaultFields\":[\"background\",\"goal\"]", preview.Review.Sources[0].Text);
            // Preserve rejection of stale default metadata, even when the numeric value agrees.
            provider.Text = JsonSerializer.Serialize(template with { WeeklyMinutes = minutes,
                Defaults = template.Defaults.Select(d => d.Field == "weeklyMinutes" ? d with { Value = minutes.ToString() } : d).ToArray() }, Wire.Options);
            var invalid = await service.ProposeAsync(preview, true);
            Assert.Contains("weeklyMinutes 错标为默认值", invalid.Error!.Message);
            Assert.Null((await service.GetAsync(draft.Id))!.Plan);
            provider.Text = JsonSerializer.Serialize(template with { WeeklyMinutes = minutes,
                Defaults = template.Defaults.Where(d => d.Field != "weeklyMinutes").ToArray() }, Wire.Options);
            draft = (await service.ProposeAsync((await service.PrepareAsync(draft.Id, draft.Revision)).Value!, true)).Value!;
            Assert.Equal(minutes == 120 ? 3 : 1, draft.Plan!.Weeks.Count);
            Assert.DoesNotContain(draft.Plan.Candidate.Defaults, d => d.Field == "weeklyMinutes");
        }
        var calls = provider.Calls;
        draft = (await service.ReviseAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, draft.Input with { WeeklyMinutes = 0 })))).Value!;
        Assert.Equal(ErrorCode.VALIDATION_ERROR, (await service.PrepareAsync(draft.Id, draft.Revision)).Error!.Code);
        Assert.Equal(calls, provider.Calls);
    }

    [Theory]
    [InlineData("学习C语言", "Math", false)]
    [InlineData("学习 C 语言。", "Math", false)]
    [InlineData("学习C语言", "SimpleC", true)]
    [InlineData("学习C语言", "Auto", true)]
    [InlineData("学习集合与关系", "Math", true)]
    [InlineData("不用C语言，只学习集合", "Math", true)]
    public async Task H4_ExplicitTopicConflictStopsBeforeDispatch_WithoutReclassifyingOtherRequests(string request, string scope, bool allowed)
    {
        var (service, provider, draft) = await Setup();
        draft = (await service.ReviseAsync(new(Guid.NewGuid(), 0, new(draft.Id, draft.Input with { Request = request, Scope = scope, WeeklyMinutes = 480 })))).Value!;
        var result = await service.PrepareAsync(draft.Id, draft.Revision);
        Assert.Equal(allowed, result.Ok);
        if (!allowed)
        {
            Assert.Equal(ErrorCode.VALIDATION_ERROR, result.Error!.Code);
            Assert.Contains("纯数学", result.Error.Message);
            Assert.Contains("尚未发送", result.Error.Message);
        }
        else if (scope == "Math") Assert.Contains("\"scope\":\"Math\"", result.Value!.Review.Sources[0].Text);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(scope, (await service.GetAsync(draft.Id))!.Input.Scope);
    }

    [Fact]
    public async Task MathConflictClarification_PreservesScope_AndCannotConfirm()
    {
        var (service, provider, draft) = await Setup();
        draft = (await service.ReviseAsync(new(Guid.NewGuid(), 0, new(draft.Id, draft.Input with { Scope = "Math" })))).Value!;
        var candidate = JsonSerializer.Deserialize<PlanCandidate>(provider.Text, Wire.Options)!;
        provider.Text = JsonSerializer.Serialize(candidate with { Clarifications = ["请澄清数学目标与编程素材的冲突"] }, Wire.Options);
        var result = await service.ProposeAsync((await service.PrepareAsync(draft.Id, draft.Revision)).Value!, true);
        Assert.True(result.Ok); Assert.Equal("Math", result.Value!.Plan!.Candidate.Scope);
        Assert.False((await service.ConfirmAsync(new(Guid.NewGuid(), result.Value.Revision, new(draft.Id, result.Value.Plan.Hash, true)))).Ok);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
