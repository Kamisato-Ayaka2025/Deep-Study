using System.IO.Compression;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class LabMaterialTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-materials", Guid.NewGuid().ToString("N"));
    private SqlitePlanningStore Plans => new(root);
    private SqliteLearningStore Store => new(root, Plans);
    private LabService Service(IModelProvider? provider = null) => new(Plans, Store, Store, new KnowledgeStore(root), new(new SqliteSettingsStore(root)), new(provider ?? new LabFixtureProvider()), new LabZipExporter());
    private sealed class Provider(Func<CancellationToken, Task<Result<ProviderReply>>> send) : IModelProvider
    {
        public int Calls;
        public string Destination => "local:deterministic-fake";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct) { Calls++; Assert.All(request.Sources, s => Assert.True(s.Kind is ModelSourceKind.UserText or ModelSourceKind.OriginalMaterial)); return send(ct); }
    }
    private static Result<ProviderReply> Reply(LabMaterial? material = null) => Result<ProviderReply>.Success(new(JsonSerializer.Serialize(material ?? LabFixtureProvider.Material, Wire.Options), true));
    [Theory]
    [InlineData("../escape.c")] [InlineData("C:/escape.c")] [InlineData("a\\b.c")] [InlineData("con.c")]
    [InlineData("a.c:ads")] [InlineData("run.ps1")] [InlineData("main.exe")] [InlineData("dir./main.c")] [InlineData("tests/notes.txt")]
    public void UnsafeNamesRejected(string name) => Assert.False(LabMaterialRules.Validate(LabFixtureProvider.Material with { SourceFiles = [new(name, "/* TODO */")] }, ["s1"]).Ok);
    [Theory]
    [InlineData("system(\"cmd\");")] [InlineData("fopen(\"secret\",\"r\");")] [InlineData("socket(1,2,3);")]
    [InlineData("tmpfile();")] [InlineData("tmpnam(0);")] [InlineData("tempnam(0,0);")]
    [InlineData("#include <windows.h>\n")] [InlineData("#pragma comment(lib, \"x\")\n")]
    public void DangerousCodeRejected(string code) => Assert.False(LabMaterialRules.Validate(LabFixtureProvider.Material with { SourceFiles = [new("main.c", code + "\n/* TODO */")] }, ["s1"]).Ok);
    [Fact]
    public void DuplicateAliasesPrefixOversizeUnknownSourceAndHiddenReferenceRejected()
    {
        var m = LabFixtureProvider.Material;
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [new("MAIN.c", "x")] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [new("main.c/test.txt", "x")] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { Instructions = new('x', 32001) }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m, ["s2"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { SourceFiles = [new("main.c", "int main(){char *s=\"TODO\";}")] }, ["s1"]).Ok);
        var json = JsonSerializer.Serialize(m, Wire.Options);
        Assert.False(LabMaterialRules.Parse(json[..^1] + ",\"referenceFiles\":[]}", ["s1"]).Ok);
        Assert.False(LabMaterialRules.Parse(json.Replace("\"schemaVersion\":3,", "").Replace(",\"schemaVersion\":3", ""), ["s1"]).Ok);
        Assert.False(LabMaterialRules.Parse(json.Replace("\"path\":\"main.c\"", "\"path\":\"hidden.c\",\"path\":\"main.c\""), ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [new("tests/cases.json", "[]")] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { SourceFiles = [new("main.c", "#include \"../outside.h\"\n/* TODO */")] }, ["s1"]).Ok);
    }
    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(20, true)] [InlineData(21, false)]
    public void CaseCountBoundaries(int count, bool expected)
    {
        var cases = Enumerable.Range(0, count).Select(i => new LabCase("c" + i, "1 2\n", "3\n")).ToArray();
        Assert.Equal(expected, LabMaterialRules.Validate(LabFixtureProvider.Material with { Cases = cases, TestFiles = [new("tests/cases.json", JsonSerializer.Serialize(cases, Wire.Options)), LabFixtureProvider.Material.TestFiles[1]] }, ["s1"]).Ok);
    }
    [Fact]
    public async Task GenerateExportConfirmRestartAndDependency_NoExecutionOrAutomaticCompletion()
    {
        var plan = await LabFixtureProvider.SeedAsync(root); var provider = new LabFixtureProvider(); var service = Service(provider);
        Assert.True((await service.GenerateAsync(plan.Id, "L2")).Ok);
        Assert.Equal(1, provider.Calls);
        var generated = await service.GenerateAsync(plan.Id, "L1"); Assert.True(generated.Ok, generated.Error?.Message);
        var package = generated.Value!; Assert.Empty(await Store.CompletedNodesAsync(plan.Id));
        Assert.True((await service.GenerateAsync(plan.Id, "L1")).Ok); Assert.Equal(2, provider.Calls);
        var path = Path.Combine(root, "export.zip"); Assert.True((await service.ExportAsync(plan.Id, "L1", path)).Ok);
        var before = await File.ReadAllBytesAsync(path); Assert.False((await service.ExportAsync(plan.Id, "L1", path)).Ok); Assert.Equal(before, await File.ReadAllBytesAsync(path));
        using (var zip = ZipFile.OpenRead(path))
        { Assert.Contains(zip.Entries, e => e.FullName == "files/main.c"); Assert.Contains(zip.Entries, e => e.FullName == "files/tests/cases.json"); Assert.Contains(zip.Entries, e => e.FullName == "README.md"); Assert.Contains(zip.Entries, e => e.FullName == "files/test.c"); Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".exe") || e.FullName.EndsWith(".txt")); }
        Assert.Empty(await Store.CompletedNodesAsync(plan.Id));
        Assert.False((await service.ConfirmAsync(plan.Id, "L1", package.Hash, false)).Ok);
        Assert.False((await service.ConfirmAsync(plan.Id, "L1", "wrong", true)).Ok);
        var attested = await service.ConfirmAsync(plan.Id, "L1", package.Hash, true); Assert.True(attested.Ok);
        Assert.Equal("userAttested", attested.Value!.CompletionKind);
        Assert.Equal(attested.Value, (await service.ConfirmAsync(plan.Id, "L1", package.Hash, true)).Value);
        Assert.Single(await Store.PendingEventsAsync()); Assert.Contains("L1", await Store.CompletedNodesAsync(plan.Id));
        Assert.Equal(package.Hash, (await Store.GetLabAsync(plan.Id, "L1"))!.Hash);
        Assert.True((await Service().GenerateAsync(plan.Id, "L2")).Ok);
        Assert.DoesNotContain("L2", await Store.CompletedNodesAsync(plan.Id));
    }
    [Fact]
    public async Task CancellationWithLateSuccessCannotPublish_ConcurrentRequestDoesNotDispatch()
    {
        var plan = await LabFixtureProvider.SeedAsync(root);
        var arrived = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var provider = new Provider(async _ => { arrived.SetResult(); await release.Task; return Reply(); });
        var service = Service(provider); using var ct = new CancellationTokenSource();
        var first = service.GenerateAsync(plan.Id, "L1", ct.Token); await arrived.Task;
        Assert.Equal(ErrorCode.TASK_ACTIVE, (await service.GenerateAsync(plan.Id, "L1")).Error!.Code);
        ct.Cancel(); release.SetResult(); Assert.Equal(ErrorCode.RUN_CANCELLED, (await first).Error!.Code);
        Assert.Null(await Store.GetLabAsync(plan.Id, "L1")); Assert.Equal("cancelled", (await Store.GetLabJobAsync(plan.Id, "L1"))!.Status);
        Assert.Equal(1, provider.Calls); Assert.Empty(await Store.CompletedNodesAsync(plan.Id));
    }
    [Theory]
    [InlineData(ErrorCode.AUTH_INVALID, 1)] [InlineData(ErrorCode.TRANSIENT_PROVIDER_ERROR, 3)]
    public async Task ErrorsHaveBoundedRetries(ErrorCode code, int attempts)
    {
        var plan = await LabFixtureProvider.SeedAsync(root); var provider = new Provider(_ => Task.FromResult(Result<ProviderReply>.Fail(code, "synthetic")));
        Assert.False((await Service(provider).GenerateAsync(plan.Id, "L1")).Ok); Assert.Equal(attempts, provider.Calls);
        Assert.Equal(attempts, (await Store.GetLabJobAsync(plan.Id, "L1"))!.Attempts); Assert.Null(await Store.GetLabAsync(plan.Id, "L1"));
    }
    [Fact]
    public async Task InvalidModelOutputAndCrashRecoveryDoNotPublishOrAutoRetry()
    {
        var plan = await LabFixtureProvider.SeedAsync(root);
        var provider = new Provider(_ => Task.FromResult(Reply(LabFixtureProvider.Material with { Cases = [] })));
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, (await Service(provider).GenerateAsync(plan.Id, "L1")).Error!.Code); Assert.Equal(1, provider.Calls);
        var job = (await Store.BeginLabAsync(plan.Id, "L1")).Value!; await Store.RecoverLabsAsync();
        Assert.Equal("failed", (await Store.GetLabJobAsync(plan.Id, "L1"))!.Status);
        Assert.False((await Store.PublishLabAsync(job, LabFixtureProvider.Material, plan.Plan!.Hash, plan.Plan.Sources)).Ok);
        Assert.Null(await Store.GetLabAsync(plan.Id, "L1"));
    }
    [Fact]
    public void ClassifiedMaterialsRequireRunnableTestOrObservableProjectSteps()
    {
        var m = LabFixtureProvider.Material;
        Assert.True(LabMaterialRules.Parse(JsonSerializer.Serialize(m, Wire.Options), ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [m.TestFiles[0]] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { ExperimentKind = "other" }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { ClassificationReason = "" }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [..m.TestFiles, new("notes.txt", "text")] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [m.TestFiles[0], new("test.c", "/* assert(1); main() */")] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(m with { TestFiles = [m.TestFiles[0], new("test.c", "#define NDEBUG\n" + m.TestFiles[1].Text)] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Parse(JsonSerializer.Serialize(m, Wire.Options).Replace("\"schemaVersion\":3", "\"schemaVersion\":\"3\""), ["s1"]).Ok);
        var project = m with { ExperimentKind = "project", TestFiles = [m.TestFiles[0]], ManualChecks = [new("step1", "启动控制台，输入1和2", "显示3")] };
        Assert.True(LabMaterialRules.Validate(project, ["s1"]).Ok);
        Assert.Contains("输入1和2", LabMaterialRules.AssessmentOverview(project));
        Assert.False(LabMaterialRules.Validate(project with { ManualChecks = [] }, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Validate(project with { ManualChecks = [new("step1", "", "3")] }, ["s1"]).Ok);
        var legacy = m with { SchemaVersion = 2, ExperimentKind = null, ClassificationReason = null, ManualChecks = null };
        Assert.True(LabMaterialRules.Validate(legacy, ["s1"]).Ok);
        Assert.False(LabMaterialRules.Parse(JsonSerializer.Serialize(legacy, Wire.Options), ["s1"]).Ok);
    }
    [Fact]
    public async Task LegacyMaterialDeserializesAndExportsMarkdownWithoutChangingOriginal()
    {
        Directory.CreateDirectory(root);
        var node = JsonSerializer.SerializeToNode(LabFixtureProvider.Material, Wire.Options)!;
        node["schemaVersion"] = 2;
        foreach (var key in new[] { "experimentKind", "classificationReason", "manualChecks" }) node.AsObject().Remove(key);
        var old = JsonSerializer.Deserialize<LabMaterial>(node.ToJsonString(), Wire.Options)!;
        old = old with { TestFiles = [..old.TestFiles, new("notes.txt", "old text")] };
        var draft = await LabFixtureProvider.SeedAsync(root);
        var package = new LabPackage(draft.Id, "L1", draft.Plan!.Hash, "original-hash", old, draft.Plan.Sources, DateTimeOffset.UtcNow);
        var path = Path.Combine(root, "legacy.zip");
        Assert.True((await new LabZipExporter().ExportAsync(package, path)).Ok);
        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.FullName == "files/notes.txt.md");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".txt"));
        using var reader = new StreamReader(zip.GetEntry("README.md")!.Open());
        var readme = await reader.ReadToEndAsync();
        Assert.StartsWith("# 实验材料", readme); Assert.Contains("旧版材料", readme); Assert.Contains("original-hash", readme);
        Assert.Contains(old.TestFiles, f => f.Path == "notes.txt");
        var collision = package with { Material = old with { TestFiles = [..old.TestFiles, new("notes.txt.md", "other")] } };
        Assert.False((await new LabZipExporter().ExportAsync(collision, Path.Combine(root, "collision.zip"))).Ok);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
