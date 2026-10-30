using System.Text.Json;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Request-time teaching design; no model call, persisted plan rewrite or quality gate.
public static class LessonTeaching
{
    public const string Guidance = """
        教学任务设计v1：围绕本课目标组织讲解，不把知识点标题清单直接扩写成百科摘要。
        把宽泛目标转为可观察的学习产出：学生学完能解释什么、辨别什么条件、完成什么推导/计算/操作；只选适合本课的产出，不擅自扩大学习范围。
        前置课程只是建议顺序，不代表已经掌握；首次使用关键符号、定义或假设时简短说明，已属其他课的内容只补理解本课所需部分。
        可采用“问题—原理—完整示范—改变一个条件—独立练习”的教学顺序，按内容灵活取舍，不要求五个固定标题。
        概念课通过对比/反例说明边界，推导课解释关键步骤依据，计算课明确已知、所求、单位和适用条件；不要每种课都机械套计算题模板。
        完整示范不仅列步骤，还解释为什么选用此方法；适合时只改变一个条件，说明方法或结论哪里变化及哪里保持不变。变式明确标为自拟，不修改原题后冒充原题解答。
        独立练习检验本课目标与方法迁移，不只重复例题数字或问原句；正文不直接公布该练习答案。简明保留必要解释，不重复铺陈或用更多题目凑篇幅。
        比较性结论明确保持不变的条件；教材简化模型标明适用范围，可能/不一定不能改成必然/不可能。模型补充的解释或例子与教材引用、原题事实明确区分。
        以下是教学指导而非输出检查表，无需返回教学任务、自评或contextChecks。没有适合的变式不阻塞生成，不因模板不齐重写整课。
        """;

    public static string Describe(PlanDocument plan, PlannedNode node)
    {
        if (node.Type != "theory") return "";
        var own = (plan.Candidate.KnowledgeCoverage ?? []).Where(m => m.State == "included" && m.NodeKeys.Contains(node.Key)).Select(m => m.KnowledgeKey).ToHashSet();
        var relevant = BookKnowledge.LessonKeys(plan, node);
        var points = (plan.Collections ?? []).SelectMany(c => (c.KnowledgeUnits ?? []).SelectMany(u => u.Points)
            .Where(p => relevant.Contains(p.Key)).Select(p => new { role = ExamReference.IsExam(c) ? "考题分析摘要" : own.Contains(p.Key) ? "本课教材知识摘要" : "必要前置摘要", p.Key, p.Title, p.Detail }));
        return "\n" + Guidance + "\n本课教学任务（摘要是派生分析，具体定义、原题条件和图表以对应原文为依据；冲突保留差异，不擅选版本）：\n" +
            JsonSerializer.Serialize(new { node.Title, learningOutcomes = node.Objectives, plan.Candidate.Background, plan.Candidate.Goal,
                prerequisites = plan.Candidate.Nodes.Where(n => node.PrerequisiteKeys.Contains(n.Key)).Select(n => new { n.Title, n.Objectives }),
                knowledge = points,
                scopeBoundary = plan.Candidate.Nodes.Where(n => n.PrerequisiteKeys.Contains(node.Key)).Select(n => n.Title) }, new JsonSerializerOptions(Wire.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static string SourceRole(PlanDocument plan, PlannedNode node, PlanSource source)
    {
        var collections = (plan.Collections ?? []).Where(c => c.References.Contains(source.Reference)).ToArray();
        if (collections.Any(ExamReference.IsExam)) return "原始考题转录：保留题干、图表、共享条件与小问关系；不是官方答案";
        var own = (plan.Candidate.KnowledgeCoverage ?? []).Where(m => m.State == "included" && m.NodeKeys.Contains(node.Key)).Select(m => m.KnowledgeKey).ToHashSet();
        var relevant = BookKnowledge.LessonKeys(plan, node);
        var points = collections.SelectMany(c => c.KnowledgeUnits ?? []).SelectMany(u => u.Points.Where(p =>
            p.EvidenceIds.Any(id => u.Sources.Any(s => s.Key == id && s.Reference == source.Reference))));
        if (points.Any(p => own.Contains(p.Key))) return "本课教材直接证据";
        if (points.Any(p => relevant.Contains(p.Key))) return "必要前置证据：仅补充理解本课所需内容";
        return own.Count == 0 ? "本课已选固定资料：用途按本课目标判断" : "同页/相邻上下文：用于补足条件，不代表全部需要讲授";
    }

    public static string SourceHeader(PlanDocument plan, PlannedNode node, PlanSource source) =>
        $"[资料用途：{SourceRole(plan, node, source)}]\n[文件={source.Reference.Locator.SourceLabel}；物理页={source.Reference.Locator.Page?.ToString() ?? "无页码"}；块={source.Reference.Locator.Block}]\n";
}
