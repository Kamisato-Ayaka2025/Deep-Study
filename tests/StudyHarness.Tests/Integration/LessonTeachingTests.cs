using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class LessonTeachingTests
{
    private static (PlanDocument Plan, PlannedNode Node, PlanSource[] Sources) Fixture(string? kind = "pdf")
    {
        var library = Guid.NewGuid();
        var sources = Enumerable.Range(1, 7).Select(i => new PlanSource("b" + i,
            new(library, Guid.NewGuid(), Guid.NewGuid(), new('A', 64), new(kind == "presentation" ? "教材.pptx" : "教材.pdf", i <= 5 ? 1 : i == 6 ? 2 : 3, "图表及正文", "block-" + i)), false)).ToArray();
        var node = new PlannedNode("n2", "地址容量", "theory", ["解释地址容量并计算/24地址数量"], ["n1"], 30, null);
        var candidate = new PlanCandidate("计网", "零基础", "期末复习", 120, "Adaptive", [], [], [], ["b1"],
            [new("n1", "二进制前置", "theory", ["理解幂"], [], 20, null), node], 2,
            KnowledgeCoverage: [new("k1", "included", ["n2"], null, ["k2"]), new("k2", "included", ["n1"], null, [])]);
        var unit = new BookKnowledgeUnit("u1", "fp", sources,
            [new("k1", "地址数", "原题共享条件需要保留", ["b1", "b6"]), new("k2", "幂的含义", "前置摘要", ["b7"]), new("unrelated", "不相关知识", "不应装入任务", ["b4"])], []);
        var collection = new PlanCollection(library, new(Guid.NewGuid(), "v1", "directory", true, []), sources.Select(s => s.Reference).ToArray(), KnowledgeUnits: [unit], SourceKind: kind);
        return (new(candidate, sources, [], "frozen-hash", null, Collections: [collection]), node, sources);
    }

    [Fact]
    public void Brief_SeparatesDirectPrerequisiteAndExamAnalysis_DoesNotMutateFrozenPlan()
    {
        var (plan, node, sources) = Fixture(); var before = JsonSerializer.Serialize(plan, Wire.Options);
        var brief = LessonTeaching.Describe(plan, node);
        Assert.Contains("本课教材知识摘要", brief); Assert.Contains("必要前置摘要", brief);
        Assert.Contains("二进制前置", brief); Assert.DoesNotContain("不应装入任务", brief);
        Assert.Contains("不代表已经掌握", brief); Assert.Contains("改变一个条件", brief);
        Assert.Equal(before, JsonSerializer.Serialize(plan, Wire.Options));
        Assert.Contains("直接证据", LessonTeaching.SourceRole(plan, node, sources[0]));
        Assert.Contains("必要前置证据", LessonTeaching.SourceRole(plan, node, sources[6]));
        Assert.Contains("同页/相邻", LessonTeaching.SourceRole(plan, node, sources[3]));
        var exam = plan with { Collections = [plan.Collections![0] with { Purpose = "exam" }] };
        Assert.Contains("考题分析摘要", LessonTeaching.Describe(exam, node));
        Assert.Contains("原始考题转录", LessonTeaching.SourceHeader(exam, node, sources[0]));
        Assert.Contains("物理页=1", LessonTeaching.SourceHeader(exam, node, sources[0]));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("presentation")]
    [InlineData(null)]
    public void Sources_KeepDistantSamePageFigureAndCrossPageConditions(string? kind)
    {
        var (plan, node, sources) = Fixture(kind);
        var selected = BookKnowledge.LessonSources(plan, node);
        Assert.Equal(sources.Select(s => s.Reference), selected.Select(s => s.Reference));
        Assert.Contains(selected, s => s.Key == "b4"); // Distant figure on the first evidence page, not just immediate neighbour.
        Assert.Contains(selected, s => s.Key == "b6"); // Continuation of the same question on page 2.
    }

    [Fact]
    public void LegacyAndKnowledgeOnlyLessons_GetTaskWithoutInventedSource_QuizHasNoTeachingTemplate()
    {
        var (plan, node, sources) = Fixture();
        var legacy = plan with { Collections = null, Candidate = plan.Candidate with { SchemaVersion = 1, KnowledgeCoverage = null } };
        Assert.Contains(node.Objectives[0], LessonTeaching.Describe(legacy, node));
        Assert.DoesNotContain("原题共享条件需要保留", LessonTeaching.Describe(legacy, node));
        Assert.Contains("已选固定资料", LessonTeaching.SourceRole(legacy, node, sources[0]));
        Assert.Equal("", LessonTeaching.Describe(plan, node with { Type = "quiz" }));
        Assert.Contains("单卷出现次数不是考试概率", ExamReference.Guidance);
    }
}
