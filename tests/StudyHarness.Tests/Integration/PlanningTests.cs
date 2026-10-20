using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class PlanningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-planning", Guid.NewGuid().ToString());
    private static PlanningInput Input(int? minutes = 120, string scope = "Math") => new("学习数学", "零基础", "理解集合", minutes, scope, []);
    private static PlanCandidate Candidate(int minutes = 120) => new("数学", "零基础", "理解集合", minutes, "Math", [], [], [], ["s1"],
        Enumerable.Range(1, 6).Select(i => new PlannedNode($"n{i}", $"主题{i}", "theory", ["解释集合"], i == 1 ? [] : [$"n{i - 1}"], 60, null)).ToArray());
    private static readonly PlanSource[] Sources = [new("s1", new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), new("test.md", null, "集合", "1")), false)];
    private SqlitePlanningStore Store() => new(root);
    private async Task<PlanningDraft> Started(PlanningInput? input = null) => (await Store().StartAsync(new(Guid.NewGuid(), 0, input ?? Input()))).Value!;
    private static PlanDocument Document(PlanningDraft draft, PlanCandidate? candidate = null) => PlanRules.Validate(draft.Id, draft.Input, candidate ?? Candidate(), Sources, null).Value!;

    [Fact]
    public async Task ProposalAndFreeze_SurviveRestart_RejectEverySubsequentWrite()
    {
        var draft = await Started(); var store = Store(); var doc = Document(draft);
        var proposed = await store.SaveProposalAsync(new(Guid.NewGuid(), draft.Revision, doc), draft.Id);
        Assert.True(proposed.Ok);
        var confirm = new Command<ConfirmPlan>(Guid.NewGuid(), proposed.Value!.Revision, new(draft.Id, doc.Hash, true));
        var frozen = await store.ConfirmAsync(confirm); Assert.True(frozen.Ok);
        Assert.True((await Store().GetAsync(draft.Id))!.Confirmed);
        Assert.Equal(doc.Hash, (await Store().GetAsync(draft.Id))!.Plan!.Hash);
        Assert.Equal(frozen.Value!.ConfirmedAt, (await Store().ConfirmAsync(confirm)).Value!.ConfirmedAt);
        Assert.Equal(ErrorCode.PLAN_FROZEN, (await store.ReviseAsync(new(Guid.NewGuid(), frozen.Value.Revision, new(draft.Id, Input(480))))).Error!.Code);
        Assert.Equal(ErrorCode.PLAN_FROZEN, (await store.SaveProposalAsync(new(Guid.NewGuid(), frozen.Value.Revision, doc), draft.Id)).Error!.Code);
        Assert.Equal(ErrorCode.PLAN_FROZEN, (await store.ConfirmAsync(confirm with { RequestId = Guid.NewGuid() })).Error!.Code);
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "planning.db")}"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM outbox"; Assert.Equal(1L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE '%lesson%'"; Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task RevisionHashAndAcknowledgement_CannotBeBypassed_ReviseInvalidatesProposal()
    {
        var draft = await Started(); var doc = Document(draft); var store = Store();
        var proposed = (await store.SaveProposalAsync(new(Guid.NewGuid(), 0, doc), draft.Id)).Value!;
        Assert.Equal(ErrorCode.REVISION_CONFLICT, (await store.ConfirmAsync(new(Guid.NewGuid(), 0, new(draft.Id, doc.Hash, true)))).Error!.Code);
        Assert.False((await store.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, "stale", true)))).Ok);
        Assert.False((await store.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, doc.Hash, false)))).Ok);
        var revised = (await store.ReviseAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, Input(480))))).Value!;
        Assert.Null(revised.Plan);
        Assert.False((await store.ConfirmAsync(new(Guid.NewGuid(), revised.Revision, new(draft.Id, doc.Hash, true)))).Ok);
        Assert.Equal(ErrorCode.REVISION_CONFLICT, (await store.SaveProposalAsync(new(Guid.NewGuid(), proposed.Revision, doc), draft.Id)).Error!.Code);
    }

    [Fact]
    public async Task IdempotencyAndMultipleSubjects_AreIsolated()
    {
        var command = new Command<PlanningInput>(Guid.NewGuid(), 0, Input());
        var a = (await Store().StartAsync(command)).Value!;
        Assert.Equal(a.Id, (await Store().StartAsync(command)).Value!.Id);
        Assert.Equal(ErrorCode.IDEMPOTENCY_CONFLICT, (await Store().StartAsync(command with { Payload = Input(480) })).Error!.Code);
        var b = await Started(Input(480)); Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, (await Store().ListAsync()).Count);
        Assert.Equal(120, (await Store().GetAsync(a.Id))!.Input.WeeklyMinutes);
    }

    [Fact]
    public async Task ConcurrentRevisions_OnlyOneCanWin()
    {
        var draft = await Started();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            Store().ReviseAsync(new(Guid.NewGuid(), 0, new(draft.Id, Input(180 + i)))))));
        Assert.Single(results, r => r.Ok);
        Assert.Equal(3, results.Count(r => r.Error?.Code == ErrorCode.REVISION_CONFLICT));
    }

    [Fact]
    public void WeeklyScheduling_DiffersForTwoAndEightHours_PreservesDurationsAndDependencies()
    {
        var id = Guid.NewGuid();
        var slow = PlanRules.Validate(id, Input(120), Candidate(120), Sources, null).Value!;
        var fast = PlanRules.Validate(id, Input(480), Candidate(480), Sources, null).Value!;
        Assert.Equal(3, slow.Weeks.Count); Assert.Single(fast.Weeks);
        Assert.All(slow.Weeks, w => Assert.InRange(w.EstimatedMinutes, 1, 120));
        Assert.Equal(360, fast.Weeks.Single().EstimatedMinutes);
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"n{i}"), slow.Weeks.SelectMany(w => w.NodeKeys));
        Assert.NotEqual(slow.Hash, fast.Hash);
    }

    [Theory]
    [InlineData("Math", false, true)]
    [InlineData("Math", true, false)]
    [InlineData("Concepts", false, true)]
    [InlineData("SimpleC", false, false)]
    [InlineData("SimpleC", true, true)]
    [InlineData("OutOfScope", false, false)]
    public void ScopeAndPracticeRules_AreDeterministic(string scope, bool lab, bool accepted)
    {
        var plan = Candidate() with { Scope = scope };
        if (lab) plan = plan with { Nodes = [new("n1", "C实验", "lab", ["运行C"], [], 60, new("independent", null, "mingw", ["s1"], "素材适合简单C"))] };
        Assert.Equal(accepted, PlanRules.Validate(Guid.NewGuid(), Input(scope: scope), plan, Sources, null).Ok);
    }

    [Fact]
    public async Task UnresolvedScopeCannotConfirm_AndZeroTimeCannotPropose()
    {
        var draft = await Started(Input(scope: "OutOfScope"));
        var candidate = Candidate() with { Scope = "OutOfScope", Limitations = ["仅支持简单C"], Clarifications = ["请调整Qt实践目标"] };
        var doc = Document(draft, candidate);
        var saved = await Store().SaveProposalAsync(new(Guid.NewGuid(), 0, doc), draft.Id); Assert.True(saved.Ok);
        Assert.False((await Store().ConfirmAsync(new(Guid.NewGuid(), saved.Value!.Revision, new(draft.Id, doc.Hash, true)))).Ok);
        Assert.False(PlanRules.Validate(Guid.NewGuid(), Input(0), Candidate(), Sources, null).Ok);
    }

    [Fact]
    public void KnownFieldsDefaultsAndSources_CannotBeSilentlyChanged()
    {
        var id = Guid.NewGuid(); var p = Candidate();
        Assert.False(PlanRules.Validate(id, Input(), p with { Background = "熟练" }, Sources, null).Ok);
        Assert.False(PlanRules.Validate(id, Input(), p with { Goal = "different" }, Sources, null).Ok);
        Assert.False(PlanRules.Validate(id, Input(), p with { Defaults = [new("background", "零基础", "guess")] }, Sources, null).Ok);
        Assert.False(PlanRules.Validate(id, Input(), p with { SourceKeys = ["s2"] }, Sources, null).Ok);
        var unknown = new PlanningInput("学习数学", null, null, null, "Auto", []);
        Assert.True(PlanRules.Validate(id, unknown, p with { Defaults = [new("background", p.Background, "未提供"), new("goal", p.Goal, "细化目标"), new("weeklyMinutes", "120", "未提供")] }, Sources, null).Ok);
    }

    [Fact]
    public void ModelSchema_RejectsBodyStatusNullAndInvalidTypes()
    {
        var parser = new PlanParser(); var text = JsonSerializer.Serialize(Candidate(), Wire.Options);
        Assert.True(parser.Parse(text).Ok);
        foreach (var field in new[] { "body", "completed", "path", "weeks" })
        {
            var node = JsonNode.Parse(text)!; node[field] = "injection"; Assert.False(parser.Parse(node.ToJsonString()).Ok);
        }
        Assert.False(parser.Parse("null").Ok);
        var invalid = JsonNode.Parse(text)!; invalid["nodes"]![0]!["type"] = "quiz"; Assert.False(parser.Parse(invalid.ToJsonString()).Ok);
        invalid = JsonNode.Parse(text)!; invalid["nodes"]![0]!["title"] = " "; Assert.False(parser.Parse(invalid.ToJsonString()).Ok);
    }

    [Fact]
    public void ContinuousLabs_RequireMatchingPredecessorAndMaterialPriority()
    {
        var plan = Candidate() with { Scope = "SimpleC", Nodes = [
            new("n1", "实验1", "lab", ["构建"], [], 30, new("continuous", null, "msvc", ["s1"], "来源适配")),
            new("n2", "实验2", "lab", ["扩展"], ["n1"], 30, new("continuous", "n1", "msvc", ["s1"], "继承当前代码"))] };
        Assert.True(PlanRules.Validate(Guid.NewGuid(), Input(scope: "SimpleC"), plan, Sources, null).Ok);
        var wrong = plan with { Nodes = [plan.Nodes[0], plan.Nodes[1] with { PrerequisiteKeys = [] }] };
        Assert.False(PlanRules.Validate(Guid.NewGuid(), Input(scope: "SimpleC"), wrong, Sources, null).Ok);
        var preferred = Sources.Concat([Sources[0] with { Key = "s2", UserPreferred = true }]).ToArray();
        Assert.False(PlanRules.Validate(Guid.NewGuid(), Input(scope: "SimpleC"), plan, preferred, null).Ok);
    }

    [Theory]
    [InlineData("independent")]
    [InlineData("continuous")]
    public async Task ContinuousSuccessor_CanInheritEitherStartingMode_AndFreeze(string startingMode)
    {
        var draft = await Started(Input(scope: "SimpleC"));
        var plan = Candidate() with { Scope = "SimpleC", Nodes = [
            new("start", "项目起点", "lab", ["创建程序"], [], 30, new(startingMode, null, "mingw", ["s1"], "项目起点")),
            new("next", "项目扩展", "lab", ["扩展程序"], ["start"], 30, new("continuous", "start", "mingw", ["s1"], "继承当前代码"))] };
        var validation = PlanRules.Validate(draft.Id, draft.Input, plan, Sources, null);
        Assert.True(validation.Ok, validation.Error?.Message);
        var saved = await Store().SaveProposalAsync(new(Guid.NewGuid(), 0, validation.Value!), draft.Id);
        Assert.True(saved.Ok);
        Assert.True((await Store().ConfirmAsync(new(Guid.NewGuid(), saved.Value!.Revision, new(draft.Id, validation.Value!.Hash, true)))).Ok);
        Assert.Equal("start", (await Store().GetAsync(draft.Id))!.Plan!.Candidate.Nodes[1].Lab!.PreviousLabKey);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("theory")]
    [InlineData("indirect")]
    [InlineData("toolchain")]
    [InlineData("independent")]
    [InlineData("cycle")]
    public void InvalidInheritance_RemainsRejected(string defect)
    {
        var start = new PlannedNode("start", "起点", "lab", ["创建"], [], 30, new("independent", null, "mingw", ["s1"], "素材"));
        var next = new PlannedNode("next", "后课", "lab", ["扩展"], ["start"], 30, new("continuous", "start", "mingw", ["s1"], "继承"));
        var bridge = new PlannedNode("bridge", "理论", "theory", ["解释"], ["start"], 30, null);
        switch (defect)
        {
            case "missing": next = next with { Lab = next.Lab! with { PreviousLabKey = "absent" } }; break;
            case "theory": start = start with { Type = "theory", Lab = null }; break;
            case "indirect": next = next with { PrerequisiteKeys = ["bridge"] }; break;
            case "toolchain": next = next with { Lab = next.Lab! with { Toolchain = "msvc" } }; break;
            case "independent": next = next with { Lab = next.Lab! with { Mode = "independent" } }; break;
            case "cycle": start = start with { PrerequisiteKeys = ["next"] }; break;
        }
        var plan = Candidate() with { Scope = "SimpleC", Nodes = [start, bridge, next] };
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, PlanRules.Validate(Guid.NewGuid(), Input(scope: "SimpleC"), plan, Sources, null).Error!.Code);
    }

    [Fact]
    public void H2Defaults_StrictTypesAndSafeFieldDiagnostics()
    {
        var plan = Candidate() with { Defaults = [new("background", "零基础", "未提供"), new("goal", "理解集合", "细化"), new("weeklyMinutes", "120", "未提供")] };
        var text = JsonSerializer.Serialize(plan, Wire.Options); var parser = new PlanParser();
        Assert.True(parser.Parse(text).Ok);
        var node = JsonNode.Parse(text)!;
        node["defaults"]![2]!["value"] = 120;
        var result = parser.Parse(node.ToJsonString());
        Assert.Contains("$.defaults[2].value [type]", result.Error!.Message);
        node = JsonNode.Parse(text)!; node["weeklyMinutes"] = "120";
        Assert.Contains("$.weeklyMinutes [type]", parser.Parse(node.ToJsonString()).Error!.Message);
        node = JsonNode.Parse(text)!; node["defaults"]![0]!["field"] = "private-model-text";
        result = parser.Parse(node.ToJsonString());
        Assert.Contains("$.defaults[0].field [enum]", result.Error!.Message);
        Assert.DoesNotContain("private-model-text", result.Error.Message);
        node = JsonNode.Parse(text)!; node["private-property"] = "private-value";
        result = parser.Parse(node.ToJsonString());
        Assert.Contains("additionalProperties", result.Error!.Message);
        Assert.DoesNotContain("private-property", result.Error.Message);
        Assert.DoesNotContain("private-value", result.Error.Message);
        Assert.Contains("不是可读取", parser.Parse("not-json").Error!.Message);
    }

    [Fact]
    public void ReviewedPromptContainsExactlyTheValidatorSchema()
    {
        using var stream = typeof(PlanParser).Assembly.GetManifestResourceStream("plan.schema.json")!;
        using var reader = new StreamReader(stream);
        Assert.EndsWith(reader.ReadToEnd(), PlanningPrompt.Text);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
