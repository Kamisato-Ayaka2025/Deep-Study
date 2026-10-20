using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using StudyHarness.Desktop;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class GenerationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-generation", Guid.NewGuid().ToString());
    private SqlitePlanningStore Plans => new(root);
    private SqliteLearningStore Store => new(root, Plans);
    private KnowledgeStore Knowledge => new(root);
    private SettingsService Settings => new(new SqliteSettingsStore(root));
    private static Command<T> Cmd<T>(T payload) => new(Guid.NewGuid(), 0, payload);
    private async Task<PlanningDraft> Seed()
    {
        Directory.CreateDirectory(root); var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var path = Path.Combine(root, "synthetic.md"); await File.WriteAllTextAsync(path, "# 集合\n\n集合是确定对象的全体。{1,2}有两个元素。重复列举不改变集合。\n");
        var selected = (await Knowledge.SwitchAsync(Cmd(HostSelection.FromFolderPicker(library)))).Value!;
        await Knowledge.ImportAsync(Cmd(HostSelection.FromFilePicker(path)), selected.Id);
        var reference = (await Knowledge.SearchAsync(selected.Id, "", [])).Fragments[0].Reference;
        var input = new PlanningInput("学习集合", "零基础", "理解集合", 120, "Math", [reference]);
        var draft = (await Plans.StartAsync(Cmd(input))).Value!;
        var candidate = new PlanCandidate("集合", "零基础", "理解集合", 120, "Math", [], [], [], ["s1"],
            [new("A", "集合", "theory", ["理解集合"], [], 20, null), new("B", "关系", "theory", ["理解关系"], [], 20, null),
             new("C", "元素", "theory", ["判断元素"], ["A"], 20, null), new("D", "综合", "theory", ["综合"], ["A", "B"], 20, null)]);
        var doc = PlanRules.Validate(draft.Id, input, candidate, [new("s1", reference, true)], null).Value!;
        var proposed = (await Plans.SaveProposalAsync(Cmd(doc), draft.Id)).Value!;
        return (await Plans.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, doc.Hash, true)))).Value!;
    }
    private GenerationService Service(IModelProvider provider, bool legacy = true) => new(Plans, Store, Store, Knowledge, Settings, new(provider), new TheoryLessonParser(), legacy);
    private async Task<GenerationTask> Request(PlanningDraft draft, GenerationTrigger trigger = GenerationTrigger.Click, string node = "A") =>
        (await Store.RequestAsync(draft.Id, Cmd(new RequestGeneration(node, trigger)))).Value!;
    private void Sql(string sql)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "learning.db")};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
    private sealed class Provider(Func<CancellationToken, Task<Result<ProviderReply>>> reply) : IModelProvider
    {
        public int Calls;
        public string Destination => "local:deterministic-fake";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken cancellationToken) { Interlocked.Increment(ref Calls); return reply(cancellationToken); }
    }
    private sealed class ObservedPlans(IPlanningStore inner) : IPlanningStore
    {
        public int Lists;
        public int DelayMilliseconds;
        public bool WithoutSources;
        public async Task<PlanningDraft?> GetAsync(Guid id, CancellationToken ct = default)
        {
            if (DelayMilliseconds > 0 && ct.WaitHandle.WaitOne(DelayMilliseconds)) ct.ThrowIfCancellationRequested();
            var draft = await inner.GetAsync(id, ct);
            return WithoutSources && draft?.Plan is not null ? draft with { Plan = draft.Plan with { Sources = [] } } : draft;
        }
        public Task<IReadOnlyList<PlanningDraft>> ListAsync(CancellationToken ct = default)
        { Lists++; return inner.ListAsync(ct); }
        public Task<Result<PlanningDraft>> StartAsync(Command<PlanningInput> c, CancellationToken ct = default) => inner.StartAsync(c, ct);
        public Task<Result<PlanningDraft>> ReviseAsync(Command<RevisePlan> c, CancellationToken ct = default) => inner.ReviseAsync(c, ct);
        public Task<Result<PlanningDraft>> SaveProposalAsync(Command<PlanDocument> c, Guid id, CancellationToken ct = default) => inner.SaveProposalAsync(c, id, ct);
        public Task<Result<PlanningDraft>> ConfirmAsync(Command<ConfirmPlan> c, CancellationToken ct = default) => inner.ConfirmAsync(c, ct);
    }
    [Fact]
    public async Task Hybrid_StableWithoutSources_GeneratesAndRestartsWithoutChangingPlan()
    {
        var draft = await Seed();
        var plans = new ObservedPlans(Plans) { WithoutSources = true };
        var store = new SqliteLearningStore(root, plans);
        var material = TheoryFixtureProvider.NewLesson("unused") with { SchemaVersion = 4, SourceKeys = [] };
        var provider = new Provider(_ => Task.FromResult(Result<ProviderReply>.Success(new(System.Text.Json.JsonSerializer.Serialize(material, Wire.Options), true))));
        var service = new GenerationService(plans, store, store, Knowledge, Settings, new(provider), new TheoryLessonParser());
        Assert.True((await service.GenerateAsync(draft.Id, "A")).Ok);
        Assert.True(await service.TickAsync());
        var reader = await new SqliteLearningStore(root, plans).GetLessonAsync(draft.Id, "A");
        Assert.True(reader.Ok, reader.Error?.Message);
        Assert.Empty(reader.Value!.Sources);
        Assert.Contains("未引用外部资料", reader.Value.Blocks[0].Text);
        Assert.Equal(draft.Plan!.Hash, (await Plans.GetAsync(draft.Id))!.Plan!.Hash);
        Assert.Equal(1, provider.Calls);
    }
    [Fact]
    public async Task Hybrid_SourceLessPublication_CannotBypassUnknownScope()
    {
        var draft = await Seed();
        var material = TheoryFixtureProvider.NewLesson("unused") with { SchemaVersion = 4, SourceKeys = [] };
        // D's unspecified "综合" objective is deliberately outside the stable-knowledge allowlist.
        var published = await Store.PublishAsync(Cmd(material), draft.Id, "D", draft.Plan!.Hash, []);
        Assert.False(published.Ok);
        Assert.False((await Store.GetLessonAsync(draft.Id, "D")).Ok);
    }
    [Fact]
    public async Task P11_IdleAndDemandDispatch_DoNotEnumerateUnrelatedPlans()
    {
        var plan = await Seed(); var plans = new ObservedPlans(Plans);
        var service = new GenerationService(plans, Store, Store, Knowledge, Settings, new(new TheoryFixtureProvider()), new TheoryLessonParser());
        for (var i = 0; i < 5; i++) Assert.False(await service.TickAsync());
        Assert.Equal(0, plans.Lists);
        Assert.True((await service.GenerateAsync(plan.Id, "A")).Ok);
        Assert.True(await service.TickAsync());
        Assert.Equal(0, plans.Lists);
        Assert.True((await Store.GetLessonAsync(plan.Id, "A")).Ok);
    }
    [Fact]
    public async Task P11_PreparationDoesNotBlockCaller_DoubleClickQueuesOnlyOnce()
    {
        var plan = await Seed(); var plans = new ObservedPlans(Plans);
        var learning = new SqliteLearningStore(root, plans);
        var service = new GenerationService(plans, learning, learning, Knowledge, Settings, new(new TheoryFixtureProvider()), new TheoryLessonParser());
        var model = new LearningViewModel(service, plan.Id);
        await model.RefreshAsync();
        plans.DelayMilliseconds = 300;
        var operation = model.GenerateAsync();
        Assert.False(operation.IsCompleted); Assert.False(model.CanGenerate); Assert.True(model.IsGenerating);
        await model.GenerateAsync();
        await operation;
        Assert.Single(await learning.TasksAsync());
        plans.DelayMilliseconds = 0;
        Assert.True(await service.TickAsync());
        Assert.Single(await learning.TasksAsync());
        Assert.True((await learning.GetLessonAsync(plan.Id, "A")).Ok);
        model.Close();
    }
    [Fact]
    public async Task P11_CancelPreparationLeavesNoDispatchAndCanRetry()
    {
        var plan = await Seed(); var plans = new ObservedPlans(Plans);
        var learning = new SqliteLearningStore(root, plans);
        var service = new GenerationService(plans, learning, learning, Knowledge, Settings, new(new TheoryFixtureProvider()), new TheoryLessonParser());
        var model = new LearningViewModel(service, plan.Id); await model.RefreshAsync();
        plans.DelayMilliseconds = 300;
        var operation = model.GenerateAsync();
        await model.CancelAsync(); await operation;
        Assert.Empty(await learning.TasksAsync()); Assert.False(await service.TickAsync());
        plans.DelayMilliseconds = 0;
        await model.RefreshAsync(); Assert.True(model.CanGenerate);
        await model.GenerateAsync(); Assert.Single(await learning.TasksAsync());
        model.Close();
    }
    [Fact]
    public async Task P11_UnchangedRefreshKeepsNodeIdentityAndEmitsNoReset()
    {
        var plan = await Seed(); var model = new LearningViewModel(Service(new TheoryFixtureProvider(), false), plan.Id);
        await model.RefreshAsync(); var first = model.Nodes[0]; var changes = 0;
        model.Nodes.CollectionChanged += (_, _) => changes++;
        await model.RefreshAsync(); await model.RefreshAsync();
        Assert.Same(first, model.Nodes[0]); Assert.Equal(0, changes);
        model.Close();
    }
    [Fact]
    public async Task P11_CloseDuringPreparationDoesNotQueueOrPublishLateState()
    {
        var plan = await Seed(); var plans = new ObservedPlans(Plans);
        var learning = new SqliteLearningStore(root, plans);
        var service = new GenerationService(plans, learning, learning, Knowledge, Settings, new(new TheoryFixtureProvider()), new TheoryLessonParser());
        var model = new LearningViewModel(service, plan.Id); await model.RefreshAsync();
        plans.DelayMilliseconds = 300;
        var operation = model.GenerateAsync(); model.Close();
        var notifications = 0; model.LessonChanged += _ => notifications++;
        await operation;
        Assert.Empty(await learning.TasksAsync()); Assert.False(await service.TickAsync());
        Assert.Equal(0, notifications);
    }
    [Fact]
    public async Task P11_HistorySelectionWinsOverDelayedCurrentRead()
    {
        var plan = await Seed(); var plans = new ObservedPlans(Plans);
        var learning = new SqliteLearningStore(root, plans);
        var service = new GenerationService(plans, learning, learning, Knowledge, Settings, new(new TheoryFixtureProvider("历史夹具新版本")), new TheoryLessonParser());
        var content = TheoryFixtureProvider.NewLesson("s1");
        var original = await learning.PublishAsync(Cmd(content), plan.Id, "A", plan.Plan!.Hash, plan.Plan.Sources);
        Assert.True(original.Ok);
        Assert.True((await service.GenerateAsync(plan.Id, "A", true)).Ok);
        Assert.True(await service.TickAsync());
        var model = new LearningViewModel(service, plan.Id); await model.RefreshAsync();
        plans.DelayMilliseconds = 150;
        var opening = model.OpenAsync(); model.SelectedHistory = model.History.Single(); model.OpenHistory();
        await opening;
        Assert.Equal(original.Value!.LessonHash, model.Reader!.Identity.LessonHash);
        Assert.False(model.Reader.CanAnswer);
        model.Close();
    }
    [Fact]
    public async Task DependentTheory_GeneratesAnswersCompletesAndRestartsWithoutCompletingAncestors()
    {
        var plan = await Seed(); var provider = new TheoryFixtureProvider(); var service = Service(provider, false);
        var request = await service.GenerateAsync(plan.Id, "D");
        Assert.True(request.Ok, request.Error?.Message);
        Assert.True(await service.TickAsync());
        var reader = (await Store.GetLessonAsync(plan.Id, "D")).Value!;
        Assert.True(reader.CanAnswer); Assert.Empty(await Store.CompletedNodesAsync(plan.Id));
        foreach (var question in TheoryFixtureProvider.NewLesson("s1").Questions)
            Assert.True((await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(reader.Identity, question.Id, question.CorrectOptionId)))).Ok);
        var restored = new SqliteLearningStore(root, Plans);
        Assert.Equal(new[] { "D" }, await restored.CompletedNodesAsync(plan.Id));
        Assert.True((await restored.GetLessonAsync(plan.Id, "D")).Value!.CanAnswer);
        Assert.Equal(plan.Plan!.Hash, (await Plans.GetAsync(plan.Id))!.Plan!.Hash);
        Assert.Equal(new[] { "A", "B" }, plan.Plan.Candidate.Nodes.Single(n => n.Key == "D").PrerequisiteKeys);
    }
    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, true)]
    public async Task NewDispatch_RejectsLegacyVersionButExistingPublicationRemainsReadable(int version, bool accepted)
    {
        var plan = await Seed();
        var lesson = TheoryFixtureProvider.NewLesson("s1") with { SchemaVersion = version };
        var provider = new Provider(_ => Task.FromResult(Result<ProviderReply>.Success(
            new(System.Text.Json.JsonSerializer.Serialize(lesson, Wire.Options), true))));
        var service = Service(provider, false);
        Assert.True((await service.GenerateAsync(plan.Id, "A")).Ok);
        Assert.True(await service.TickAsync());
        Assert.Equal(accepted, (await Store.GetLessonAsync(plan.Id, "A")).Ok);
        Assert.Equal(1, provider.Calls);
        if (!accepted)
        {
            Assert.Equal(GenerationStatus.Failed, (await Store.TasksAsync()).Single().Status);
            Assert.True((await Store.PublishAsync(Cmd(lesson), plan.Id, "B", plan.Plan!.Hash, plan.Plan.Sources)).Ok);
            Assert.True((await Store.GetLessonAsync(plan.Id, "B")).Ok);
        }
    }
    [Fact]
    public async Task ConcurrentRequests_OneActiveTaskAndOneGlobalDispatch()
    {
        var plan = await Seed();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => Request(plan))));
        Assert.Single(results.Select(t => t.Id).Distinct());
        var a = results[0]; var b = await Request(plan, node: "B");
        var claims = await Task.WhenAll(new[] { a, b }.Select(t => Task.Run(() => Store.DispatchAsync(t.Id, t.Revision, "deepseek-flash"))));
        Assert.Single(claims, c => c.Ok); Assert.Single(await Store.TasksAsync(), t => t.Status == GenerationStatus.Running);
    }
    [Theory]
    [InlineData(ErrorCode.AUTH_INVALID)] [InlineData(ErrorCode.MODEL_INVALID)] [InlineData(ErrorCode.QUOTA_EXHAUSTED)] [InlineData(ErrorCode.INVALID_MODEL_OUTPUT)]
    public async Task PermanentErrors_AreNotRetried(ErrorCode code)
    {
        var plan = await Seed(); var task = await Request(plan);
        var provider = new Provider(_ => Task.FromResult(Result<ProviderReply>.Fail(code, "synthetic")));
        var service = Service(provider); service.Authorize((await service.PrepareAsync(plan.Id, "A")).Value!, true);
        Assert.True(await service.TickAsync()); Assert.False(await service.TickAsync());
        Assert.Equal(1, provider.Calls); Assert.Equal(GenerationStatus.Failed, (await Store.TasksAsync()).Single().Status);
        Assert.Single(await Store.DispatchesAsync(task.Id));
    }
    [Fact]
    public async Task TransientBudget_PersistsAcrossNewService_RefreshDoesNotReset()
    {
        var plan = await Seed(); var task = await Request(plan);
        var provider = new Provider(_ => Task.FromResult(Result<ProviderReply>.Fail(ErrorCode.TRANSIENT_PROVIDER_ERROR, "synthetic")));
        for (var i = 0; i < 3; i++)
        {
            var service = Service(provider); service.Authorize((await service.PrepareAsync(plan.Id, "A")).Value!, true);
            if (i > 0) await Task.Delay(i == 1 ? 1100 : 3100);
            Assert.True(await service.TickAsync());
            Assert.Equal(i + 1, (await Store.TasksAsync()).Single().Attempts);
        }
        Assert.Equal(3, provider.Calls); Assert.Equal(GenerationStatus.Failed, (await Store.TasksAsync()).Single().Status);
        Assert.False((await Store.RequestAsync(plan.Id, Cmd(new RequestGeneration("A", GenerationTrigger.Click)))).Ok);
        Assert.Equal(3, (await Store.DispatchesAsync(task.Id)).Count);
        Assert.NotEqual(task.Id, (await Request(plan, GenerationTrigger.ManualRetry)).Id);
    }
    [Fact]
    public async Task CancelWinsLateReply_AutoPauseSurvivesModeAndRestart_ResumeExplicit()
    {
        var plan = await Seed(); var service = Service(new TheoryFixtureProvider()); await service.SetModeAsync(GenerationMode.Auto);
        var task = (await Store.TasksAsync()).First(t => t.NodeKey == "A");
        var running = (await Store.DispatchAsync(task.Id, task.Revision, "deepseek-flash")).Value!;
        await Store.CancelAsync(task.Id);
        var result = await Store.CompleteAsync(task.Id, running.Revision, TheoryFixtureProvider.Lesson("s1"), plan.Plan!.Sources, new(10, 20, 30));
        Assert.Equal(ErrorCode.RUN_CANCELLED, result.Error!.Code); Assert.False((await Store.GetLessonAsync(plan.Id, "A")).Ok);
        Assert.Equal(30, (await Store.DispatchesAsync(task.Id)).Single().Usage!.TotalTokens);
        await service.SetModeAsync(GenerationMode.Click); await service.SetModeAsync(GenerationMode.Auto); await Store.RecoverAsync();
        Assert.True((await Store.ControlAsync()).AutoPaused);
        Assert.False(await service.TickAsync()); await service.ResumeAsync(); Assert.False((await Store.ControlAsync()).AutoPaused);
        Assert.Contains(await Store.TasksAsync(), t => t.NodeKey == "A" && t.Status == GenerationStatus.Queued && t.Id != task.Id);
    }
    [Fact]
    public async Task SuccessfulPublishWinsCancel_AtomicOutbox_OneImmutableLesson()
    {
        var plan = await Seed(); var task = await Request(plan); task = (await Store.DispatchAsync(task.Id, 0, "deepseek-flash")).Value!;
        Sql("CREATE TRIGGER fail_publish BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => Store.CompleteAsync(task.Id, task.Revision, TheoryFixtureProvider.Lesson("s1"), plan.Plan!.Sources, null));
        Assert.Equal(GenerationStatus.Running, (await Store.TasksAsync()).Single().Status); Assert.False((await Store.GetLessonAsync(plan.Id, "A")).Ok);
        Sql("DROP TRIGGER fail_publish;");
        Assert.True((await Store.CompleteAsync(task.Id, task.Revision, TheoryFixtureProvider.Lesson("s1"), plan.Plan!.Sources, null)).Ok);
        await Store.CancelAsync(task.Id); Assert.Equal(GenerationStatus.Ready, (await Store.TasksAsync()).Single().Status);
        Assert.True((await Store.GetLessonAsync(plan.Id, "A")).Ok); Assert.Single(await Store.PendingEventsAsync());
    }
    [Fact]
    public async Task CrashRecovery_PreservesDispatchAndRequiresManualRetry()
    {
        var plan = await Seed(); var task = await Request(plan); await Store.DispatchAsync(task.Id, 0, "deepseek-flash");
        await Store.RecoverAsync(); var restored = (await Store.TasksAsync()).Single();
        Assert.Equal(1, restored.Attempts); Assert.Equal("INTERRUPTED_MAY_BE_BILLED", restored.Failure);
        Assert.False(await Service(new TheoryFixtureProvider()).TickAsync());
    }
    [Fact]
    public async Task ConsentDenialAndChangedModel_NeverDispatch_FreeBranchesStillRequireConsent()
    {
        var plan = await Seed(); var provider = new TheoryFixtureProvider(); var service = Service(provider); await Request(plan);
        var preview = (await service.PrepareAsync(plan.Id, "A")).Value!;
        service.Authorize(preview, false); Assert.False(await service.TickAsync()); Assert.Equal(0, provider.Calls);
        service.Authorize(preview, true); Assert.True(await service.TickAsync());
        var reader = (await Store.GetLessonAsync(plan.Id, "A")).Value!;
        foreach (var q in new[] { ("q1", "a"), ("q2", "b") }) Assert.True((await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(reader.Identity, q.Item1, q.Item2)))).Ok);
        await service.SetModeAsync(GenerationMode.Auto);
        Assert.Contains(await Store.TasksAsync(), t => t.NodeKey == "C"); Assert.Contains(await Store.TasksAsync(), t => t.NodeKey == "D");
        Assert.Equal(1, provider.Calls); Assert.False(await service.TickAsync());
        var before = await Settings.GetAsync(); await Settings.SaveAsync(new(Guid.NewGuid(), before.Revision, new(before.Model, before.GenerationMode, ConsentMode.Once, null)));
        var all = (await service.PrepareAsync(plan.Id, null)).Value!; service.Authorize(all, true);
        var config = await Settings.GetAsync(); await Settings.SaveAsync(new(Guid.NewGuid(), config.Revision, new("deepseek-flash", GenerationMode.Auto, config.ConsentMode, null)));
        Assert.False(await service.TickAsync());
    }
    [Fact]
    public async Task TransportIgnoringCancellation_CannotPublishLateReply_UsageNotLost()
    {
        var plan = await Seed(); var release = new TaskCompletionSource<Result<ProviderReply>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(_ => release.Task); var service = Service(provider);
        service.Authorize((await service.PrepareAsync(plan.Id, "A")).Value!, true); var task = await Request(plan);
        var running = service.TickAsync();
        Assert.Equal(GenerationStatus.Running, (await Store.TasksAsync()).Single().Status);
        await service.CancelAsync(task.Id);
        release.SetResult(Result<ProviderReply>.Success(new(System.Text.Json.JsonSerializer.Serialize(TheoryFixtureProvider.Lesson("s1"), Wire.Options), true, Usage: new(7, 9, 16))));
        await running;
        Assert.False((await Store.GetLessonAsync(plan.Id, "A")).Ok);
        Assert.Equal(GenerationStatus.Cancelled, (await Store.TasksAsync()).Single().Status);
        Assert.Equal(16, (await Store.TasksAsync()).Single().Usage!.TotalTokens);
        Assert.Equal(16, (await Store.DispatchesAsync(task.Id)).Single().Usage!.TotalTokens);
    }
    [Fact]
    public async Task AutomaticFakeLoop_GeneratesAnyBranchWithoutMarkingPrerequisitesComplete()
    {
        var plan = await Seed(); var config = await Settings.GetAsync();
        await Settings.SaveAsync(new(Guid.NewGuid(), config.Revision, new(config.Model, GenerationMode.Auto, ConsentMode.Once, null)));
        var provider = new TheoryFixtureProvider(); var service = Service(provider);
        service.Authorize((await service.PrepareAsync(plan.Id, null)).Value!, true); await service.SetModeAsync(GenerationMode.Auto);
        Assert.True(await service.TickAsync());
        var a = (await Store.GetLessonAsync(plan.Id, "A")).Value!;
        foreach (var q in new[] { ("q1", "a"), ("q2", "b") }) await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(a.Identity, q.Item1, q.Item2)));
        Assert.True(await service.TickAsync()); Assert.True(await service.TickAsync());
        Assert.True((await Store.GetLessonAsync(plan.Id, "C")).Ok);
        Assert.DoesNotContain("B", await Store.CompletedNodesAsync(plan.Id));
        Assert.True(await service.TickAsync());
        Assert.True((await Store.GetLessonAsync(plan.Id, "D")).Ok); Assert.Equal(4, provider.Calls);
        Assert.False(await service.TickAsync()); Assert.Equal(4, provider.Calls);
        Assert.Empty(await Store.PendingEventsAsync());
    }
    [Fact]
    public async Task VersionOneMigration_RetainsLessonMasteryAndPosition()
    {
        var plan = await Seed(); var id = (await Store.PublishAsync(Cmd(TheoryFixtureProvider.Lesson("s1")), plan.Id, "A", plan.Plan!.Hash, plan.Plan.Sources)).Value!;
        await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(id, "q1", "a")));
        await Store.SavePositionAsync(Cmd(new SaveReadingPosition(id, "code", 5)));
        Sql("DROP TABLE generation; DROP TABLE generation_control; DROP TABLE dispatches; DROP TABLE generation_budgets; DROP TABLE generation_timings; DROP TABLE quiz_option_orders; PRAGMA user_version=1;");
        var restored = (await Store.GetLessonAsync(plan.Id, "A")).Value!;
        Assert.Equal(id, restored.Identity); Assert.True(restored.Questions[0].EverCorrect);
        Assert.Equal(new ReadingPosition("code", 5), restored.Position); Assert.Single(await Store.AttemptsAsync(plan.Id, "A"));
        Assert.Empty(await Store.TasksAsync()); Assert.False((await Store.ControlAsync()).AutoPaused);
    }
    [Fact]
    public async Task ModeSwitch_CancelsQueuedAutomaticOnly_RunningContinues()
    {
        var plan = await Seed(); var service = Service(new TheoryFixtureProvider()); await service.SetModeAsync(GenerationMode.Auto);
        var task = (await Store.TasksAsync()).First(); await Store.DispatchAsync(task.Id, task.Revision, "deepseek-flash");
        await service.SetModeAsync(GenerationMode.Click);
        Assert.Single(await Store.TasksAsync(), t => t.Status == GenerationStatus.Running);
        Assert.Equal(3, (await Store.TasksAsync()).Count(t => t.Status == GenerationStatus.Cancelled && t.Failure == "MODE_CHANGED"));
        Assert.False((await Store.ControlAsync()).AutoPaused);
    }
    [Fact]
    public async Task ModeChangesRecoveryAndLearningMigration_PreserveCredential_StaleReplacementRejected()
    {
        await Seed(); var before = await Settings.GetAsync();
        Assert.True((await Settings.SaveAsync(new(Guid.NewGuid(), before.Revision, new(before.Model, GenerationMode.Click, ConsentMode.Each, "synthetic-key")))).Ok);
        byte[] Ciphertext()
        {
            using var db = new SqliteConnection($"Data Source={Path.Combine(root, "app.db")};Mode=ReadOnly;Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT protected_value FROM credentials WHERE id='deepseek'";
            return (byte[])cmd.ExecuteScalar()!;
        }
        var original = Ciphertext();
        var service = Service(new TheoryFixtureProvider());
        await service.SetModeAsync(GenerationMode.Auto); await service.SetModeAsync(GenerationMode.Click);
        await Store.RecoverAsync();
        Sql("DROP TABLE generation; DROP TABLE generation_control; DROP TABLE dispatches; DROP TABLE generation_budgets; DROP TABLE generation_timings; DROP TABLE quiz_option_orders; PRAGMA user_version=1;");
        await Store.RecoverAsync();
        await Service(new TheoryFixtureProvider()).SetModeAsync(GenerationMode.Auto);
        Assert.Equal(original, Ciphertext());
        var rejected = await Settings.SaveAsync(new(Guid.NewGuid(), before.Revision, new(before.Model, GenerationMode.Click, ConsentMode.Each, "stale-replacement")));
        Assert.Equal(ErrorCode.REVISION_CONFLICT, rejected.Error!.Code);
        Assert.Equal(original, Ciphertext());
        Assert.Equal("synthetic-key", await new SqliteSettingsStore(root).ReadKeyAsync(default));
    }
    [Fact]
    public async Task DemandOnly_OneClickApprovesOnlySelectedCourse_NoBackgroundAfterCompletion()
    {
        var plan = await Seed(); var provider = new TheoryFixtureProvider(); var service = Service(provider, false);
        await service.SetModeAsync(GenerationMode.Auto); // Legacy persisted setting must not enable product scheduling.
        Assert.Empty(await Store.TasksAsync());
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GenerateAsync(plan.Id, "A")));
        Assert.All(results, r => Assert.True(r.Ok)); Assert.Single(results.Select(r => r.Value!.Id).Distinct());
        Assert.True(await service.TickAsync()); Assert.Equal(1, provider.Calls);
        var lesson = (await Store.GetLessonAsync(plan.Id, "A")).Value!;
        await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(lesson.Identity, "q1", "a")));
        await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(lesson.Identity, "q2", "b")));
        await service.ScanAsync(); Assert.False(await service.TickAsync()); Assert.Single(await Store.TasksAsync());
        Assert.True((await service.GenerateAsync(plan.Id, "D")).Ok); Assert.Equal(1, provider.Calls);
        Assert.True(await service.TickAsync()); Assert.Equal(2, provider.Calls);
        Assert.DoesNotContain("B", await Store.CompletedNodesAsync(plan.Id));
    }
    [Fact]
    public async Task Regenerate_ArchivesOldLessonAndAttempts_PreservesCompletion_ResetsNewQuestionMastery()
    {
        var plan = await Seed(); var original = TheoryFixtureProvider.NewLesson("s1");
        var next = original with { Blocks = original.Blocks.Select(b => b.Id == "definition" ? b with { Text = "第二版：集合是确定对象的全体。" } : b).ToArray() };
        var calls = 0;
        var provider = new Provider(_ => Task.FromResult(Result<ProviderReply>.Success(new(System.Text.Json.JsonSerializer.Serialize(++calls == 1 ? original : next, Wire.Options), true))));
        var service = Service(provider, false);
        await service.GenerateAsync(plan.Id, "A"); await service.TickAsync();
        var before = (await Store.GetLessonAsync(plan.Id, "A")).Value!;
        await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(before.Identity, "q1", "a")));
        await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(before.Identity, "q2", "b")));
        await Store.SavePositionAsync(Cmd(new SaveReadingPosition(before.Identity, "code", 5)));
        await service.GenerateAsync(plan.Id, "A", true); await service.TickAsync();
        var after = (await Store.GetLessonAsync(plan.Id, "A")).Value!;
        Assert.NotEqual(before.Identity.LessonHash, after.Identity.LessonHash); Assert.NotNull(after.CompletedAt);
        Assert.All(after.Questions, q => { Assert.False(q.EverCorrect); Assert.Null(q.Answer); }); Assert.Null(after.Position);
        var history = Assert.Single(await Store.HistoryAsync(plan.Id, "A")); Assert.Equal(before.Identity.LessonHash, history.LessonHash);
        Assert.Equal(before.Questions[0].Options.Select(o => o.Id), history.Reader.Questions[0].Options.Select(o => o.Id));
        Assert.Equal(after.Questions[0].Options.Select(o => o.Id),
            (await new SqliteLearningStore(root, Plans).GetLessonAsync(plan.Id, "A")).Value!.Questions[0].Options.Select(o => o.Id));
        Assert.False(history.Reader.CanAnswer); Assert.All(history.Reader.Questions, q => Assert.True(q.EverCorrect));
        Assert.Equal(new ReadingPosition("code", 5), history.Reader.Position);
        Assert.All(await Store.AttemptsAsync(plan.Id, "A"), a => Assert.Equal(before.Identity.LessonHash, a.LessonHash));
        Assert.False((await Store.SubmitAnswerAsync(Cmd(new SubmitAnswer(before.Identity, "q1", "a")))).Ok);
        Assert.True((await service.GenerateAsync(plan.Id, "C")).Ok);
        Assert.Equal(after.Identity, (await new SqliteLearningStore(root, Plans).GetLessonAsync(plan.Id, "A")).Value!.Identity);
    }
    [Fact]
    public async Task Regenerate_CancelOrFailedPublishRetainsOriginal_ArchiveSwitchIsAtomic()
    {
        var plan = await Seed(); var content = TheoryFixtureProvider.Lesson("s1");
        var original = (await Store.PublishAsync(Cmd(content), plan.Id, "A", plan.Plan!.Hash, plan.Plan.Sources)).Value!;
        var task = await Request(plan, GenerationTrigger.Regenerate); task = (await Store.DispatchAsync(task.Id, task.Revision, "deepseek-flash")).Value!;
        var changed = content with { Blocks = content.Blocks.Select(b => b.Id == "definition" ? b with { Text = "新的内容" } : b).ToArray() };
        Sql("CREATE TRIGGER fail_replace BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => Store.CompleteAsync(task.Id, task.Revision, changed, plan.Plan.Sources, null));
        Assert.Equal(original, (await Store.GetLessonAsync(plan.Id, "A")).Value!.Identity); Assert.Empty(await Store.HistoryAsync(plan.Id, "A"));
        Sql("DROP TRIGGER fail_replace;"); await Store.CancelAsync(task.Id);
        Assert.False((await Store.CompleteAsync(task.Id, task.Revision, changed, plan.Plan.Sources, new(1, 2, 3))).Ok);
        Assert.Equal(original, (await Store.GetLessonAsync(plan.Id, "A")).Value!.Identity); Assert.Empty(await Store.HistoryAsync(plan.Id, "A"));
        var failure = await Request(plan, GenerationTrigger.Regenerate); failure = (await Store.DispatchAsync(failure.Id, failure.Revision, "deepseek-flash")).Value!;
        await Store.FailAsync(failure.Id, failure.Revision, ErrorCode.INVALID_MODEL_OUTPUT, null);
        Assert.Equal(original, (await Store.GetLessonAsync(plan.Id, "A")).Value!.Identity);
    }
    [Fact]
    public async Task DemandOnly_RestartQueuedTaskNeedsClick_ThenReusesTaskWithoutDuplicateDispatch()
    {
        var plan = await Seed(); var provider = new TheoryFixtureProvider();
        var first = await Service(provider, false).GenerateAsync(plan.Id, "A");
        var restarted = Service(provider, false); Assert.False(await restarted.TickAsync());
        var resumed = await restarted.GenerateAsync(plan.Id, "A"); Assert.Equal(first.Value!.Id, resumed.Value!.Id);
        Assert.True(await restarted.TickAsync()); Assert.Equal(1, provider.Calls);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
