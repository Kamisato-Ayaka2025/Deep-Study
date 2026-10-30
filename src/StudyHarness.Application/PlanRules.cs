using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudyHarness.Contracts;
using StudyHarness.Domain;

namespace StudyHarness.Application;

public static class PlanRules
{
    public static bool ValidInput(PlanningInput? input) => input is not null &&
        !string.IsNullOrWhiteSpace(input.Request) && input.Request.Length <= 8000 &&
        input.Background?.Length is not > 4000 && input.Goal?.Length is not > 4000 &&
        input.WeeklyMinutes is null or >= 0 and <= 10080 &&
        input.Scope is "Adaptive" or "Auto" or "Math" or "Concepts" or "SimpleC" or "CppAlgorithms" or "OutOfScope" &&
        input.PreferredReferences is not null && input.PreferredReferences.Count <= 99;

    public static Guid NodeId(Guid subject, string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{subject:N}/{key}"))[..16]);
    public static CourseGraph Graph(Guid subject, PlanCandidate plan) => CourseGraph.Create(plan.Nodes.Select((n, i) =>
        new CourseDefinition(NodeId(subject, n.Key), i, n.Type == "quiz" ? CourseKind.Quiz : n.Type == "theory" ? CourseKind.Theory : CourseKind.Lab,
            n.Title, n.Objectives, n.EstimatedMinutes, n.PrerequisiteKeys.Select(k => NodeId(subject, k)).ToArray())));

    public static Result<PlanDocument> Validate(Guid subject, PlanningInput input, PlanCandidate plan,
        IReadOnlyList<PlanSource> sources, TokenUsage? usage, IReadOnlyList<PlanCollection>? collections = null)
    {
        Result<PlanDocument> Bad(string message) => Result<PlanDocument>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, message);
        var adaptive = plan.SchemaVersion == 2;
        if (BookKnowledge.Validate(plan, collections) is { } knowledgeError) return Bad(knowledgeError);
        if (plan.Nodes.Count > (adaptive ? 240 : 80))
            return Bad($"PVAL001：本次计划超过{(adaptive ? 240 : 80)}个节点的技术容量。请明确分阶段规划并保留后续范围，不得压缩后半部分的教学深度来凑上限。");
        if (plan.SchemaVersion is not (1 or 2) || plan.Nodes.Count < 1 || plan.Nodes.Count > (adaptive ? 240 : 80) || plan.Nodes.Any(n => n.Type is not ("theory" or "lab" or "quiz") || n.EstimatedMinutes is < 1 or > 10080) ||
            plan.WeeklyMinutes is < 1 or > 10080 || (adaptive ? plan.Scope != "Adaptive" : plan.Scope is not ("Math" or "Concepts" or "SimpleC" or "CppAlgorithms" or "OutOfScope")))
            return Bad("PVAL001：计划类型、课程数量或每周时间无效。");
        if (adaptive && AdaptivePlanRules.Problems(plan) is { Count: > 0 } problems)
            return Bad(string.Join("；", problems));
        var firstTheory = plan.Nodes.FirstOrDefault(n => n.Type == "theory");
        if (adaptive && firstTheory is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(firstTheory.Title, "资料边界|资料局限|学习路线|课程介绍|用途") &&
            !firstTheory.Objectives.Any(o => System.Text.RegularExpressions.Regex.IsMatch(o, "推导|证明|计算|手算|构造|实现|写出|推演|求解|举例")))
            return Bad("PDEPTH001：首课仅有用途/资料范围目标。请合并到实质知识课，标题及objectives须包含明确的定义与具体计算/推演任务；不要将未选入计划误称为原文未覆盖。");
        if (!adaptive && (plan.Strategy is not null || plan.Coverage is not null || plan.Nodes.Any(n => n.Lab?.Kind is not null)))
            return Bad("PVAL002：旧版计划不得包含新版策略/覆盖/kind字段。");
        if (collections is { Count: > 0 })
        {
            if (!adaptive || collections.Count > 10 || collections.Select(c => c.Manifest.CollectionId).Distinct().Count() != collections.Count ||
                !AdaptivePlanRules.CoversDirectory(plan, collections.SelectMany((c, i) => c.Manifest.Entries.Select(e => AdaptivePlanRules.DirectoryKey(i, e.Key)))))
                return Bad("PVAL003：计划未完整交代冻结目录的覆盖或排除范围。" +
                    AdaptivePlanRules.DirectoryDifference(plan, collections.SelectMany((c, i) => c.Manifest.Entries.Select(e => AdaptivePlanRules.DirectoryKey(i, e.Key)))) +
                    "每个缺失键须按原文明确included/excluded/unavailable，不得猜补状态。修复已有条目而非追加冲突范围；保留所有课程与已有绑定。");
            foreach (var (c, i) in collections.Select((c, i) => (c, i)))
                foreach (var entry in c.Manifest.Entries)
                    if (entry.State is not ("textReady" or "ocrReady") && !entry.QualityFlags.Contains("fetchOnDemand") && plan.Coverage!.Any(e => e.EntryKey == AdaptivePlanRules.DirectoryKey(i, entry.Key) && e.State == "included"))
                        return Bad("PVAL004：尚未取得正文的目录项不能标为已覆盖，请先补取或明确缺口。");
        }
        else if (adaptive && plan.Coverage!.Count != 0) return Bad("PVAL005：没有目录时不得编造覆盖记录。");
        if (input.WeeklyMinutes is { } minutes && minutes != plan.WeeklyMinutes)
            return Bad("PVAL006：模型改变了已填写的weeklyMinutes；请重新拟定，输入无需改动。");
        if (!string.IsNullOrWhiteSpace(input.Background) && input.Background != plan.Background)
            return Bad("PVAL007：模型改变了已填写的background；请重新拟定，输入无需改动。");
        if (!string.IsNullOrWhiteSpace(input.Goal) && input.Goal != plan.Goal)
            return Bad("PVAL008：模型改变了已填写的goal；请重新拟定，输入无需改动。");
        if (input.Scope != "Auto" && input.Scope != plan.Scope)
            return Bad("PVAL009：模型改变了已选择的scope；请核对目标与范围是否冲突，或重新拟定。程序未替换已选范围。");
        if (plan.Defaults.Select(d => d.Field).Distinct().Count() != plan.Defaults.Count)
            return Bad("PVAL010：defaults包含重复字段；模型需重新拟定，不应重复标记默认项。");
        foreach (var d in plan.Defaults)
        {
            var supplied = d.Field switch
            {
                "background" => !string.IsNullOrWhiteSpace(input.Background),
                "goal" => !string.IsNullOrWhiteSpace(input.Goal),
                "weeklyMinutes" => input.WeeklyMinutes is not null,
                _ => false
            };
            var expected = d.Field switch
            {
                "background" => plan.Background,
                "goal" => plan.Goal,
                "weeklyMinutes" => plan.WeeklyMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null
            };
            if (expected is null) return Bad("PVAL011：defaults含不支持的字段；仅允许基础、目标和每周时间。");
            if (supplied) return Bad($"PDEF001：模型将已填写字段 {d.Field} 错标为默认值；输入无需改动，请重新拟定。");
            if (d.Value != expected) return Bad($"PDEF002：默认项 {d.Field} 的value与计划对应字段不一致；请重新拟定。");
        }
        var keys = sources.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        if (plan.SourceKeys.Count == 0 || plan.SourceKeys.Any(k => !keys.Contains(k)))
            return Bad("PVAL012：计划必须引用本次实际发送的资料，不得编造来源。");
        if (plan.Scope == "OutOfScope" && (plan.Limitations.Count == 0 || plan.Clarifications.Count == 0))
            return Bad("PVAL013：范围外实践必须说明限制并澄清目标。");
        var labs = plan.Nodes.Where(n => n.Type == "lab").ToArray();
        var practical = adaptive ? plan.Strategy!.MaterialCapabilities.Count > 0 : plan.Scope is "SimpleC" or "CppAlgorithms";
        if (!practical && labs.Length != 0)
            return Bad("PVAL014：实验支持简单C及C++算法竞赛；纯数学和纯概念计划应为理论课。");
        var cpp = plan.Scope == "CppAlgorithms" || adaptive && plan.Strategy!.MaterialCapabilities.Contains("portable-cpp");
        if (cpp)
        {
            var algorithms = plan.Algorithms;
            if (algorithms is null || algorithms.Count is < 1 or > 80 ||
                algorithms.Any(a => a is null || !System.Text.RegularExpressions.Regex.IsMatch(a.Key ?? "", @"^a[1-9][0-9]{0,3}$") || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 200) ||
                algorithms.Select(a => a.Key).Distinct().Count() != algorithms.Count ||
                algorithms.Select(a => a.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != algorithms.Count)
                return Bad("PVAL015：C++计划必须列出唯一的算法目录（key/name）。");
            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                var n = plan.Nodes[i];
                if (n.AlgorithmKeys is null || n.AlgorithmKeys.Count > 80 || n.AlgorithmKeys.Distinct().Count() != n.AlgorithmKeys.Count ||
                    n.AlgorithmKeys.Any(k => !algorithms.Any(a => a.Key == k)))
                    return Bad($"PVAL016 nodes[{i}].algorithmKeys：映射缺失、重复或引用未知算法；所有节点必须提供数组，无映射的理论课可填[]。");
                if (n.Lab?.Toolchain == "portable-cpp" && n.AlgorithmKeys.Count == 0)
                    return Bad($"PALG001 nodes[{i}].algorithmKeys：portable-cpp练习不可为空映射；包括输入输出、循环、数组等基础练习。为本题真实练习的方法建立algorithms条目及对应理论依赖，不得删除题目、改成项目或填无关算法来绕过。");
            }
            foreach (var algorithm in algorithms)
            {
                var theory = plan.Nodes.Where(n => n.Type == "theory" && n.AlgorithmKeys!.Contains(algorithm.Key)).ToArray();
                var exercises = labs.Where(n => n.Lab?.Toolchain == "portable-cpp" && n.AlgorithmKeys!.Contains(algorithm.Key)).ToArray();
                if (theory.Length == 0) return Bad($"PALG002 algorithms[{plan.Algorithms!.ToList().IndexOf(algorithm)}]：缺少theory节点的algorithmKeys映射；每个算法至少一节理论课，实验可选，将实际对应节点映射到此算法，不能理论全填[]，不得删除算法规避。");
                if (exercises.Length > 0 && !exercises.Any(n => theory.Any(t => DependsOn(n, t.Key, plan.Nodes, []))))
                    return Bad($"PALG003 algorithms[{plan.Algorithms!.ToList().IndexOf(algorithm)}]：对应练习必须直接或间接依赖本算法理论课。");
            }
        }
        else if (plan.Algorithms is { Count: > 0 } || plan.Nodes.Any(n => n.AlgorithmKeys is { Count: > 0 }))
            return Bad("PVAL019：算法映射仅适用于CppAlgorithms计划。");
        var sourceProblems = plan.Nodes.Select((node, index) => (node, index))
            .Where(p => p.node.Lab is not null)
            .Select(p => (p.index, Missing: p.node.Lab!.SourceKeys.Where(k => !keys.Contains(k) || !plan.SourceKeys.Contains(k)).Distinct().ToArray()))
            .Where(p => p.Missing.Length > 0).ToArray();
        if (sourceProblems.Length > 0)
            return Bad("PVAL021：练习来源问题共" + sourceProblems.Length + "项：" + string.Join("；", sourceProblems.Take(40).Select(p =>
                $"nodes[{p.index}].lab.sourceKeys：{string.Join(",", p.Missing.Take(12).Select(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"^s[1-9][0-9]?$") ? k : "<无效来源键>"))}")) +
                "。必须引用本次实际提供且列入顶层sourceKeys的来源；不得编造来源。一次修正所有列出的练习。");
        foreach (var node in plan.Nodes)
        {
            if (node.Type is ("theory" or "quiz") && node.Lab is not null || node.Type == "lab" && node.Lab is null)
                return Bad("PVAL020：实验安排与课程类型不匹配。");
            if (node.Lab is not { } lab) continue;
            if (lab.Mode is not ("independent" or "continuous") ||
                (adaptive ? !plan.Strategy!.MaterialCapabilities.Contains(lab.Toolchain) : plan.Scope == "CppAlgorithms" ? lab.Toolchain != "portable-cpp" : lab.Toolchain is not ("portable-c" or "msvc" or "mingw")) ||
                lab.SourceKeys.Count == 0 || lab.SourceKeys.Any(k => !keys.Contains(k) || !plan.SourceKeys.Contains(k)) ||
                string.IsNullOrWhiteSpace(lab.Rationale)) return Bad("PVAL021：实验必须说明有效素材、适配理由和与计划一致的语言范围。");
            if (sources.Any(s => s.UserPreferred) && !lab.SourceKeys.Any(k => sources.Any(s => s.Key == k && s.UserPreferred)))
                return Bad("PVAL022：已指定的项目未被采用；请在确认前调整素材，不能静默替换。");
            if (lab.Mode == "independent" && lab.PreviousLabKey is not null) return Bad("PVAL023：独立实验不可继承前课。");
            if (lab.PreviousLabKey is { } previous)
            {
                // An independent starting lab still produces the workspace inherited by its successor.
                var predecessor = plan.Nodes.FirstOrDefault(n => n.Key == previous);
                if (predecessor?.Type != "lab" || predecessor.Lab is null)
                    return Bad("PLAB001：previousLabKey必须是存在的实验课。");
                if (!node.PrerequisiteKeys.Contains(previous))
                    return Bad("PLAB002：必须将继承来源列入直接前置课程。");
                if (predecessor.Lab.Toolchain != lab.Toolchain)
                    return Bad("PLAB003：实验与继承来源必须使用相同工具链。");
            }
        }
        if (plan.Nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != plan.Nodes.Count)
            return Bad("PVAL024：课程标识重复。");
        CourseGraph graph;
        try { graph = Graph(subject, plan); }
        catch (ArgumentException) { return Bad("PVAL025：课程图含环、自依赖、缺失节点或无效课程字段。"); }
        if (plan.Nodes.Any(n => n.EstimatedMinutes > plan.WeeklyMinutes))
            return Bad("PVAL026：单课时长超过每周时间，请拆分课程或调整时间后重新拟定。");
        var weeks = new List<PlanWeek>(); var pending = new List<string>(); var total = 0;
        foreach (var node in plan.Nodes.OrderBy(n => graph.Layers[NodeId(subject, n.Key)]))
        {
            if (total + node.EstimatedMinutes > plan.WeeklyMinutes)
            {
                weeks.Add(new(weeks.Count + 1, pending.ToArray(), total)); pending.Clear(); total = 0;
            }
            pending.Add(node.Key); total += node.EstimatedMinutes;
        }
        if (pending.Count > 0) weeks.Add(new(weeks.Count + 1, pending.ToArray(), total));
        var used = sources.Where(s => plan.SourceKeys.Contains(s.Key)).ToArray();
        var hash = collections is { Count: > 0 } ? Hash(new { subject, input, candidate = plan, sources = used, weeks, collections }) : Hash(new { subject, input, candidate = plan, sources = used, weeks });
        return Result<PlanDocument>.Success(new(plan, used, weeks, hash, usage, Collections: collections is { Count: > 0 } ? collections : null));
    }

    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Options)));

    private static bool DependsOn(PlannedNode node, string key, IReadOnlyList<PlannedNode> nodes, HashSet<string> visited)
    {
        if (!visited.Add(node.Key)) return false;
        return node.PrerequisiteKeys.Contains(key) || node.PrerequisiteKeys.Any(k =>
            nodes.FirstOrDefault(n => n.Key == k) is { } parent && DependsOn(parent, key, nodes, visited));
    }

    public static string AlgorithmCoverage(PlanCandidate plan) => string.Join("\n", (plan.Algorithms ?? []).Select(a =>
        $"{a.Key} · {a.Name} → 理论：{string.Join(", ", plan.Nodes.Where(n => n.Type == "theory" && (n.AlgorithmKeys?.Contains(a.Key) ?? false)).Select(n => n.Key))} → 实验题：{string.Join(", ", plan.Nodes.Where(n => n.Type == "lab" && (n.AlgorithmKeys?.Contains(a.Key) ?? false)).Select(n => n.Key))}"));
}
