using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class P6Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-p6", Guid.NewGuid().ToString("N"));
    private SqlitePlanningStore Plans => new(root);
    private SqliteLearningStore Store => new(root, Plans);
    private SettingsService Settings => new(new SqliteSettingsStore(root));
    private TutoringSession Tutor(IModelProvider provider) => new(Plans, Store, Store, Settings, new(provider));
    private sealed class Provider : IModelProvider
    {
        public string Destination => "local:deterministic-fake";
        public List<ApprovedModelRequest> Requests { get; } = [];
        public Func<Task>? Wait { get; init; }
        public string? Reply { get; init; }
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Requests.Add(request); if (Wait is not null) await Wait();
            return Result<ProviderReply>.Success(new(Reply ?? "{\"hint\":\"先考虑输入边界，再自己检查负数用例。\",\"expression\":null}", true));
        }
    }
    private async Task<LabPackage> Material(PlanningDraft draft)
    {
        var job = (await Store.BeginLabAsync(draft.Id, "L1")).Value!;
        return (await Store.PublishLabAsync(job, LabFixtureProvider.Material, draft.Plan!.Hash, draft.Plan.Sources)).Value!;
    }
    private void Sql(string sql, string database = "learning.db")
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, database)};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
    [Fact]
    public async Task DeleteRemovesTheoryAttemptsPositionsHistoryDispatchesAndReplayCaches()
    {
        var draft = await LabFixtureProvider.SeedAsync(root); var store = Store;
        foreach (var key in new[] { "L1", "L2" })
        {
            var job = (await store.BeginLabAsync(draft.Id, key)).Value!;
            var package = (await store.PublishLabAsync(job, LabFixtureProvider.Material, draft.Plan!.Hash, draft.Plan.Sources)).Value!;
            Assert.True((await store.ConfirmLabAsync(draft.Id, new(Guid.NewGuid(), 0, new(key, package.Hash, true)))).Ok);
        }
        var content = new TheoryLesson([new("b", "example", "例如零属于输入边界。"), new("qblock", "question", QuestionId: "q")],
            [new("q", "边界？", [new("a", "零"), new("b", "无")], "a", "包含零。")], ["s1"]);
        var published = await store.PublishAsync(new(Guid.NewGuid(), 0, content), draft.Id, "T3", draft.Plan!.Hash, draft.Plan.Sources);
        Assert.True(published.Ok, published.Error?.Message);
        Assert.True((await store.SubmitAnswerAsync(new(Guid.NewGuid(), 0, new(published.Value!, "q", "a")))).Ok);
        Assert.True((await store.SavePositionAsync(new(Guid.NewGuid(), 0, new(published.Value!, "b", 1)))).Ok);
        var queued = (await store.RequestAsync(draft.Id, new(Guid.NewGuid(), 0, new("T3", GenerationTrigger.Regenerate)))).Value!;
        var running = (await store.DispatchAsync(queued.Id, queued.Revision, "test-model")).Value!;
        var updated = content with { Blocks = [new("b", "example", "例如负数也是边界用例。"), new("qblock", "question", QuestionId: "q")] };
        Assert.True((await store.CompleteAsync(running.Id, running.Revision, updated, draft.Plan.Sources, null)).Ok);
        Assert.Single(await store.HistoryAsync(draft.Id, "T3")); Assert.Single(await store.AttemptsAsync(draft.Id, "T3"));
        Assert.Single(await store.DispatchesAsync(running.Id));
        Assert.True((await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true)).Ok);
        foreach (var name in new[] { "lessons", "attempts", "mastery", "completions", "positions", "outbox", "generation", "dispatches", "lesson_history", "lab_packages", "lab_jobs", "lab_attestations", "requests" })
        {
            using var db = new SqliteConnection($"Data Source={Path.Combine(root, "learning.db")};Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT count(*) FROM {name}";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
        Assert.False((await store.CompleteAsync(running.Id, running.Revision, content, draft.Plan.Sources, null)).Ok);
        Assert.False((await store.PublishAsync(new(Guid.NewGuid(), 0, content), draft.Id, "T3", draft.Plan.Hash, draft.Plan.Sources)).Ok);
    }
    [Fact]
    public async Task V4MigrationPreservesMaterialAndCompletionAndAssignsNewRequestOwnership()
    {
        var draft = await LabFixtureProvider.SeedAsync(root); var package = await Material(draft);
        await Store.ConfirmLabAsync(draft.Id, new(Guid.NewGuid(), 0, new("L1", package.Hash, true)));
        Sql("ALTER TABLE requests DROP COLUMN subject; DROP TABLE deleted_subjects; DROP TABLE generation_budgets; DROP TABLE generation_timings; DROP TABLE quiz_option_orders; PRAGMA user_version=4;");
        var migrated = Store;
        Assert.Equal(package.Hash, (await migrated.GetLabAsync(draft.Id, "L1"))!.Hash);
        Assert.Contains("L1", await migrated.CompletedNodesAsync(draft.Id));
        Assert.True((await migrated.ConfirmLabAsync(draft.Id, new(Guid.NewGuid(), 0, new("L1", package.Hash, true)))).Ok);
        Assert.Single(await migrated.PendingEventsAsync());
        Assert.True((await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true)).Ok);
    }
    [Fact]
    public async Task DeleteIsScopedConfirmedRevisionCheckedAndIdempotent()
    {
        var draft = await LabFixtureProvider.SeedAsync(root); var package = await Material(draft);
        var other = (await Plans.StartAsync(new(Guid.NewGuid(), 0, draft.Input with { Request = "other" }))).Value!;
        Assert.True((await Store.ConfirmLabAsync(draft.Id, new(Guid.NewGuid(), 0, new("L1", package.Hash, true)))).Ok);
        var export = Path.Combine(root, "kept.zip"); await new LabZipExporter().ExportAsync(package, export);
        var exported = await File.ReadAllBytesAsync(export); var source = await File.ReadAllBytesAsync(Path.Combine(root, "p5-synthetic.md"));
        var lifecycle = new SqliteSubjectLifecycle(root);
        Assert.False((await lifecycle.DeleteAsync(draft.Id, draft.Revision, false)).Ok);
        Assert.False((await lifecycle.DeleteAsync(draft.Id, draft.Revision + 1, true)).Ok);
        Assert.NotNull(await Store.GetLabAsync(draft.Id, "L1"));
        Assert.True((await lifecycle.DeleteAsync(draft.Id, draft.Revision, true)).Ok);
        Assert.Null(await Plans.GetAsync(draft.Id)); Assert.Equal(PlanRules.Hash(other), PlanRules.Hash(await Plans.GetAsync(other.Id)));
        Assert.Null(await Store.GetLabAsync(draft.Id, "L1")); Assert.Null(await Store.GetLabJobAsync(draft.Id, "L1"));
        Assert.Null(await Store.GetLabAttestationAsync(draft.Id, "L1")); Assert.Empty(await Store.CompletedNodesAsync(draft.Id));
        Assert.Empty(await Store.PendingEventsAsync());
        Assert.Equal(exported, await File.ReadAllBytesAsync(export)); Assert.Equal(source, await File.ReadAllBytesAsync(Path.Combine(root, "p5-synthetic.md")));
        Assert.NotNull((await new KnowledgeStore(root).ResolveAsync(draft.Plan!.Sources[0].Reference)).Text);
        Assert.False((await lifecycle.DeleteAsync(draft.Id, draft.Revision, true)).Value);
        Assert.False((await Store.BeginLabAsync(draft.Id, "L1")).Ok);
    }
    [Fact]
    public async Task FailureMidDeleteRollsBackBothDatabasesAndCanRetry()
    {
        var draft = await LabFixtureProvider.SeedAsync(root); var package = await Material(draft);
        Sql("CREATE TRIGGER fail_delete BEFORE DELETE ON drafts BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;", "planning.db");
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true));
        Assert.NotNull(await Plans.GetAsync(draft.Id)); Assert.Equal(package.Hash, (await Store.GetLabAsync(draft.Id, "L1"))!.Hash);
        Sql("DROP TRIGGER fail_delete", "planning.db");
        Assert.True((await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true)).Ok);
        Assert.Null(await Store.GetLabAsync(draft.Id, "L1"));
    }
    [Fact]
    public async Task DeletedSubjectRejectsLateLabPublicationAndUpdate()
    {
        var draft = await LabFixtureProvider.SeedAsync(root); var job = (await Store.BeginLabAsync(draft.Id, "L1")).Value!;
        await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true);
        Assert.False(await Store.UpdateLabJobAsync(job));
        Assert.False((await Store.PublishLabAsync(job, LabFixtureProvider.Material, draft.Plan!.Hash, draft.Plan.Sources)).Ok);
        Assert.Null(await Store.GetLabAsync(draft.Id, "L1"));
    }
    [Theory]
    [InlineData(ConsentMode.Each)] [InlineData(ConsentMode.Once)]
    public async Task TutorRequiresExactConsentAndRetainsOnlyCurrentInMemoryConversation(ConsentMode mode)
    {
        var draft = await LabFixtureProvider.SeedAsync(root); var config = await Settings.GetAsync();
        await Settings.SaveAsync(new(Guid.NewGuid(), config.Revision, new(config.Model, config.GenerationMode, mode, null)));
        var provider = new Provider(); using var session = Tutor(provider);
        Assert.False((await session.SendAsync(true)).Ok);
        var preview = await session.PrepareAsync(draft.Id, "L1", "怎样检查边界？", "/* selected marker */");
        Assert.Contains(preview.Sources, s => s.Text.Contains("selected marker")); Assert.Empty(provider.Requests);
        Assert.False((await session.SendAsync(false)).Ok); Assert.Empty(provider.Requests);
        preview = await session.PrepareAsync(draft.Id, "L1", "怎样检查边界？", "/* selected marker */");
        Assert.True((await session.SendAsync(true)).Ok); Assert.Equal(preview.Sources, provider.Requests[0].Sources);
        Assert.False((await session.SendAsync(true)).Ok); Assert.Single(provider.Requests);
        var next = await session.PrepareAsync(draft.Id, "L1", "为什么？", "");
        Assert.Contains(next.Sources, s => s.Id == "temporary-dialogue" && s.Text.Contains("user"));
        Assert.DoesNotContain(next.Sources, s => s.Id == "selected-code");
        Assert.True((await session.SendAsync(true)).Ok); Assert.Equal(4, session.Messages.Count);
        await session.PrepareAsync(draft.Id, "T3", "请解释理论", ""); Assert.Empty(session.Messages);
        Assert.Empty(Tutor(provider).Messages); Assert.Empty(await Store.CompletedNodesAsync(draft.Id));
    }
    [Fact]
    public async Task SameNodeKeyInAnotherSubjectCannotReuseTutorHistory()
    {
        var draft = await LabFixtureProvider.SeedAsync(root);
        var other = (await Plans.StartAsync(new(Guid.NewGuid(), 0, draft.Input with { Request = "second subject" }))).Value!;
        var validated = PlanRules.Validate(other.Id, other.Input, draft.Plan!.Candidate, draft.Plan.Sources, null);
        Assert.True(validated.Ok, validated.Error?.Message);
        var saved = await Plans.SaveProposalAsync(new(Guid.NewGuid(), other.Revision, validated.Value!), other.Id);
        Assert.True(saved.Ok, saved.Error?.Message); other = saved.Value!;
        other = (await Plans.ConfirmAsync(new(Guid.NewGuid(), other.Revision, new ConfirmPlan(other.Id, other.Plan!.Hash, true)))).Value!;
        var provider = new Provider(); using var session = Tutor(provider);
        await session.PrepareAsync(draft.Id, "L1", "private-first-subject-marker", "");
        Assert.True((await session.SendAsync(true)).Ok);
        var next = await session.PrepareAsync(other.Id, "L1", "second question", "");
        Assert.Empty(session.Messages);
        Assert.Equal("[]", next.Sources.Single(s => s.Id == "temporary-dialogue").Text);
        Assert.DoesNotContain(next.Sources, s => s.Text.Contains("private-first-subject-marker"));
    }
    [Fact]
    public async Task ChangedModelRevokesPreparedRequest()
    {
        var draft = await LabFixtureProvider.SeedAsync(root); var provider = new Provider(); using var session = Tutor(provider);
        await session.PrepareAsync(draft.Id, "L1", "提示", ""); var config = await Settings.GetAsync();
        await Settings.SaveAsync(new(Guid.NewGuid(), config.Revision, new("different-model", config.GenerationMode, config.ConsentMode, null)));
        Assert.Equal(ErrorCode.CONSENT_REQUIRED, (await session.SendAsync(true)).Error!.Code); Assert.Empty(provider.Requests);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelOrDeleteDropsLateTutorReply(bool delete)
    {
        var draft = await LabFixtureProvider.SeedAsync(root); var release = new TaskCompletionSource();
        var provider = new Provider { Wait = () => release.Task }; using var session = Tutor(provider);
        await session.PrepareAsync(draft.Id, "L1", "提示", ""); var pending = session.SendAsync(true);
        Assert.Single(provider.Requests);
        if (delete) await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true); else session.Reset();
        release.SetResult(); Assert.False((await pending).Ok); Assert.Empty(session.Messages);
    }
    [Theory]
    [InlineData("{\"hint\":\"```c full solution```\",\"expression\":null}")]
    [InlineData("{\"hint\":\"answer\",\"expression\":\"int main(){return 0;}\"}")]
    [InlineData("{\"hint\":\"answer\",\"expression\":null,\"completed\":true}")]
    [InlineData("{\"hint\":\"answer\",\"hint\":\"duplicate\",\"expression\":null}")]
    public void FullCodeOrAuthorityShapedRepliesRejected(string text) => Assert.False(TutoringSession.Parse(text).Ok);
    [Fact]
    public async Task TheoryExplainsHeadersWithCodeWhileLabKeepsHintBoundary()
    {
        var draft = await LabFixtureProvider.SeedAsync(root);
        var reply = JsonSerializer.Serialize(new { hint = "头文件保存声明，通过 #include 引入。\n```c\n#include <stdio.h>\nint main(void) { puts(\"hello\"); }\n```", expression = (string?)null });
        var provider = new Provider { Reply = reply }; using var session = Tutor(provider);
        var review = await session.PrepareAsync(draft.Id, "T3", "C语言头文件是什么？", "");
        Assert.Contains(review.Sources, s => s.Id == "tutor-policy" && s.Text == TutoringSession.TheoryPrompt);
        Assert.True((await session.SendAsync(true)).Ok);
        Assert.Contains("#include", session.Messages.Last().Text);
        review = await session.PrepareAsync(draft.Id, "L1", "给我完整实验答案", "");
        Assert.Contains(review.Sources, s => s.Id == "tutor-policy" && s.Text == TutoringSession.Prompt);
        Assert.False((await session.SendAsync(true)).Ok);
        Assert.Empty(session.Messages);
    }
    [Theory]
    [InlineData("{\"hint\":\"answer\",\"expression\":null,\"completed\":true}")]
    [InlineData("{\"hint\":\"answer\",\"hint\":\"duplicate\",\"expression\":null}")]
    public void TheoryStillRejectsInvalidReplyShape(string text) => Assert.False(TutoringSession.Parse(text, true).Ok);
    [Fact]
    public async Task SelectedFileReadOnlyNoDirectoryScanAndLimits()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "chosen.c");
        await File.WriteAllTextAsync(path, "/* TODO */"); var before = await File.ReadAllBytesAsync(path);
        var files = new TutoringFiles(); Assert.Equal("/* TODO */", files.ReadSelected(HostSelection.FromFilePicker(path)));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Throws<IOException>(() => files.ReadSelected(HostSelection.FromFolderPicker(root)));
        await File.WriteAllTextAsync(path, new string('x', 16001)); Assert.Throws<IOException>(() => files.ReadSelected(HostSelection.FromFilePicker(path)));
        var draft = await LabFixtureProvider.SeedAsync(root); using var tutor = Tutor(new Provider());
        await Assert.ThrowsAsync<ArgumentException>(() => tutor.PrepareAsync(draft.Id, "L1", new string('x', 4001), ""));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
