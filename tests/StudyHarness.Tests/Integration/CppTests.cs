using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class CppTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-cpp", Guid.NewGuid().ToString("N"));
    private static readonly PlanningInput Input = new("C++", "基础", "算法", 120, "CppAlgorithms", []);
    private static readonly PlanSource[] Sources = [new("s1", new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new('A', 64), new("synthetic", null, null, "1")), true)];
    private static bool Valid(PlanCandidate plan) => PlanRules.Validate(Guid.NewGuid(), Input, plan, Sources, null).Ok;
    [Fact]
    public void MultiAlgorithmTheory_AllowsOptionalLabsButChecksScheduledDependencies()
    {
        var plan = CppFixture.Candidate(Input); Assert.True(Valid(plan));
        Assert.True(new PlanParser().Parse(JsonSerializer.Serialize(plan, Wire.Options)).Ok);
        Assert.True(Valid(plan with { Nodes = plan.Nodes.Take(2).ToArray() }));
        Assert.False(Valid(plan with { Algorithms = null }));
        Assert.False(Valid(plan with { Algorithms = [new("a1", "同名"), new("a2", "同名")] }));
        Assert.False(Valid(plan with { Nodes = plan.Nodes.Select(n => n.Key == "n2" ? n with { AlgorithmKeys = ["unknown"] } : n).ToArray() }));
        Assert.False(Valid(plan with { Nodes = plan.Nodes.Select(n => n.Key == "n2" ? n with { PrerequisiteKeys = [] } : n).ToArray() }));
        Assert.False(Valid(plan with { Nodes = plan.Nodes.Select(n => n.Key == "n2" ? n with { Lab = n.Lab! with { Toolchain = "portable-c" } } : n).ToArray() }));
        Assert.Contains("a2 · 前缀和 → 理论：n1 → 实验题：n3", PlanRules.AlgorithmCoverage(plan));
    }
    [Fact]
    public void LegacyPlanSerializationDoesNotAddNullFields()
    {
        var plan = CppFixture.Candidate(Input) with { Algorithms = null, Nodes = [CppFixture.Candidate(Input).Nodes[0] with { AlgorithmKeys = null }] };
        var json = JsonSerializer.Serialize(plan, Wire.Options);
        Assert.DoesNotContain("algorithms", json); Assert.DoesNotContain("algorithmKeys", json);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<PlanCandidate>(json, Wire.Options), Wire.Options));
    }
    [Fact]
    public void SingleFileStdioIsExercise_HostHarnessHasOneEntryAndAllCases()
    {
        var result = LabMaterialRules.Parse(JsonSerializer.Serialize(CppFixture.RawMaterial, Wire.Options), ["s1"]);
        Assert.True(result.Ok, result.Error?.Message); var m = result.Value!;
        Assert.Equal("exercise", m.ExperimentKind); Assert.Single(m.SourceFiles);
        Assert.Equal(CppLabRules.TestSource(m.Cases), m.TestFiles.Single(f => f.Path == "test.cpp").Text);
        Assert.Contains("#include \"solution.cpp\"", m.TestFiles[1].Text);
        Assert.Equal(m.Cases.Count, m.TestFiles[1].Text.Split("assert(out.str()").Length - 1);
        Assert.True(LabMaterialRules.Validate(m, ["s1"]).Ok);
        var schema = Json.Schema.JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", "lab-material.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(m, Wire.Options)).IsValid);
        Assert.False(LabMaterialRules.Validate(m with { ExperimentKind = "project" }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { SchemaVersion = 3 }, ["s1"]).Ok);
        Assert.False(schema.Evaluate(JsonSerializer.SerializeToNode(m with { SchemaVersion = 3 }, Wire.Options)).IsValid);
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [m.TestFiles[1], m.TestFiles[1]] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [m.TestFiles[0], m.TestFiles[1] with { Text = m.TestFiles[1].Text.Replace("solve(in, out);", "") }] }, ["s1"]).Ok);
    }
    [Theory]
    [InlineData("#include <fstream>\n")]
    [InlineData("#include <bits/stdc++.h>\n")]
    [InlineData("#include <thread>\n")]
    [InlineData("#define NDEBUG\n")]
    [InlineData("#define solve fake\n")]
    [InlineData("int main() {}\n")]
    [InlineData("#include \"secret.cpp\"\n")]
    [InlineData("void evil(){system(\"cmd\");}\n")]
    public void CppForbiddenContentIsRejected(string prefix)
    {
        var raw = CppFixture.RawMaterial;
        Assert.False(LabMaterialRules.Parse(JsonSerializer.Serialize(raw with { SourceFiles = [raw.SourceFiles[0] with { Text = prefix + raw.SourceFiles[0].Text }] }, Wire.Options), ["s1"]).Ok);
    }
    [Fact]
    public void CppNullFileAndInconsistentCasesReturnModelError()
    {
        var raw = CppFixture.RawMaterial;
        var missing = LabMaterialRules.Parse(JsonSerializer.Serialize(raw with { TestFiles = [null!] }, Wire.Options), ["s1"]);
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, missing.Error!.Code);
        var mismatch = LabMaterialRules.Parse(JsonSerializer.Serialize(raw with { TestFiles = [new("tests/cases.json", "[]")] }, Wire.Options), ["s1"]);
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, mismatch.Error!.Code);
    }
    [Fact]
    public async Task FrozenCppPlanRestartsAndLabCompletesWithoutTheory()
    {
        var draft = await CppFixture.SeedAsync(root); var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans);
        var reread = (await plans.GetAsync(draft.Id))!;
        Assert.Equal(draft.Plan!.Hash, reread.Plan!.Hash); Assert.True(reread.Confirmed);
        var service = new LabService(plans, learning, learning, new KnowledgeStore(root), new(new SqliteSettingsStore(root)), new(new CppFixture()), new LabZipExporter());
        Assert.Empty(await learning.CompletedNodesAsync(draft.Id));
        var generated = await service.GenerateAsync(draft.Id, "n2"); Assert.True(generated.Ok, generated.Error?.Message);
        Assert.Equal(4, generated.Value!.Material.SchemaVersion);
        var export = Path.Combine(root, "cpp.zip"); Assert.True((await service.ExportAsync(draft.Id, "n2", export)).Ok);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(export))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "files/solution.cpp");
            Assert.Contains(zip.Entries, e => e.FullName == "files/test.cpp");
        }
        Assert.DoesNotContain("n2", await learning.CompletedNodesAsync(draft.Id));
        Assert.False((await service.ConfirmAsync(draft.Id, "n2", generated.Value.Hash, false)).Ok);
        Assert.True((await service.ConfirmAsync(draft.Id, "n2", generated.Value.Hash, true)).Ok);
        Assert.Equal(new[] { "n2" }, await learning.CompletedNodesAsync(draft.Id));
        Assert.Equal(generated.Value.Hash, (await new SqliteLearningStore(root, plans).GetLabAsync(draft.Id, "n2"))!.Hash);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
