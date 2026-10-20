using System.Text.RegularExpressions;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface ISourceSelectionHistory
{
    Task SaveSelectionAsync(Guid subject, LessonSourceSelection selection, CancellationToken ct);
    Task<LessonSourceSelection?> SelectionAsync(Guid subject, string nodeKey, CancellationToken ct = default);
}

public static class LessonSourceSelector
{
    private static string SearchText(string text) => Regex.Replace(text, @"\\([\\`*_{}\[\]()#+.!<>:;,/&=~-])", "$1");
    private static string Heading(string text)
    {
        var first = SearchText(text).TrimStart().Split('\n')[0].Trim();
        var match = Regex.Match(first, @"^#{1,6}\s+(.+)$");
        return match.Success ? match.Groups[1].Value : "";
    }
    // Lexical and structure retrieval; no claim of embedding-based semantic search.
    public static IReadOnlySet<string> Terms(string query)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(SearchText(query), @"[a-zA-Z0-9_+]+|[\p{IsCJKUnifiedIdeographs}]+"))
        {
            if (m.Value[0] <= 127) { if (m.Length > 1) terms.Add(m.Value); }
            else for (var i = 0; i < m.Length - 1; i++) terms.Add(m.Value.Substring(i, 2));
        }
        return terms;
    }
    public static bool HasBody(string text) => text.Split('\n').Any(line => !string.IsNullOrWhiteSpace(line) && !Regex.IsMatch(line, @"^\s*(?:\\?#){1,6}\s+|^\s*\[[^\]]+\]\([^)]*\)\s*$"));
    public static int Score(string text, IReadOnlySet<string> terms)
    {
        var searchable = SearchText(text);
        return terms.Count(t => searchable.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
    public static IReadOnlyList<PlanSource> Choose(PlannedNode node, IReadOnlyList<(PlanSource Source, string Text)> candidates)
    {
        var chosen = new HashSet<PlanSource>();
        // Lab source keys are explicit frozen constraints. Theory is selected per objective and prerequisite.
        foreach (var objective in node.Objectives.Prepend(node.Title))
        {
            var terms = Terms(objective);
            // The page title is shared by every fragment and cannot establish relevance of its body.
            var ranked = candidates.Where(c => HasBody(c.Text)).Select(c => (c.Source, Score: Score(c.Text, terms) + 2 * Score(Heading(c.Text), terms)))
                .OrderByDescending(c => c.Score).ToArray(); // stable source order, not lexical s1/s10/s2 order
            if (ranked.FirstOrDefault().Score > 0) chosen.Add(ranked[0].Source);
            // Recall explanatory anchors independently: a long overview can otherwise outscore
            // the short definition of a named concept (e.g. include) needed by the same objective.
            var anchors = candidates.Where(c => HasBody(c.Text)).Select(c => (c.Source, Heading: Heading(c.Text)))
                .Where(c => c.Heading.Length > 0)
                .Select(c => (c.Source, Score: Score(c.Heading, terms)))
                .Where(c => c.Score > 0).OrderByDescending(c => c.Score).Take(2);
            foreach (var anchor in anchors) chosen.Add(anchor.Source);
        }
        // Keep directly attached conditions, proof/solution and ordered continuation pieces from the same page.
        foreach (var group in candidates.GroupBy(c => c.Source.Reference.Locator.SourceLabel))
        {
            var items = group.ToArray();
            // Complete each selected local heading unit, including fenced examples split into
            // separate chunks. Stop at the next heading; never fill with the rest of the page.
            var seeds = chosen.ToHashSet();
            for (var i = 0; i < items.Length; i++)
            {
                if (!seeds.Contains(items[i].Source)) continue;
                var start = i;
                while (start > 0 && Heading(items[start].Text).Length == 0) start--;
                if (Heading(items[start].Text).Length == 0) continue;
                for (var j = start; j < items.Length && (j == start || Heading(items[j].Text).Length == 0); j++)
                    chosen.Add(items[j].Source);
            }
            for (var i = 0; i < items.Length; i++)
            {
                if (!chosen.Contains(items[i].Source)) continue;
                if (i > 0 && SemanticChunker.Kind(items[i - 1].Text) is "definition" or "theorem" or "example") chosen.Add(items[i - 1].Source);
                for (var j = i + 1; j < items.Length && SemanticChunker.Kind(items[j].Text) is "proof" or "solution"; j++) chosen.Add(items[j].Source);
            }
        }
        return candidates.Where(c => chosen.Contains(c.Source)).Select(c => c.Source).ToArray();
    }
    public static LessonSourceSelection Describe(PlanDocument plan, PlannedNode node, IReadOnlyList<PlanSource> sources, int characters) =>
        new(plan.Hash, node.Key, TrustedSources.Version, SemanticChunker.IndexVersion, SemanticChunker.Version, sources,
            [CourseMaterials.MissingBinding(plan, node) ? "旧计划遗漏本课绑定：在已确认included条目及冻结原文内恢复选材，不修改计划hash" : "使用本课已确认绑定",
                "本课目标与片段正文/标题匹配；补充目标标题解释与同节代码/条件/证明/解答；不填满剩余预算", "已核验的父级章节导览仅作导航；对应子章节正文仍须完整取得，导览不作教学证据外发",
                sources.Count == 0 ? "无外部原文：仅允许已识别的稳定基础知识，模型讲解不冒充来源" : "跨章内容仅限已冻结来源；允许稳定基础知识补充，动态规则仍须正文；匹配不代表事实核验通过"],
            sources.Count == 0 ? node.Objectives : [], plan.Sources.Except(sources).Select(s => s.Key).ToArray(), characters,
            checked(characters * 2)); // conservative bound; actual whole request is separately checked by ModelEgress
}
