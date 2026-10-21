using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Unit;

public sealed class HybridKnowledgeTests
{
    private static PlannedNode Node(string objective) => new("n2", "C++程序结构", "theory", [objective], [], 30, null);
    [Theory]
    [InlineData("解释整数加法和变量赋值", true)]
    [InlineData("解释NOIP规定的允许语言", false)]
    [InlineData("解释2026年C++竞赛要求", false)]
    [InlineData("使用指定教材中的矩阵原式", false)]
    [InlineData("解释最新C++版本行为", false)]
    [InlineData("确定药品剂量", false)]
    public void KnowledgeOnly_IsConservative(string objective, bool allowed)
    {
        Assert.Equal(allowed, TheoryKnowledgePolicy.CanUseKnowledgeOnly(Node(objective)));
        Assert.Equal(allowed, TheoryKnowledgePolicy.MissingEvidence(Node(objective), []) is null);
    }
    [Fact]
    public void DynamicObjective_RequiresRelevantBody_NotJustAnySource()
    {
        var node = Node("解释NOIP中C++为唯一允许语言的规定");
        Assert.NotNull(TheoryKnowledgePolicy.MissingEvidence(node, [new("s1", 0, ModelSourceKind.OriginalMaterial, "整数加法中两个整数相加得到整数。", "资料")]));
        Assert.Null(TheoryKnowledgePolicy.MissingEvidence(node, [new("s1", 0, ModelSourceKind.OriginalMaterial, "NOIP规定C++为唯一允许语言。本段说明该比赛规则。", "资料")]));
    }
    [Fact]
    public void Schema4_EmptySourcesAllowed_ButFabricatedCitationAndMissingQuizRejected()
    {
        var parser = new TheoryLessonParser();
        var lesson = TheoryFixtureProvider.NewLesson("s1") with { SchemaVersion = 4, SourceKeys = [] };
        Assert.True(parser.Parse(JsonSerializer.Serialize(lesson, Wire.Options), []).Ok);
        Assert.False(parser.Parse(JsonSerializer.Serialize(lesson with { SchemaVersion = 3 }, Wire.Options), []).Ok);
        Assert.False(parser.Parse(JsonSerializer.Serialize(lesson with { SourceKeys = ["invented"] }, Wire.Options), []).Ok);
        Assert.False(parser.Parse(JsonSerializer.Serialize(lesson with { Questions = [] }, Wire.Options), []).Ok);
        Assert.False(parser.Parse(JsonSerializer.Serialize(lesson with
        {
            Questions = lesson.Questions.Select(q => q with { Options = q.Options.Take(2).ToArray() }).ToArray()
        }, Wire.Options), []).Ok);
        Assert.Contains("未引用外部资料", TheoryKnowledgePolicy.Label(lesson).Blocks[0].Text);
    }
    [Fact]
    public void Recovery_UsesIncludedEntriesOnly_AndPreservesConfirmedHash()
    {
        var node = Node("使用C++变量和整数输入输出");
        var manifest = new CoverageManifest(Guid.NewGuid(), "v1", new string('A', 64), true,
            [new("body", "https://example.org/cpp", "C++变量和输入输出", "ready", null, null, []),
             new("excluded", "https://example.org/other", "C++变量和整数输入输出", "ready", null, null, [])]);
        var collection = new PlanCollection(Guid.NewGuid(), manifest, []);
        var candidate = new PlanCandidate("基础", "初学", "目标", 120, "Math", [], [], [], [],
            [node], 2, Coverage: [new("d1/body", "included", ["n1"], ""), new("d1/excluded", "excluded", [], "用户排除")]);
        var plan = new PlanDocument(candidate, [], [], "original", null, Collections: [collection]);
        Assert.True(CourseMaterials.MissingBinding(plan, node));
        Assert.Contains("n2",CourseMaterials.MissingBindingError(plan));
        Assert.Null(CourseMaterials.MissingBindingError(plan with { Candidate=candidate with { Coverage=[new("d1/body","included",["n2"],null)] } }));
        Assert.Equal(["body"], CourseMaterials.Entries(plan, node, collection, 0));
        Assert.Equal("original", plan.Hash);
        Assert.DoesNotContain(candidate.Coverage!, e => e.NodeKeys.Contains("n2"));
    }
}
