using StudyHarness.Contracts;

namespace StudyHarness.Application;

public static class AdaptivePlanRules
{
    public static string DirectoryKey(int collectionIndex, string entryKey) => $"d{collectionIndex + 1}/{entryKey}";
    public static bool Valid(PlanCandidate plan) => Problems(plan).Count == 0;
    public static IReadOnlyList<string> Problems(PlanCandidate plan)
    {
        var errors = new List<string>();
        void Add(string code, string path, string message) { if (errors.Count < 8) errors.Add($"{code} {path}：{message}"); }
        if (!StrategyRules.Valid(plan.Strategy)) Add("PSTR001", "strategy", "策略字段或模式与能力组合不合法");
        if (plan.Strategy is { } strategy)
        {
            if (!plan.Nodes.Select(n => n.Type == "theory" ? "theory" : n.Lab?.Kind).ToHashSet().SetEquals(strategy.TeachingModes))
                Add("PSTR002", "strategy.teachingModes", "必须等于实际节点的教学模式集合");
            if (!plan.Nodes.Where(n => n.Type == "lab").Select(n => n.Lab?.Toolchain).ToHashSet().SetEquals(strategy.MaterialCapabilities))
                Add("PSTR003", "strategy.materialCapabilities", "必须等于实际实践节点的能力集合");
        }
        for (var i = 0; i < plan.Nodes.Count; i++)
        {
            var node = plan.Nodes[i];
            if (node.Type != "lab") continue;
            if (node.Lab is not { } lab || lab.Kind is not ("exercise" or "project"))
                Add("PSTR004", $"nodes[{i}].lab.kind", "实践必须声明exercise或project");
            else if (lab.Toolchain is "paper-exercise" or "portable-cpp" && lab.Kind != "exercise" || lab.Toolchain == "manual-project" && lab.Kind != "project")
                Add("PSTR005", $"nodes[{i}].lab", "paper-exercise/portable-cpp必须是exercise，manual-project必须是project");
        }
        if (plan.Coverage is null || plan.Coverage.Count > 10000) Add("PCOV001", "coverage", "覆盖数组缺失或超限");
        else
        {
            var seen = new HashSet<string>();
            for (var i = 0; i < plan.Coverage.Count; i++)
            {
                var e = plan.Coverage[i]; var path = $"coverage[{i}]";
                if (e is null || string.IsNullOrWhiteSpace(e.EntryKey) || e.EntryKey.Length > 200) { Add("PCOV002", path, "目录键为空或超限"); continue; }
                if (!seen.Add(e.EntryKey)) Add("PCOV003", path + ".entryKey", "目录键重复");
                if (e.NodeKeys is null || e.NodeKeys.Distinct().Count() != e.NodeKeys.Count || e.NodeKeys.Any(k => !plan.Nodes.Any(n => n.Key == k)))
                    Add("PCOV004", path + ".nodeKeys", "节点引用为空、重复或不存在");
                if (e.State == "included")
                {
                    if (e.NodeKeys is not { Count: > 0 } || e.Reason is not null && (string.IsNullOrWhiteSpace(e.Reason) || e.Reason.Length > 2000))
                        Add("PCOV005", path, "included必须绑定实际课程，reason若提供须非空且不超限");
                }
                else if (e.State is not ("excluded" or "unavailable") || e.NodeKeys is not { Count: 0 } || string.IsNullOrWhiteSpace(e.Reason) || e.Reason.Length > 2000)
                    Add("PCOV006", path, "excluded/unavailable必须有理由且nodeKeys为空");
            }
        }
        return errors;
    }

    public static bool CoversDirectory(PlanCandidate plan, IEnumerable<string> entryKeys) =>
        plan.Coverage is not null && plan.Coverage.Select(e => e.EntryKey).ToHashSet(StringComparer.Ordinal).SetEquals(entryKeys);
}
