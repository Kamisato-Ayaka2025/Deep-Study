using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class GeneralPracticeTests
{
    private static GeneralPractice Proof => new("exercise", "paper-exercise", "证明奇数之和",
        ["用归纳法证明求和公式"], ["理解数学归纳法"],
        [new("T1", "证明前n个正奇数的和为n²", "n是正整数", "写出基例和归纳步骤", "n=1成立；假设和为k²，加入2k+1得到(k+1)²")],
        "核对基例、假设、推导，缺一步不得确认完成。", ["s1"]);
    private static GeneralPractice Network => new("project", "manual-project", "观察DNS查询",
        ["区分DNS查询与响应"], ["外部已准备好只含自己测试流量的抓包文件"],
        [new("T1", "在外部抓包查看器中选择一组DNS查询与响应", "你自己的测试抓包，过滤DNS", "同一事务ID的查询与响应", "记录查询名、事务ID、返回地址；响应匹配且解释正确")],
        "自行记录观察，不上传抓包；逐条核对后再明确确认。", ["s1"]);

    [Fact]
    public void ProofAndNetwork_AreIndependentSchema5Materials()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", "general-practice.schema.json"));
        foreach (var material in new[] { Proof, Network })
        {
            var text = JsonSerializer.Serialize(material, Wire.Options);
            Assert.True(schema.Evaluate(JsonNode.Parse(text)).IsValid);
            Assert.True(GeneralPracticeRules.Parse(text, ["s1"]).Ok);
            Assert.Contains(material.Tasks[0].Acceptance, GeneralPracticeRules.Markdown(material, ["s1"]));
            Assert.False(LabMaterialRules.Parse(text, ["s1"]).Ok); // Never enters a relaxed C validator.
        }
    }

    [Theory]
    [InlineData("script")] [InlineData("sourceFiles")] [InlineData("referenceFiles")]
    [InlineData("completed")] [InlineData("apiKey")] [InlineData("path")]
    public void UnknownExecutableOrCompletionFields_AreRejected(string property)
    {
        var json = JsonSerializer.SerializeToNode(Proof, Wire.Options)!; json[property] = "untrusted";
        Assert.False(GeneralPracticeRules.Parse(json.ToJsonString(), ["s1"]).Ok);
    }

    [Fact]
    public void EmptyKnownSupplement_IsMechanicalOnly_NonemptyAndUnknownFieldsStillReject()
    {
        var json = JsonSerializer.SerializeToNode(Proof, Wire.Options)!;
        var task = json["tasks"]![0]!;
        task["expectedObservation补充"] = "";
        var parsed = GeneralPracticeRules.Parse(json.ToJsonString(), ["s1"]);
        Assert.True(parsed.Ok);
        Assert.Equal(JsonSerializer.Serialize(Proof, Wire.Options), JsonSerializer.Serialize(parsed.Value, Wire.Options));
        task["expectedObservation补充"] = "不能丢弃的额外内容";
        Assert.False(GeneralPracticeRules.Parse(json.ToJsonString(), ["s1"]).Ok);
        task.AsObject().Remove("expectedObservation补充"); task["script补充"] = "";
        Assert.False(GeneralPracticeRules.Parse(json.ToJsonString(), ["s1"]).Ok);
    }

    [Fact]
    public void MissingDuplicateNullAndUnknownSources_RejectWithoutInventingAnswers()
    {
        Assert.False(GeneralPracticeRules.Validate(Proof with { Sources = ["invented"] }, ["s1"]).Ok);
        Assert.False(GeneralPracticeRules.Validate(Proof with { Tasks = [] }, ["s1"]).Ok);
        Assert.False(GeneralPracticeRules.Validate(Proof with { Tasks = [Proof.Tasks[0], Proof.Tasks[0]] }, ["s1"]).Ok);
        Assert.False(GeneralPracticeRules.Validate(Proof with { Tasks = [Proof.Tasks[0] with { Acceptance = "" }] }, ["s1"]).Ok);
        Assert.False(GeneralPracticeRules.Validate(Proof with { Capability = "manual-project" }, ["s1"]).Ok);
        Assert.False(GeneralPracticeRules.Validate(Proof with { SchemaVersion = 4 }, ["s1"]).Ok);
        Assert.False(GeneralPracticeRules.Validate(Proof with { Tasks = null! }, ["s1"]).Ok);
        var json = JsonSerializer.SerializeToNode(Proof, Wire.Options)!;
        json.AsObject().Remove("schemaVersion"); Assert.False(GeneralPracticeRules.Parse(json.ToJsonString(), ["s1"]).Ok);
        var duplicate = JsonSerializer.Serialize(Proof, Wire.Options).Replace("\"schemaVersion\":5", "\"schemaVersion\":5,\"schemaVersion\":5");
        Assert.False(GeneralPracticeRules.Parse(duplicate, ["s1"]).Ok);
        Assert.False(GeneralPracticeRules.Parse(new string('x', 262145), ["s1"]).Ok);
    }

    [Fact]
    public async Task Export_OnlyHostReadme_NoOverwriteOrExecutableEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "study-p8-practice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "practice.zip"); var exporter = new GeneralPracticeExporter();
            Assert.True((await exporter.ExportAsync(Network, ["s1"], path)).Ok);
            var before = await File.ReadAllBytesAsync(path);
            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = Assert.Single(zip.Entries); Assert.Equal("README.md", entry.FullName);
                using var reader = new StreamReader(entry.Open()); var text = await reader.ReadToEndAsync();
                Assert.Contains("软件不执行操作", text); Assert.Contains(Network.Tasks[0].ExpectedObservation, text);
            }
            Assert.False((await exporter.ExportAsync(Proof, ["s1"], path)).Ok);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.False((await exporter.ExportAsync(Proof, ["s1"], Path.Combine(root, "run.cmd"))).Ok);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(Proof, ["s1"], Path.Combine(root, "cancel.zip"), cancel.Token));
            Assert.False(File.Exists(Path.Combine(root, "cancel.zip")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Strategy_UsesCapabilitiesNotSubjectKeywords()
    {
        Assert.True(StrategyRules.Valid(new("计算机网络", ["theory", "project"], "观察网络行为", ["manual-project"], [], [])));
        Assert.True(StrategyRules.Valid(new("数学证明", ["theory", "exercise"], "推导练习", ["paper-exercise"], [], [])));
        Assert.True(StrategyRules.Valid(new("跨学科主题", ["theory"], "只需理解概念", [], [], [])));
        Assert.False(StrategyRules.Valid(new("任意主题", ["theory"], "不能偷偷执行", ["run-shell"], [], [])));
        Assert.False(StrategyRules.Valid(new("网络", ["exercise"], "缺少能力", [], [], [])));
        Assert.False(StrategyRules.Valid(new("数学", ["theory"], "能力与模式不符", ["paper-exercise"], [], [])));
        Assert.False(StrategyRules.Valid(new("C++", ["project"], "不支持C++工程", ["portable-cpp"], [], [])));
    }
}
