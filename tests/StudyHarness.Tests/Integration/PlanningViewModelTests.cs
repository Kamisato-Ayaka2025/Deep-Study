using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Desktop;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class PlanningViewModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-planning-vm", Guid.NewGuid().ToString());
    private sealed class Provider : IModelProvider
    {
        public string Destination => "local:planning-vm";
        public int Calls { get; private set; }
        public string[] Questions { get; set; } = [];
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Calls++;
            var plan = new PlanCandidate("集合", "零基础", "理解集合", 120, "Math", [], Questions, [], ["s1"],
                [new("n1", "定义", "theory", ["解释定义"], [], 30, null), new("n2", "应用", "theory", ["举例"], ["n1"], 30, null)]);
            return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(plan, Wire.Options), true, ReadSourceIds: ["s1"], ToolCalls: 1)));
        }
    }
    [Fact]
    public async Task ViewModel_SaveReviseConsentFreezeAndReload_AreConsistent()
    {
        var knowledge = new KnowledgeStore(root); var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var selected = await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)));
        var source = Path.Combine(root, "source.md"); await File.WriteAllTextAsync(source, "# 集合\n\n集合定义。");
        await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(source)), selected.Value!.Id);
        var refs = (await knowledge.SearchAsync(selected.Value.Id, "", [])).Fragments.Select(f => f.Reference).ToArray();
        var provider = new Provider(); var service = new PlanningService(new SqlitePlanningStore(root), knowledge, new SettingsService(new SqliteSettingsStore(root)), new ModelEgress(provider), new PlanParser());
        var vm = new PlanningViewModel(service) { Request = "学习集合", Background = "零基础", Goal = "理解集合", Minutes = "120", Scope = "Math" };
        await vm.SaveAsync(refs); Assert.Single(vm.Drafts);
        await vm.PrepareAsync(); Assert.Contains("8192", vm.Preview);
        vm.Minutes = "480"; await vm.ProposeAsync(true); Assert.Equal(0, provider.Calls);
        vm.Minutes = "120"; await vm.PrepareAsync(); await vm.ProposeAsync(false); Assert.Equal(0, provider.Calls);
        provider.Questions = ["是否接受零基础起点？", "是否有固定学习期限？"];
        await vm.PrepareAsync(); await vm.ProposeAsync(true); Assert.Equal(1, provider.Calls);
        vm.AckFrozen = true; Assert.False(vm.CanConfirm); Assert.True(vm.CanClarify);
        Assert.Contains("补充计划信息", vm.ConfirmationHint);
        await vm.SaveClarificationAsync(); Assert.Equal(1, provider.Calls); Assert.True(vm.HasClarifications);
        vm.ClarificationAnswer = "接受零基础默认值，没有固定期限。";
        Assert.False(vm.CanConfirm);
        await vm.SaveClarificationAsync();
        Assert.Equal(1, provider.Calls); Assert.True(vm.CanSendPlan);
        Assert.Contains("接受零基础默认值", vm.Preview); Assert.Contains("集合定义", vm.Preview);
        Assert.Equal(refs, vm.Selected!.Draft.Input.PreferredReferences);
        Assert.Equal("理解集合", vm.Goal); Assert.Equal("120", vm.Minutes);
        var afterAnswer = new PlanningViewModel(service); await afterAnswer.LoadAsync();
        Assert.Contains("接受零基础默认值", afterAnswer.Request); Assert.Null(afterAnswer.Selected!.Draft.Plan);
        provider.Questions = []; await vm.ProposeAsync(true); Assert.Equal(2, provider.Calls);
        Assert.False(vm.CanConfirm); vm.AckFrozen = true; Assert.True(vm.CanConfirm);
        await vm.ConfirmAsync(); Assert.False(vm.CanEdit); Assert.False(vm.CanConfirm);
        Assert.True(vm.Nodes[0].Eligible); Assert.True(vm.Nodes[1].Eligible);
        vm.SelectNode(vm.Nodes[1]); Assert.Contains("可自由生成", vm.Detail);
        var restored = new PlanningViewModel(service); await restored.LoadAsync(); Assert.False(restored.CanEdit);
        Assert.Contains("理解集合", restored.Summary);
        await restored.RefreshAfterDeletionAsync();
        Assert.Null(restored.Selected); Assert.True(restored.CanEdit); Assert.Empty(restored.Request);
        Assert.Single(restored.Drafts); // Remaining frozen courses must not be auto-selected.
        restored.NewDraft(); Assert.True(restored.CanEdit); Assert.Empty(restored.Nodes);
    }
    [Fact]
    public async Task ChangedGoal_DoesNotCarryOldSourcesOrSearchOverrideIntoSavedRequest()
    {
        var knowledge = new KnowledgeStore(root); var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var selected = await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)));
        var path = Path.Combine(root, "source.md"); await File.WriteAllTextAsync(path, "# 二分\n二分查找在有序数组中缩小区间。");
        await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(path)), selected.Value!.Id);
        var refs = (await knowledge.SearchAsync(selected.Value.Id, "", [])).Fragments.Select(f => f.Reference).ToArray();
        var store = new SqlitePlanningStore(root);
        var vm = new PlanningViewModel(new(store, knowledge, new(new SqliteSettingsStore(root)), new(new Provider()), new PlanParser()));
        vm.Request = "学习二分查找"; await vm.SaveAsync(refs);
        vm.WebTopic = "测试二分查找"; vm.Request = "学习算法竞赛"; vm.Goal = "通过NOIP";
        Assert.Empty(vm.WebTopic);
        await vm.SaveAsync([]);
        var draft = Assert.Single(await store.ListAsync());
        Assert.Equal("通过NOIP", draft.Input.Goal); Assert.Empty(draft.Input.PreferredReferences);
        vm.WebTopic = "测试二分查找"; vm.WebSite = "oi.wiki"; vm.NewDraft();
        Assert.Empty(vm.WebTopic); Assert.Empty(vm.WebSite); Assert.True(vm.CanEdit);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
