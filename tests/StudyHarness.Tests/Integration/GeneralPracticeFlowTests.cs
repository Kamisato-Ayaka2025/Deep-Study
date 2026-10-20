using System.IO.Compression;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Desktop;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class GeneralPracticeFlowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-practice-flow", Guid.NewGuid().ToString("N"));
    private static GeneralPractice Material => new("exercise", "paper-exercise", "集合证明", ["证明交换律"], [],
        [new("t1", "证明A并B=B并A", "任意集合A和B", "两边含有完全相同的元素", "任取x，x在A或B中当且仅当x在B或A中")], "按元素法核对双向包含。", ["s1"]);
    private sealed class Provider : IModelProvider
    {
        public string Destination => "local:general-practice";
        public int Calls { get; private set; }
        public Func<Task>? During { get; set; }
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Calls++;
            Assert.Contains("通用实践", request.Sources[0].Text);
            if (During is not null) await During();
            return Result<ProviderReply>.Success(new(JsonSerializer.Serialize(Material, Wire.Options), true));
        }
    }
    private async Task<(PlanningDraft Draft, SqlitePlanningStore Plans, SqliteLearningStore Store, LabService Service, Provider Provider)> Setup()
    {
        var knowledge = new KnowledgeStore(root); var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var selected = await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)));
        var path = Path.Combine(root, "集合.md"); await File.WriteAllTextAsync(path, "# 集合\n\n并集包含至少属于一方的元素。证明集合相等需证明双向包含。");
        await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(path)), selected.Value!.Id);
        var reference = (await knowledge.SearchAsync(selected.Value.Id, "", [])).Fragments.First().Reference;
        var plans = new SqlitePlanningStore(root);
        var input = new PlanningInput("复习集合证明", "已学集合", "证明交换律", 120, "Adaptive", [reference]);
        var draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
        var candidate = new PlanCandidate("集合", input.Background!, input.Goal!, 120, "Adaptive", [], [], [], ["s1"],
            [new("n1", "证明", "lab", ["证明交换律"], [], 60, new("independent", null, "paper-exercise", ["s1"], "练习证明", "exercise")),
             new("n2", "后继", "theory", ["迁移应用"], ["n1"], 60, null)],
            2, Strategy: new("集合", ["exercise", "theory"], "先复习再迁移", ["paper-exercise"], [], []), Coverage: []);
        var document = PlanRules.Validate(draft.Id, input, candidate, [new("s1", reference, true)], null);
        Assert.True(document.Ok, document.Error?.Message);
        var saved = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, document.Value!), draft.Id)).Value!;
        draft = (await plans.ConfirmAsync(new(Guid.NewGuid(), saved.Revision, new(draft.Id, document.Value!.Hash, true)))).Value!;
        var store = new SqliteLearningStore(root, plans); var provider = new Provider();
        var service = new LabService(plans, store, store, knowledge, new(new SqliteSettingsStore(root)), new(provider), new LabZipExporter());
        return (draft, plans, store, service, provider);
    }

    [Fact]
    public async Task GenerateReadRestartExportAttest_UsesIndependentSchemaAndNeverAutoCompletes()
    {
        var (draft, plans, store, service, provider) = await Setup();
        var result = await service.GenerateAsync(draft.Id, "n1"); Assert.True(result.Ok, result.Error?.Message);
        Assert.NotNull(result.Value!.Practice); Assert.Equal(5, result.Value.Material.SchemaVersion);
        Assert.False(LabMaterialRules.Validate(result.Value.Material, ["s1"]).Ok);
        Assert.Empty(await store.CompletedNodesAsync(draft.Id));
        var restored = await new SqliteLearningStore(root, plans).GetLabAsync(draft.Id, "n1");
        Assert.Equal(result.Value.Hash, restored!.Hash);
        var vm = new LabViewModel(service, draft.Id, "n1"); await vm.LoadAsync();
        Assert.Contains("双向包含", vm.Instructions); Assert.Empty(vm.Files);
        var zipPath = Path.Combine(root, "practice.zip"); Assert.True((await service.ExportAsync(draft.Id, "n1", zipPath)).Ok);
        using (var zip = ZipFile.OpenRead(zipPath)) Assert.Equal("README.md", Assert.Single(zip.Entries).FullName);
        Assert.Empty(await store.CompletedNodesAsync(draft.Id));
        Assert.False((await service.ConfirmAsync(draft.Id, "n1", result.Value.Hash, false)).Ok);
        Assert.True((await service.ConfirmAsync(draft.Id, "n1", result.Value.Hash, true)).Ok);
        Assert.Contains("n1", await store.CompletedNodesAsync(draft.Id));
        Assert.Equal("userAttested", (await store.GetLabAttestationAsync(draft.Id, "n1"))!.CompletionKind);
        Assert.True((await service.GenerateAsync(draft.Id, "n1")).Ok); Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task DeleteDuringModelReply_RejectsLatePublication()
    {
        var (draft, _, store, service, provider) = await Setup();
        provider.During = async () => { await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true); };
        Assert.False((await service.GenerateAsync(draft.Id, "n1")).Ok);
        Assert.Null(await store.GetLabAsync(draft.Id, "n1")); Assert.Empty(await store.CompletedNodesAsync(draft.Id));
    }

    [Fact]
    public async Task WrongCapabilityAndCancelledJob_CannotPublish()
    {
        var (draft, _, store, _, _) = await Setup();
        var job = (await store.BeginLabAsync(draft.Id, "n1")).Value!;
        Assert.False((await store.PublishPracticeAsync(job, Material with { Kind = "project", Capability = "manual-project" }, draft.Plan!.Hash, draft.Plan.Sources)).Ok);
        Assert.True(await store.UpdateLabJobAsync(job with { Status = "cancelled" }));
        Assert.False((await store.PublishPracticeAsync(job, Material, draft.Plan.Hash, draft.Plan.Sources)).Ok);
        Assert.Null(await store.GetLabAsync(draft.Id, "n1"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
