using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Describes actual frozen page bindings, not an inferred chapter structure or quality score.
public static class PlanTeachingCoverage
{
    public static string Describe(PlanDocument document)
    {
        var plan = document.Candidate;
        var lines = new List<string>();
        foreach (var (collection, index) in (document.Collections ?? []).Select((c, i) => (c, i)))
        {
            var prefix = $"d{index + 1}/";
            var entries = collection.Manifest.Entries.ToDictionary(e => prefix + e.Key);
            var groups = (plan.Coverage ?? []).Where(e => e.State == "included" && entries.ContainsKey(e.EntryKey))
                .GroupBy(e => string.Join("\n", e.NodeKeys.Order(StringComparer.Ordinal)));
            foreach (var group in groups.OrderBy(g => g.Min(e => int.TryParse(entries[e.EntryKey].Locator, out var p) ? p : int.MaxValue)))
            {
                var keys = group.First().NodeKeys.ToHashSet(StringComparer.Ordinal);
                var nodes = plan.Nodes.Where(n => keys.Contains(n.Key)).ToArray();
                var pages = group.Select(e => entries[e.EntryKey].Locator).Select(v => int.TryParse(v, out var p) ? (int?)p : null).ToArray();
                var range = pages.All(p => p.HasValue) ? $"资料页{pages.Min()}–{pages.Max()}（{pages.Length}页）" : $"{group.Count()}个资料条目";
                lines.Add($"资料{index + 1} · {range}：理论{nodes.Count(n => n.Type == "theory")}课、实践{nodes.Count(n => n.Type == "lab")}课；{string.Join("、", nodes.Select(n => n.Key + " " + n.Title))}");
            }
        }
        return $"共{plan.Nodes.Count}课，预计{plan.Nodes.Sum(n => n.EstimatedMinutes)}分钟、{document.Weeks.Count}周。每周时间只安排学习节奏，不限制总课数。\n" +
            (lines.Count == 0 ? "本计划无教材页绑定；请按完整课程目标核对教学深度。" : "以下按相同教学绑定汇总，不代表已验证讲解完整；请对照教材小节检查后半部分是否过粗。\n" + string.Join("\n", lines));
    }
}
