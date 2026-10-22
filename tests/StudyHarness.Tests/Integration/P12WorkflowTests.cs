using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Desktop;
using StudyHarness.Infrastructure;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Core;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class P12WorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-p12", Guid.NewGuid().ToString());
    private sealed class Provider : IModelProvider
    {
        public string Destination => "local:p12";
        public List<ApprovedModelRequest> Requests { get; } = [];
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var plan = new PlanCandidate("集合", "基础", "理解集合", 120, "Math", [], [], [], ["s1"],
                [new("n1", "集合定义", "theory", ["理解集合"], [], 30, null), new("n2", "集合运算", "theory", ["理解运算"], ["n1"], 30, null)]);
            return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(plan, Wire.Options), true, ReadSourceIds: ["s1"], ToolCalls: 1)));
        }
    }
    private sealed class Web : IWebSources
    {
        public Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WebHit>>([new("集合", "https://oi.wiki/math/set/")]);
        public Task<WebPage> FetchAsync(string url, CancellationToken ct) =>
            Task.FromResult(new WebPage(url,"集合","集合定义：集合表示确定对象的全体。元素不能重复计数。例如集合{1,2}有两个元素。",DateTimeOffset.UnixEpoch));
    }
    private async Task<(PlanningService Service, Provider Provider, SourceRef Reference, KnowledgeStore Knowledge, Guid Library)> Setup()
    {
        Directory.CreateDirectory(root);
        var knowledge = new KnowledgeStore(root);
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var selected = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)))).Value!;
        var path = Path.Combine(root, "source.c"); await File.WriteAllTextAsync(path, "// 集合表示确定对象的全体。\nint a = 1;");
        var imported = await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromPlanFilePicker(path)), selected.Id);
        var provider = new Provider();
        return (new(new SqlitePlanningStore(root), knowledge, new(new SqliteSettingsStore(root)), new(provider), new PlanParser(), explicitSourcesOnly: true),
            provider, imported.Value!.References![0], knowledge, selected.Id);
    }
    [Fact]
    public async Task P16_UnchangedRecoverySaveDoesNotCommitButActualEditIsImmediatelyDurable()
    {
        var f = await Setup();
        var journal = new SqlitePlanningSessionStore(root);
        var vm = new PlanningViewModel(f.Service, sessionStore: journal);
        vm.Request = "first";
        using var observer = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "planning-session.db")};Pooling=False");
        observer.Open();
        using var command = observer.CreateCommand(); command.CommandText = "PRAGMA data_version";
        var version = command.ExecuteScalar();
        journal.Save(journal.Load()!);
        Assert.Equal(version, command.ExecuteScalar());
        vm.Request = "last edit before crash";
        Assert.NotEqual(version, command.ExecuteScalar());
        Assert.Equal("last edit before crash", new SqlitePlanningSessionStore(root).Load()!.Request);
    }
    [Fact]
    public async Task P16_ReloadReusesUnchangedDraftAndInvalidatesChangedRevision()
    {
        var f = await Setup(); var store = new SqlitePlanningStore(root);
        var draft = (await store.StartAsync(new(Guid.NewGuid(), 0, new("first", null, null, null, "Adaptive", [])))).Value!;
        var vm = new PlanningViewModel(f.Service);
        await vm.LoadAsync(); var previous = Assert.Single(vm.Drafts);
        await vm.LoadAsync(); Assert.Same(previous, Assert.Single(vm.Drafts));
        vm.Request = "unsaved input"; vm.AckFrozen = true;
        await vm.LoadAsync(); Assert.Equal("first", vm.Request); Assert.False(vm.AckFrozen);
        var revised = await store.ReviseAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, draft.Input with { Request = "changed" })));
        Assert.True(revised.Ok);
        await vm.LoadAsync(); Assert.NotSame(previous, Assert.Single(vm.Drafts)); Assert.Equal("changed", vm.Request);
        _ = new SqliteLearningStore(root, store);
        Assert.True((await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, revised.Value!.Revision, true)).Ok);
        await vm.LoadAsync(); Assert.Empty(vm.Drafts);
    }
    [Fact]
    public async Task LauncherDotSegmentRootPreservesDraftAndFormsPreviewWithoutDispatch()
    {
        var f = await Setup();
        var launcher = Path.Combine(root, "release", "app");
        Directory.CreateDirectory(launcher);
        var configuredRoot = Path.Combine(launcher, "..", "..");
        var vm = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(configuredRoot));
        await vm.RestoreSessionAsync();
        vm.Request = "学习集合"; vm.Goal = "通过考试"; vm.Hours = "10";
        vm.SetReferences([f.Reference]);
        await vm.FormSubmissionAsync();
        Assert.True(vm.CanSendPlan, vm.Status);
        Assert.NotEmpty(vm.Preview);
        var restored = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        await restored.RestoreSessionAsync();
        Assert.Equal(vm.Request, restored.Request);
        Assert.Equal(vm.Goal, restored.Goal);
        Assert.Equal(vm.Hours, restored.Hours);
        Assert.Equal([f.Reference], restored.LocalReferences);
        await restored.FormSubmissionAsync();
        Assert.True(restored.CanSendPlan, restored.Status);
        Assert.Empty(f.Provider.Requests);
    }
    [Fact]
    public async Task IncompleteInputReferencesAndClarificationSurviveFreshInstanceWithoutDispatch()
    {
        var f = await Setup();
        var vm = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        await vm.RestoreSessionAsync();
        vm.Request = "学习集合"; vm.Background = "基础"; vm.Goal = "理解集合"; vm.Hours = "尚未填完";
        vm.WebTopic = "集合"; vm.WebSite = "example.org"; vm.ClarificationAnswer = "我的未提交回答";
        vm.SetReferences([f.Reference]);
        var collectionId = Guid.NewGuid(); vm.SetImportState("21-40",collectionId);
        var restored = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        await restored.RestoreSessionAsync();
        Assert.Equal(vm.Request, restored.Request); Assert.Equal(vm.Hours, restored.Hours);
        Assert.Equal(vm.WebTopic, restored.WebTopic); Assert.Equal(vm.WebSite, restored.WebSite);
        Assert.Equal(vm.ClarificationAnswer, restored.ClarificationAnswer);
        Assert.Equal("21-40",restored.SavedOcrPages); Assert.Equal(collectionId,restored.SavedOcrCollectionId);
        Assert.Equal([f.Reference], restored.LocalReferences); Assert.Empty(restored.Preview);
        Assert.Empty(f.Provider.Requests); Assert.Empty(await f.Service.ListAsync());
    }
    [Fact]
    public async Task OneSubmissionChainDenyClearEditInvalidationAndConfirmCrashRecovery()
    {
        var f = await Setup();
        var vm = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        vm.Request = "学习集合"; vm.Background = "基础"; vm.Goal = "理解集合"; vm.Hours = "2"; vm.Scope = "Math";
        vm.SetReferences([f.Reference]);
        await vm.FormSubmissionAsync(); Assert.True(vm.CanSendPlan);
        await vm.ProposeAsync(false); Assert.Empty(vm.Preview); Assert.Empty(f.Provider.Requests);
        await vm.FormSubmissionAsync(); vm.Goal = "新目标"; await vm.ProposeAsync(true); Assert.Empty(f.Provider.Requests);
        vm.Goal = "理解集合"; await vm.FormSubmissionAsync(); await vm.ProposeAsync(true);
        Assert.Single(f.Provider.Requests); vm.AckFrozen = true; Assert.True(vm.CanConfirm, vm.Status + " | " + vm.ConfirmationHint);
        await vm.ConfirmAsync(); Assert.NotNull(vm.ConfirmedSubject);
        // Simulate a crash after the durable confirmation but before UI resets the journal.
        var restored = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        await restored.RestoreSessionAsync();
        Assert.Empty(restored.Request); Assert.Empty(restored.LocalReferences); Assert.True(restored.CanEdit);
        Assert.Single(await f.Service.ListAsync(), d => d.Confirmed);
    }
    [Fact]
    public async Task ExplicitSourcesNeverFallBackToHistoricalLibrary()
    {
        var f = await Setup();
        var draft = (await f.Service.StartAsync(new(Guid.NewGuid(), 0, new("集合", null, null, 120, "Math", [])))).Value!;
        var result = await f.Service.PrepareAsync(draft.Id, draft.Revision);
        Assert.Equal(ErrorCode.INSUFFICIENT_SOURCES, result.Error!.Code); Assert.Empty(f.Provider.Requests);
    }
    [Fact]
    public async Task LocalAndWebReferencesCombineAndSurviveRestoreWithoutBorrowingOldLibrary()
    {
        var f = await Setup();
        var vm = new PlanningViewModel(f.Service, web:new WebLearningService(new Web(),f.Knowledge,f.Knowledge,Path.Combine(root,"library")),
            sessionStore:new SqlitePlanningSessionStore(root));
        vm.Request = "学习集合"; vm.SetReferences([f.Reference]); vm.WebTopic = "集合"; vm.WebSite = "oi.wiki";
        await vm.AcquireWebAsync();
        Assert.Contains("网页正文已保存",vm.Status);
        await vm.FormSubmissionAsync();
        var draft = Assert.Single(await f.Service.ListAsync());
        Assert.Equal(2,draft.Input.PreferredReferences.Count);
        Assert.Contains(f.Reference,draft.Input.PreferredReferences);
        var restored = new PlanningViewModel(f.Service,sessionStore:new SqlitePlanningSessionStore(root));
        await restored.RestoreSessionAsync();
        Assert.Equal(2,restored.LocalReferences.Count);
        restored.NewDraft();
        Assert.Empty(restored.LocalReferences);
        restored.Request = "学习基础集合";
        await restored.FormSubmissionAsync();
        Assert.True(restored.CanSendPlan, restored.Status); Assert.DoesNotContain("// 集合",restored.Preview); Assert.DoesNotContain("集合不能",restored.Preview);
        Assert.Empty(restored.Selected!.Draft.Input.PreferredReferences);
        Assert.Empty(f.Provider.Requests);
    }
    [Fact]
    public async Task PdfAndCodeDirectoryImportIndexesWholePdfAndExcludesOtherFiles()
    {
        var f = await Setup(); var directory = Path.Combine(root, "materials"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "a.c"), "int x;");
        await File.WriteAllTextAsync(Path.Combine(directory, "b.cpp"), "int y;");
        await File.WriteAllTextAsync(Path.Combine(directory, "private.txt"), "not-authorized");
        var pdf = new PdfDocumentBuilder(); var font = pdf.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < 3; i++) pdf.AddPage(400,500).AddText("Chapter " + i + " Sets contain distinct elements.", 12, new PdfPoint(20,450), font);
        await File.WriteAllBytesAsync(Path.Combine(directory, "book.pdf"), pdf.Build());
        var result = await f.Knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromPlanFolderPicker(directory)), f.Library);
        Assert.True(result.Ok); Assert.Equal(3, result.Value!.References!.Count);
        Assert.Contains(result.Value.Files, file => !file.Ok && file.SourceLabel == "private.txt");
        var collection = Assert.Single(await f.Knowledge.CollectionsAsync(f.Library));
        Assert.Equal(3, collection.Collection.EntryCount); Assert.Equal(3, collection.Collection.ReadyCount);
        Assert.All(result.Value.References, r => Assert.DoesNotContain("private", r.Locator.SourceLabel));
    }
    [Fact]
    public async Task ConnectionCheckSendsOnlyFixedQuestionAndOneRequest()
    {
        var f = await Setup();
        var vm = new ShellViewModel(new(new SqliteSettingsStore(root)), new(f.Provider), new SafeLog(Path.Combine(root,"log.jsonl")));
        await vm.LoadAsync(); await vm.CheckConnectionAsync();
        var request = Assert.Single(f.Provider.Requests);
        Assert.Equal("你是什么模型？", Assert.Single(request.Sources).Text);
        Assert.Equal(512, request.MaxOutputTokens); Assert.Contains("连接成功", vm.Status);
    }
    [Fact]
    public async Task ScannedImportIsVisibleAutoRangesAndRemovalSurvivesRestart()
    {
        var f = await Setup();
        var workspace = new WorkspaceViewModel(new(f.Knowledge, new(new SqliteSettingsStore(root)), new(f.Provider)));
        var planning = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        workspace.PendingCollections.CollectionChanged += (_, _) => planning.SetPendingCollections(workspace.PendingCollections.ToArray());
        workspace.References.CollectionChanged += (_, _) => planning.SetReferences(workspace.References.ToArray());
        await workspace.LoadAsync();
        var pdf = new PdfDocumentBuilder();
        for (var i = 0; i < 25; i++) pdf.AddPage(400, 500);
        var path = Path.Combine(root, "scanned.pdf"); var bytes = pdf.Build();
        await File.WriteAllBytesAsync(path, bytes);
        await workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
        Assert.Empty(workspace.References); Assert.Single(workspace.Materials);
        Assert.Single(workspace.PendingCollections); Assert.Equal(20, workspace.OcrPages.Split(',').Length);
        Assert.Contains("扫描页", workspace.Status);
        planning.Request = "学习计算机网络";
        await planning.FormSubmissionAsync();
        Assert.Contains("尚无可读正文", planning.Status); Assert.False(planning.CanSendPlan);
        var restored = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        await restored.RestoreSessionAsync(); Assert.Single(restored.PendingCollections);
        workspace.RemoveMaterial(Assert.Single(workspace.Materials));
        await restored.RestoreSessionAsync(); Assert.Empty(restored.PendingCollections);
        Assert.Null(restored.SavedOcrCollectionId); Assert.Empty(workspace.Materials);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(await f.Knowledge.CollectionsAsync(f.Library)); Assert.Empty(f.Provider.Requests);
    }
    [Fact]
    public async Task TextbookOnlyInfersRequestAndGroupedRemovalInvalidatesPreview()
    {
        var f = await Setup();
        var workspace = new WorkspaceViewModel(new(f.Knowledge, new(new SqliteSettingsStore(root)), new(f.Provider)));
        var planning = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        workspace.References.CollectionChanged += (_, _) => planning.SetReferences(workspace.References.ToArray());
        await workspace.LoadAsync();
        workspace.RestoreReferences([f.Reference, f.Reference with { NodeId = Guid.NewGuid() }]);
        Assert.Single(workspace.Materials);
        workspace.RemoveMaterial(workspace.Materials[0]); Assert.Empty(workspace.References);
        workspace.RestoreReferences([f.Reference]);
        await planning.FormSubmissionAsync(); Assert.True(planning.CanSendPlan, planning.Status);
        Assert.Contains(f.Reference.Locator.SourceLabel, planning.Request);
        workspace.RemoveMaterial(workspace.Materials[0]);
        Assert.Empty(planning.Preview); Assert.False(planning.CanSendPlan);
        await planning.ProposeAsync(true); Assert.Empty(f.Provider.Requests);
        var restored = new PlanningViewModel(f.Service, sessionStore: new SqlitePlanningSessionStore(root));
        await restored.RestoreSessionAsync(); Assert.Empty(restored.LocalReferences);
        Assert.NotNull((await f.Knowledge.ResolveAsync(f.Reference)).Text);
    }
    [Fact]
    public async Task MixedPdfAutomaticallyBindsTextAndSuggestsOnlyMissingPages()
    {
        var f = await Setup();
        var workspace = new WorkspaceViewModel(new(f.Knowledge, new(new SqliteSettingsStore(root)), new(f.Provider)));
        await workspace.LoadAsync();
        var pdf = new PdfDocumentBuilder(); var font = pdf.AddStandard14Font(Standard14Font.Helvetica);
        pdf.AddPage(400,500).AddText("Computer networks connect hosts using packet switching.",12,new PdfPoint(20,450),font);
        pdf.AddPage(400,500);
        var path = Path.Combine(root,"mixed.pdf"); await File.WriteAllBytesAsync(path,pdf.Build());
        await workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
        Assert.Single(workspace.References); Assert.Empty(workspace.PendingCollections);
        Assert.Equal("2", workspace.OcrPages); Assert.Contains("部分页待识别",workspace.Status);
        await workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
        Assert.Single(workspace.References); Assert.Single(workspace.Materials);
        Assert.Empty(f.Provider.Requests);
    }
    [Fact]
    public async Task UnknownRecoveryVersionFailsClosedWithoutReplacingSavedInput()
    {
        var f = await Setup();
        var journal = new SqlitePlanningSessionStore(root);
        var vm = new PlanningViewModel(f.Service,sessionStore:journal); vm.Request = "不得丢失";
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"planning-session.db")};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => journal.Load());
        var restored = new PlanningViewModel(f.Service,sessionStore:journal); await restored.RestoreSessionAsync();
        Assert.Contains("恢复失败",restored.RecoveryStatus);
        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"planning-session.db")};Pooling=False");
        check.Open(); using var query = check.CreateCommand(); query.CommandText = "SELECT json FROM session WHERE id=1";
        Assert.Equal("不得丢失",JsonSerializer.Deserialize<PlanningSession>((string)query.ExecuteScalar()!,Wire.Options)!.Request);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
